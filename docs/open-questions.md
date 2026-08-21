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

### The real question is the writer, not the reader

MARS's output guarantee is the [byte-splice passthrough](mzml-passthrough.md): the corrected
file is the input, byte for byte, except the m/z arrays actually changed. That guarantee
exists because two serializer round-trips in the Python implementation produced valid mzML
that broke DIA-NN and SeeMS.

Reading RAW removes the input the splice is made against. `RAW -> MARS -> mzML` means MARS
*generates* mzML, and the guarantee weakens from "identical by construction" to "our writer is
as good as msconvert's" - which is a far larger surface, and one pwiz-sharp is still
reconciling. Training from RAW and applying to an msconvert mzML keeps the guarantee but
keeps the conversion too, so it buys nothing.

That makes this a strategic choice rather than an incremental feature: RAW in and mzML out
turns MARS into a converter.

### Recommendation

Start with **`mars qc` accepting `.raw`**. `qc` writes no mzML, so it takes on the reader and
none of the writer risk, and it answers a question worth answering before a conversion rather
than after: is there enough systematic error in this run to be worth calibrating? Behind that,
decide `calibrate --raw` separately, once the reader has been used in earnest and PR #4178 has
landed.

Reproduce with the probe under `scratchpad/rawprobe` - `Program.cs` for the field dump,
`Parallel.cs` for the thread scaling.
