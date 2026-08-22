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

## MARS is blind to ion mobility

**Found while adding Bruker support, not yet addressed.**

ProteoWizard's `diaPASEF.d` test file holds 4,631 spectra with **5 distinct scan times**, and
every one of them carries an inverse reduced ion mobility. It is five TIMS frames, each
expanded to about 926 spectra - one per mobility scan - taken as a 0.31-second excerpt from
64.4 minutes into a run.

That is how pwiz presents TIMS data when the mobility dimension is not combined, and it means
MARS sees roughly 900 spectra sharing one retention time, distinguished by a dimension it has
no feature for.

The concrete gap: a diaPASEF isolation window is two-dimensional, m/z **and** mobility. MARS
reads the m/z bounds and nothing else, so two windows at the same m/z but different mobility
are identical as far as the model is concerned. `precursor_mz`, `absolute_time` and
`acquisition time` are all constant within a frame; only the intensity and space-charge
features vary between those 900 spectra.

Whether that matters is an empirical question nobody has asked yet. It matters if mass error
on a timsTOF depends on mobility - which is plausible, since mobility separation happens before
the flight tube and changes both the ion population reaching it and when - and does not if the
error is dominated by the same effects MARS already models.

What would settle it: calibrate a real diaPASEF run, then plot the residual against
`inverse reduced ion mobility` the way the QC report already plots it against every feature. If
that panel is flat, nothing is missing. If it slopes, mobility belongs in the feature set, and
the isolation window should carry its mobility bounds as well as its m/z bounds.

Worth doing before anyone concludes from a weak reduction that MARS does not help on Bruker
data - with the injection-time group off as well, a timsTOF run is fitted on 8 features rather
than 22, and two separate explanations for a poor result is one too many.
