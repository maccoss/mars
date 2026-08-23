# Open questions

Things deliberately left undone, with enough context to pick up cold. Each says what was
measured, what is unresolved, and what would settle it.

## Generalizing a model across datasets

**The goal:** train once on a well-characterized run and apply that model broadly to new
data from the same instrument platform, rather than fitting a fresh model per cohort.

This is the most valuable open question here, and MARS is already built for it - `mars apply`
exists, the model file carries its feature list and acquisition-time offset, and
cross-validation already reports the number that matters for it. What has never been done is
the experiment.

What is known so far:

- Cross-validation estimates within-cohort generalization: a model scored on peptides it did
  not train on. On the reference Stellar run that costs 0.0014 Th against a corrected error
  of 0.0446 Th, about 3%.
- That is **not** the same question. Held-out peptides from the same run share its
  instrument state, its acquisition window, its space-charge conditions. A different run does
  not.
- The data-poor 400-500 window shows a 20% gap where the data-rich 600-700 window shows 3%,
  so the estimate is sensitive to how much the fit had to work with.

What would settle it, roughly in order:

1. **Train on run A, apply to run B, re-match and measure.** The infrastructure is all
   there: `mars calibrate --no-recalibrate` on A, `mars apply --model` to B, then `mars qc` on
   the corrected B against the same library. Compare against fitting B directly.
2. **Vary the gap between A and B**: same plate, same day, different day, different column,
   different instrument of the same model. The interesting output is where transfer stops
   working, not whether it works at all.
3. **Watch `absolute_time` specifically.** It is re-based per fit, so a model carries A's
   time origin. Transfer either has to re-base against B or drop the feature. This is the
   most likely thing to break quietly.
4. **Consider what "same platform" means for the feature set.** A model trained with RF
   temperature features cannot be applied to a run without those logs; loading fails cleanly
   rather than silently, which is correct, but it constrains what a broadly applicable model
   can use.

If transfer works, the practical payoff is large: no library required for routine runs, and
a correction that does not depend on how many peptides happened to be identified.

## Whether a robust loss beats trimming on more data

`--robust trim` is the default and `--robust huber` is available. The measurement behind that
choice is thinner than it first looked:

| | 600-700 window | 400-500 window |
|---|---|---|
| `trim` | 0.0442 Th | 0.0505 Th |
| `huber` | 0.0443 Th | 0.0518 Th |
| *fold-to-fold spread* | *0.0005 Th* | *0.0021 Th* |

On the larger window the difference is one part in four thousand - noise. On the smaller one
trim is ahead, but by less than the spread between folds. Trim is the safer default, not a
demonstrated winner.

