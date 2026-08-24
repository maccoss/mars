# MARS documentation

## Start here

| Document | What it covers |
|---|---|
| [algorithm.md](algorithm.md) | The recalibration algorithm end to end: fragment matching, the 22 features, training, and how the correction is applied. |
| [cli-reference.md](cli-reference.md) | Every command and option, and what the exit codes mean. |
| [spectral-libraries.md](spectral-libraries.md) | The four library sources - including the Skyline PRISM report as CSV or parquet - what makes a usable one, and how to choose a tolerance. |
| [qc-report.md](qc-report.md) | How to read the QC figures, and what a small correction actually means. |

## In depth

| Document | What it covers |
|---|---|
| [model.md](model.md) | The gradient boosted trees: objective, histogram splits, hyperparameters, intensity weighting, determinism, importance, and the model file format. |
| [mzml-passthrough.md](mzml-passthrough.md) | How MARS writes mzML without disturbing anything it did not mean to change. |
| [architecture.md](architecture.md) | A map of the code: projects, data flow, the managed SQLite reader, dependencies, and where to change things. |

## Provenance

| Document | What it covers |
|---|---|
| [python-parity.md](python-parity.md) | How the C# implementation was checked against the Python one row by row, what agreed, and what parity cannot cover. The Python implementation was removed after `v26.1.0`; the result is frozen in [`parity/`](../parity/README.md). |
| [dotnet-port-spec.md](dotnet-port-spec.md) | The specification that governed the port: decisions, acceptance gates, measured results, and four defects the port found in the Python implementation. Kept as the record; the Python source it refers to is at `v26.1.0`. |
| [open-questions.md](open-questions.md) | What was deliberately left undone, what was measured, and what would settle it. |

---

## Quick answers

**Should I run MARS on my data?**
Run `mars qc` first. It reports the mass error already present without training or writing
anything. On a well-calibrated instrument there is often nothing worth removing, and
leaving the files alone is the right answer. See [qc-report.md](qc-report.md).

**What does MARS change in my file?**
The m/z arrays of MS2 spectra it corrected, and nothing else. Intensities, chromatograms and
metadata are untouched, and the bytes MARS did not mean to change are the input's own bytes.
See [mzml-passthrough.md](mzml-passthrough.md) and
[algorithm.md](algorithm.md#step-4-correction).

**How much improvement should I expect?**
On Thermo Stellar ion-trap DIA, roughly half the median absolute fragment mass error. On an
already well-calibrated Astral run, under 2%. See [algorithm.md](algorithm.md#results).

**Which library should I use?**
A Skyline PRISM report if you have one, because its `Product Mz` is genuinely theoretical
and it carries real per-peptide elution windows. A `.blib` without peak annotations will be
refused outright, and MARS will say so. See [spectral-libraries.md](spectral-libraries.md).

**Is the output reproducible?**
Identical input gives a bit-identical model and bit-identical decoded output at any thread
count, enforced by its own CI job. The compressed bytes are not identical across platforms,
because runtimes ship different zlib builds - use `mars compare`, not `cmp`. See
[model.md](model.md#determinism).

**Does it give the same answer as the Python version?**
Fragment matching and every model feature are bit-identical: 352,349 fragments across the
five-file Stellar cohort, every shared column at maximum absolute difference zero, checked
immediately before the Python implementation was removed and frozen in
[`parity/`](../parity/README.md). The models are different
implementations, agree to r = 0.9955, and leave the same amount of error behind. Four
Python defects are deliberately not reproduced. See [python-parity.md](python-parity.md)
and [model.md](model.md#how-close-is-this-to-xgboost).

**Something looks wrong with a corrected file.**
Run `mars verify` on the *input*. It round-trips the file with a null correction and checks
the index, checksum and decoded arrays. If that fails, the problem is file handling rather
than the model, and nothing else is worth investigating first.

---

For installing and running MARS, see the [top-level README](../README.md). For the C#
source tree, see [dotnet/README.md](../dotnet/README.md).