The mechanism suggests Huber should do relatively better with more data: it errs by leaving a
mislabelled row about 79% of its weight, which costs most when there is little real signal to
outvote it. Worth re-running on a full cohort, and on Astral data, before treating the
default as settled. The Astral run is a particularly good test of this, at 1.4 million rows
over 81 thousand peptides - an order of magnitude more of both than the Stellar windows the
current default was chosen on. See [model.md](model.md#why-trimming-rather-than-a-robust-loss).

## A redescending weight

The untried middle between trimming and Huber. Tukey's biweight,
`w = (1 - (r/c)^2)^2` for `|r| <= c` and `0` beyond, goes to exactly zero past a multiple of
the threshold instead of decaying as `1/|r|`. That would soften the boundary - no cliff for a
row to sit astride - while still eliminating the far tail, which is the property trimming has
and Huber lacks.

About ten lines in `MzCalibrator.TrainRobust`, plus a decision about how `--robust-sigma`
should scale for it: Tukey down-weights *within* the threshold too, so the conventional
constant is around 4.685 sigma rather than 3.

## A native Huber objective in Osprey.ML

The current `--robust huber` reaches Huber by reweighting and refitting, which is exact for
the gradient but applies the robustness once rather than at every boosting round. A real
objective upstream would re-clip each round as the residuals shrink, and would avoid the
second full pass.

Only worth doing if the reweighted version proves valuable first. It is a pwiz change with
the same bit-identity discipline PR #4595 established, and should follow that PR rather than
stack on it.

## Hyperparameter tuning

Settled for now: **do not**. A sweep of 13 configurations from 50 trees at depth 4 to 400 at
depth 8 leaves out-of-fold MAD flat at 0.0445-0.0449 while in-sample drops from 0.0446 to
0.0347, and the largest configuration is the worst out-of-fold. See
[model.md](model.md#hyperparameters).

Worth revisiting only if the error floor moves - if the mismatched-peak population is dealt
with better, or if a transferred model turns out to be capacity-limited rather than
noise-limited.

## The Python CLI's import cost

`mars/cli.py` imports `__version__` from the package root, which runs
`mars/__init__.py` and so eagerly imports the submodules and their dependencies on
every invocation - including `mars --version`. Reading the distribution metadata
directly would avoid both the side effects and the startup cost.

Raised by the Copilot review on PR #9 and **not applied**: the Python implementation is
frozen to bug fixes, and startup cost is not a bug. Worth doing only if that track is
unfrozen; if it is retired as planned, this closes with it.

## Reading vendor RAW directly, via pwiz-sharp

**Measured, decision pending.** [ProteoWizard PR #4178](https://github.com/ProteoWizard/pwiz/pull/4178)
ports the ProteoWizard core to .NET 8, including the Thermo `.raw` reader and the mzML
writer. If MARS used it, the workflow would go from `RAW -> msconvert -> mzML -> MARS` to
`RAW -> MARS`.

### What was tried

A shallow sparse clone of `chambem2/pwiz-sharp` (44 MB), building
`pwiz/src/Vendor/Thermo/Thermo.csproj` with `-p:IAgreeToVendorLicenses=true`, then a throwaway
probe against a 4.9 GB Astral run (`Ast_20240220_S10_26.raw`, 121,290 spectra).

**It works, and it gives MARS everything it needs.** Every field the matcher reads is present
on the pwiz `Spectrum`: ms level, scan start time, ion injection time, isolation window target
with lower and upper offsets, total ion current, the Thermo filter string, and the m/z and
intensity arrays. Injection time and isolation window were present on 120,327 of 120,327 MS2
spectra. Opening the file costs 1.6 s, because the reader is lazy and only the header is read.

The run declares **two instrument configurations** - `IC1` quadrupole + orbitrap, `IC2`
quadrupole + Astral analyzer - which is the same hybrid layout the mzML analyzer detection
handles, reached the same way. Detection would carry over to RAW input unchanged.

### What argues against it

**Reading RAW is not faster, and does not thread.** Full read with binary data:

| Threads | Wall | Throughput |
|---:|---:|---:|
| 1 | 85.8 s | 3.38 M peaks/s |
| 2 | 108.5 s | 2.67 M peaks/s |
| 4 | 72.5 s | 4.00 M peaks/s |
| 8 | 72.5 s | 4.00 M peaks/s |
| 12 | 72.9 s | 3.98 M peaks/s |

Flat from four threads on, with one reader handle per worker and striped indices. For scale,
MARS's whole match pass over a comparable Astral mzML is 41 s - a different acquisition, so
not a like-for-like comparison, but enough to say RAW reading is not the faster path. The win
would be removing the conversion and its ~5 GB intermediate, not the read itself.

**A Thermo-only build drags a native Windows DLL.** `Thermo.csproj` references
`Analysis.csproj`, which references `Waters.csproj`, which stages `MassLynxRaw.dll` - a
Windows x86-64 native PE - into the output. Nothing in the Thermo reader touches Waters, so
this looks vestigial upstream, but MARS ships `linux-x64`, `linux-arm64`, `osx-arm64` and
`osx-x64` and would be carrying it. The managed Thermo SDK itself is cross-platform; this is a
project-reference shape, and worth raising upstream rather than working around.

**It is not consumable as a package.** No `GeneratePackageOnBuild`, so there is no NuGet
artifact; MARS would vendor or submodule a build of an unmerged draft branch. The tree is also
not self-contained - `Common.csproj` embeds `pwiz/data/common/{psi-ms,unimod,unit}.obo` from
the C++ tree, and the build needs `libraries/7za.exe` - so a sparse checkout has to include
those paths. The vendor SDK is license-gated behind `-p:IAgreeToVendorLicenses=true`, which
MARS's CI and release build would have to carry deliberately.

**The port is a draft at 85% semantic parity** with C++ msconvert (359 of 421 comparable
files identical), with the remaining differences documented as mostly not port defects.

### The writer, measured

MARS's output guarantee is the [byte-splice passthrough](mzml-passthrough.md), and reading
RAW removes the input that splice is made against. The concern that motivated the splice was
serializer damage: two round-trips in the Python implementation produced valid mzML that broke
DIA-NN and SeeMS. That concern does **not** transfer to pwiz. Those round-trips were psims and
lxml; the mzML MARS reads was written by msconvert, so writing with pwiz-sharp regenerates a
file with the same lineage rather than introducing a foreign serializer. Handing the format
code to the people who maintain the format is the point.

Measured, on `Ste-2024-12-02_HeLa_20msIIT_GPFDIA_600-700_16`, applying the same model through
a `SpectrumListWrapper` and writing with `MSDataFile.Write`, then diffing against MARS's own
byte-splice output with `mars compare`:

```
spectra compared      114,021
peaks compared        82,349,582
m/z values differing  0
max |delta m/z|       0 Th
intensity differing   0
```

**Numerically identical.** That settles the two things worth settling: the adapter from pwiz's
`Spectrum` to MARS's `SpectrumRecord` feeds the model the same values the native reader does,
and the writer round-trips them without loss.

### What the writer costs

| Output | Size | Wall | Input |
|---|---:|---:|---|
| mzML (byte-splice, for reference) | 1.906 GB | - | 1.471 GB |
| mzML (pwiz) | 1.916 GB | 337 s | 1.471 GB |
| mzXML (pwiz) | 0.983 GB | 226 s | 1.216 GB |
| mzMLb (pwiz) | 0.557 GB | 213 s | 1.216 GB |

Both mzML writers inflate relative to the input, which is expected: correcting m/z makes the
arrays less compressible than the smooth originals. pwiz lands within 0.56% of the splice.
mzMLb is less than half the input, which is an argument for it on its own.

Two things to get right:

**Match the encoding explicitly.** `BinaryEncoderConfig` defaults to 64-bit *uncompressed*,
which inflated the first attempt by 61%. The input is 64-bit zlib for both arrays, and setting
that recovers the size. Note the shape difference: MARS's splice reads encoding **per array**,
because m/z is often 64-bit where intensity is 32-bit and compression can differ between two
arrays of one spectrum. pwiz's config is global, with per-array overrides keyed by CVID - so
the common case is expressible, but a file whose encoding varies spectrum to spectrum is not.

**The write path is sequential and that is the real cost.** MARS's byte-splice writer is
parallel and writes 8.4 GB across five files in 123 s; the pwiz spike reads, corrects and
writes one file in 337 s single-threaded. pwiz's `ISpectrumList` is random-access so the work
parallelizes in principle, but `MSDataFile.Write` pulls spectra sequentially. Closing that gap
is the main engineering cost of the move, and it is a throughput problem rather than a
correctness one.

### Decision

**Adopt the pwiz writer.** It buys mzXML and mzMLb now (mz5 has no writer class yet; mzMLb has
one and is dispatched from the path-shaped `Write` overload, not the stream-shaped one), it
puts the format code with the people who maintain the format, and it produces byte-for-byte
equivalent numbers on real data.

Staging, smallest dependency first:

1. **Writer only, mzML in.** Needs `Util`, `Common` and `MsData` - no vendor projects, so no
   native Waters DLL. `SpectrumListWrapper` lives in `Analysis`, so either take that reference
   or derive from `SpectrumListBase` in `MsData` to keep the dependency to three projects.
2. **Parallelize the write**, to close the throughput gap against the splice.
3. **RAW input**, which adds the vendor chain and its Windows-only transitive DLL.

Keep the byte-splice path for mzML in and mzML out until 2 lands, then decide whether to
retire it on the evidence rather than in advance. Note that mzMLb output on `linux-arm64` and
`osx-arm64` needs checking: `HDF.PInvoke.1.10` bundles native libhdf5 for Windows and Linux
**x64**, and MARS ships arm64 artifacts.

Reproduce with the probes under `scratchpad/` - `rawprobe` for the reader, `pwizwrite` for the
wrapper and writer.

## Ion mobility: collapsed, not modelled

**Settled.** MARS does not read ion mobility and does not intend to. It collapses the dimension
instead, by asking pwiz to combine each TIMS frame's mobility scans back into one spectrum per
isolation window.

The reason it came up: pwiz presents an uncombined TIMS frame as hundreds of spectra sharing
one retention time and one isolation m/z, separated only by mobility. ProteoWizard's
`diaPASEF.d` is 4,631 spectra at five distinct scan times, and the first MS2 in it holds
**two peaks**. Combining turns the same file into 8 MS2 spectra across 8 distinct isolation
windows and 4 retention times, and that first spectrum into **8,377 peaks**.

That is not only tidier, it is the difference between usable and not. MARS matches library
fragments within a spectrum and computes its space-charge features from the peaks around each
match - `ions_above_0_1`, `adjacent_ratio_1_2` and the rest. On a two-peak mobility slice there
are no neighbours to measure, so those fourteen features would be noise even where they were
defined. Combined, the spectrum has the shape every other instrument already produces, and the
matcher and the features work unchanged.

Reading and writing both combine, so what gets written matches what was matched and modelled.

The alternative - carrying mobility as a feature and fitting the uncombined slices - was
tried and reverted. It would have meant a 23rd feature that only one vendor populates, and a
matcher operating on spectra too sparse to compute most of the others from.

## Where the vendor SDKs come from

**Decided for now: MARS ships them.** A released binary carries the Thermo, Bruker and Sciex
assemblies, so a download opens a `.raw` with nothing else installed. That is what pwiz and
Skyline already do, and it is the only option that makes "download MARS, calibrate a run" true.

**Planned to change once [PR #4178](https://github.com/ProteoWizard/pwiz/pull/4178) merges to
master.** When Skyline and msconvert ship pwiz-sharp themselves, MARS should stop carrying its
own copies and use the installed ones - the user has already accepted the vendor licences by
installing either.

Three candidates, in preference order, and the order is about how stale each one's SDK is
likely to be:

| Candidate | Updates | Discovery |
|---|---|---|
| **Skyline-daily** | ClickOnce, frequent | Verified below; both channels verified |
| **Skyline** | ClickOnce, much less often | Same mechanism and the **same** token; see below |
| **msconvert** | Manual download only | Unreliable - see below |

Skyline-daily first because it updates fastest, so its SDK will track upstream without MARS
releasing anything. Regular Skyline uses the same ClickOnce machinery and should be used when
it is the only one present.

**Both channels ship the same Thermo SDK today**: 5.0.0.93, on Skyline 26.1.0.57 and
Skyline-daily 26.1.1.209 alike, both installed here. So the ordering is about which will move
first once pwiz-sharp merges, not about a difference that exists now. msconvert last: it does
not update itself at all, and its registry entry is the least useful of the three.

**Because they lag by different amounts, the version has to be checked rather than assumed.**
Whatever is found, read the `FileVersion` of `ThermoFisher.CommonCore.RawFileReader.dll` and
compare it against the minimum pwiz-sharp needs before using it; fall through to the next
candidate, and finally to a bundled copy, when it is too old. That turns "prefer daily" from a
guess into a check - and it is the same check that catches today's 5.0.0.93 immediately rather
than at the first `MissingMethodException`.

### Why it cannot be done today

Tried, and it fails for a version reason rather than a licensing one. Skyline is installed on
the development machine and does ship `ThermoFisher.CommonCore.RawFileReader.dll` - at
**5.0.0.93, targeting .NET Framework 4.7.1**. pwiz-sharp needs **8.0.6.0**: a `net8.0` process
cannot load the former, and pwiz-sharp calls `RawFileReaderAdapter.ThreadedFileFactory` and the
three-argument `Scan.FromFile`, which only the newer SDK has. The C++ ProteoWizard install
carries the same 5.0.0.93. There is no NuGet package.

So the precondition is not "Skyline is installed" but "Skyline ships pwiz-sharp's assemblies",
which is what #4178 merging brings.

### What the switch will need

**Finding the install needs a registry lookup, and the registry does not hold the path.**
ClickOnce puts Skyline under a hashed directory that changes on every update, so a directory
constant is not an option. The route from registry to files, confirmed against the
Skyline-daily installed on the development machine:

1. Enumerate `HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall`. The subkey names are
   opaque hashes - `6e917b9fd968e06d` here - so match on `DisplayName`, which is `Skyline-daily`
   (a second entry, `Skyline-daily Parquet`, is a tool rather than the application).

2. The entry has **no `InstallLocation`**. What it does have is the ClickOnce identity, in
   `UninstallString` and `ShortcutAppId`:

   ```
   rundll32.exe dfshim.dll,ShArpMaintain Skyline-daily.application, Culture=neutral,
   PublicKeyToken=9286511f3362df93, processorArchitecture=msil
   ```

   Take `PublicKeyToken`. Keep `DisplayVersion` too - `26.1.1.209` here - it disambiguates in
   the next step.

3. That token is embedded in the deployment directory name under
   `%LOCALAPPDATA%\Apps\2.0`:

   ```
   ...\Apps\2.0\<hash>\<hash>\skyl..tion_9286511f3362df93_001a.0001_6c454ec13578dbec\
   ```

   **The token does not identify which Skyline.** Skyline and Skyline-daily are signed with
   the same key and share `9286511f3362df93`, so both match the same glob - an earlier
   version of this note assumed they differed, and an implementation built on that would
   have picked whichever it enumerated first. Several directories match for the further
   reason that previous versions are kept, and `..exe_` folders sit beside `..tion_` ones
   carrying no vendor DLLs at all.

   Identify the right one by the **executable it contains** - `Skyline.exe` against
   `Skyline-daily.exe` - and confirm with the version. Compare versions **parsed, not as
   strings**: the registry says `26.1.0.57` where the file says `26.1.0.057`, equal as a
   version and unequal as text. Do not select by newest timestamp; an update in progress
   would make that lie.

That directory is the one holding the vendor assemblies; the `..exe_` folders hold none, which
is a cheap way to reject them. On this machine both channels hold
`ThermoFisher.CommonCore.RawFileReader.dll` at **5.0.0.93** - Skyline 26.1.0.57 and
Skyline-daily 26.1.1.209 - which is the version gap above, measured on current builds of
both rather than assumed.

**msconvert is the awkward one to find.** ProteoWizard is installed on the development
machine and registers under `HKLM`, but the entry carries **no `InstallLocation`**, there is no
`App Paths` entry for `msconvert.exe`, and it is not under `Program Files`. Its
`UninstallString` is a bare `MsiExec.exe /I{GUID}`, which names the product without saying
where it went. Resolving it would mean querying the Windows Installer product database rather
than reading a value. That is a second, independent reason to reach for it last.

macOS and Linux need something else entirely - Skyline is Windows-only - so those platforms
keep the bundled SDKs regardless. The switch is a Windows optimisation rather than a change of
approach everywhere, which means the bundling machinery stays either way.

**Only the vendor SDKs move.** mzMLb needs a native HDF5 through `HDF.PInvoke`, which is not a
vendor library and would stay bundled.

**`mars --version` already reports what a binary carries**, and should keep telling the truth
across the change: a MARS that borrows its readers from Skyline and cannot find one has to say
so there, rather than at the moment somebody opens a file.

## The ZenoTOF 8600 reads, but reports no analyzer

**Measured on real data, and it needs an upstream fix.**

A ZenoTOF 8600 `.wiff2` opens and reads correctly: 300,570 spectra, 3,000 MS2 sampled with
11.7 million peaks across 429 distinct isolation windows, isolation window present on every
one. The bundled Sciex SDK handles the instrument.

What it does not do is say what recorded it. pwiz's Sciex model table stops at `ZENOTOF7600`,
so an 8600 falls off the end:

```
[Reader_Sciex.FillInMetadata] unable to determine instrument model
    (unknown instrument type: ZenoTOF 8600 System)
```

That is non-fatal by design, but the consequence is not cosmetic. With the model unrecognised
the reader emits an instrument configuration with **no components at all** - `IC1: (no
components)` - and Sciex writes no Thermo-style filter string. MARS therefore has nothing to
classify from, reports `Unknown`, and falls back to the unit-resolution default of 0.3 Th.
On a TOF that is about **760 ppm at m/z 400**, against real error of a few ppm.

### The upstream fix

One line in `pwiz/src/Vendor/Sciex/Reader_Sciex_Detail.cs`, alongside the existing entry:

```csharp
if (n.Contains("ZENOTOF7600", StringComparison.Ordinal)) return SciexInstrumentModel.ZenoTOF7600;
```

An 8600 case, and a `SciexInstrumentModel` member mapping to `MS_time_of_flight` the way
`ZenoTOF7600` does. Worth raising on
[PR #4178](https://github.com/ProteoWizard/pwiz/pull/4178), because every tool reading 8600
data through pwiz-sharp inherits this, not only MARS.

### What MARS does about it meanwhile

Detection cannot be fixed from MARS's side - there is genuinely no analyzer information in the
file as pwiz presents it. So MARS checks the consequence instead, after matching, when the data
can answer: if the matching window is more than 50x the median absolute error actually found,
it says so. A window that wide is the signature of a tolerance set for the wrong instrument.

The asymmetry is what makes this worth doing. A tolerance that is too narrow fails loudly, with
too few matches to train on. One that is too wide fails silently - it fills with peaks that are
not the fragment, and the run completes and reports numbers regardless. Only the silent
direction needs a detector.

The threshold is loose deliberately: trap data at its correct 0.3 Th sits around 4x, so this
cannot fire on the case MARS was built for. Verified both ways - silent on a Stellar run at
0.3 Th, and firing on high-resolution data forced to the trap tolerance.

## Profile data is centroided by the vendor

**Settled.** Sciex writes profile spectra, and MARS asks the vendor to centroid them before
matching or correcting anything.

The ZenoTOF 8600 file is stored as profile: `profile=True`, 1,619 points in one MS2, evenly
spaced at 0.00233 Th. That spacing is 16 ppm at m/z 142, which matters because MARS measures
mass error by taking the most intense peak inside a tolerance window - on a sampled curve the
answer is quantised to the grid, so the floor on measurable error would be several times the
error the instrument actually has. The fourteen space-charge features fare worse still: they
count the peaks around a match, and on profile they would count samples of the same ion.

pwiz exposes the vendor's own algorithm through `IVendorCentroidingSpectrumList`, which
`SpectrumList_Sciex` implements as "ABI/Analyst peak picking". The vendor knows its detector,
so that is preferred over a peak picker of ours. On the first MS2 of that file it turns 1,619
points into **210**, and across 200 MS2 the average falls from about 3,900 samples to 801
peaks.

Applied on **both** paths, reading and writing. They have to agree: the model is fitted on peak
lists, and correcting sampled curves with it would put every feature far outside anything it
saw in training.

**Only when the spectrum says it is profile.** Thermo and Bruker already deliver centroids -
an Astral run reads `centroid=True`, 139 peaks, and passes through untouched - so this changes
nothing for them.

One consequence worth being explicit about: a corrected file written from profile input comes
out **centroided**, because that is what was modelled. That is what `msconvert --filter
peakPicking` does routinely and what DIA-NN and Skyline want, but it is a real change to the
data rather than only to the m/z values, and it is the one case where MARS's output differs
from its input in more than the numbers it set out to correct.
