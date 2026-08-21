# The MARS recalibration algorithm

How MARS turns a spectral library and a set of DIA runs into corrected m/z values.

- [The problem](#the-problem)
- [Overview](#overview)
- [Step 1: Fragment matching](#step-1-fragment-matching)
- [Step 2: Feature extraction](#step-2-feature-extraction)
- [Step 3: Model training](#step-3-model-training)
- [Step 4: Correction](#step-4-correction)
- [What the model actually learns](#what-the-model-actually-learns)
- [Determinism](#determinism)
- [Limits](#limits)

## The problem

A mass analyzer's reported m/z drifts. On a Thermo Stellar ion trap the drift is large
enough to matter: across a five-file HeLa GPF-DIA cohort the fragment mass error has a
standard deviation of **0.118 Th** and a median absolute deviation of **0.080 Th**, with
a systematic offset of about -0.013 Th.

That error is not random. It varies with where the peak sits in the scan, how much signal
the trap accumulated, how crowded the neighborhood is, and when in the run the scan
happened. Anything systematic is learnable, and anything learnable can be subtracted.

MARS learns the systematic part from peaks whose true m/z is already known - fragments of
peptides a spectral library has identified - and applies the learned correction to every
peak in the file.

## Overview

```
                spectral library                     mzML (MS2)
                       |                                  |
                       +--------------+-------------------+
                                      |
                            [1] fragment matching
                     most intense peak within tolerance
                                      |
                        delta_mz = observed - theoretical
                                      |
                            [2] feature extraction
                        22 features per matched peak
                                      |
                            [3] model training
                    gradient boosted trees, squared error
                                      |
                                 model.json
                                      |
                            [4] correction
                    corrected = observed - predicted error
                                      |
                              {input}-mars.mzML
```

MARS makes **two passes** over each file. The first reads spectra, matches fragments and
builds training rows. The second re-reads the file and splices corrected m/z arrays into a
byte-for-byte copy of the original. Neither pass holds the file in memory; a 4.9 GB Astral
run streams in the same working set as a 1.2 GB Stellar one.

## Step 1: Fragment matching

A library fragment is compared against a spectrum only when the spectrum could plausibly
contain it:

1. **Isolation window.** The library precursor m/z must fall inside the spectrum's DIA
   isolation window, `low <= precursor_mz <= high`, both bounds inclusive.
2. **Retention time.** The spectrum's scan start time must fall inside the library entry's
   RT window, `rt_start <= rt <= rt_end`, both inclusive. Entries with no RT window are
   considered at every retention time.

Within a candidate spectrum, each fragment is looked for within a tolerance of its
theoretical m/z - either absolute (`--tolerance`, default 0.3 Th) or relative
(`--tolerance-ppm`, appropriate for Orbitrap and Astral data).

> **MARS takes the MOST INTENSE peak in the window, not the nearest.**
>
> This is the single most important choice in the matching step. Taking the nearest peak
> would bias every label toward zero - you would be selecting for peaks that agree with
> the library and measuring the error you had already assumed. The most intense peak has
> the best-determined centroid, and whatever error it carries is the error worth learning.

Peaks below `--min-intensity` (default 500) cannot become training rows. The threshold
governs training only; **every** peak in a corrected spectrum is corrected regardless of
intensity.

The label is:

```
delta_mz = observed_mz - theoretical_mz
```

Positive means the instrument reported the peak too high. There is no q-value filter and
no outlier trimming: confidence comes from the library having been built from confident
identifications, and the tolerance bounds the label by construction.

## Step 2: Feature extraction

Each matched fragment becomes one row of up to 22 features. The order below is the model's
feature order and is part of the on-disk model contract.

**Where the peak is, and how strong**

| Feature | Meaning |
|---|---|
| `precursor_mz` | Isolation window target m/z. Different DIA windows can calibrate differently. |
| `fragment_mz` | The reference m/z. Mass error is mass-dependent, so this is the backbone of the model. |
| `log_intensity` | log10 of peak intensity. A weak peak has a noisier centroid. |
| `log_tic` | log10 of the spectrum's summed intensity. A proxy for total trap loading. |

**How much charge was in the trap**

| Feature | Meaning |
|---|---|
| `injection_time` | Ion injection time, in seconds. |
| `tic_injection_time` | Summed intensity x injection time: total ions rather than an ion rate. |
| `fragment_ions` | Peak intensity x injection time: this peak's ion count. |

**What was next to the peak** (the space-charge features)

Six windows of summed neighbor intensity, each 1 Th wide, scaled by injection time:

| Feature | Window, relative to the reference m/z x |
|---|---|
| `ions_above_0_1` | (x + 0.5, x + 1.5] |
| `ions_above_1_2` | (x + 1.5, x + 2.5] |
| `ions_above_2_3` | (x + 2.5, x + 3.5] |
| `ions_below_0_1` | (x - 1.5, x - 0.5] |
| `ions_below_1_2` | (x - 2.5, x - 1.5] |
| `ions_below_2_3` | (x - 3.5, x - 2.5] |

Plus six ratio features, `ions_* / fragment_ions`, expressing the neighborhood relative to
the peak itself rather than in absolute terms.

The half-Th offsets are deliberate: isotopes sit at +1, +2, +3 Th, so a window running from
+0.5 to +1.5 brackets the first isotope instead of straddling two. Bounds are exclusive at
the low end and inclusive at the high end. A window that runs past the edge of the recorded
scan range simply finds nothing and sums to zero - there is no missing-value marker.

**When, and how hot**

| Feature | Meaning |
|---|---|
| `absolute_time` | Seconds since the earliest acquisition in the cohort. Captures drift across a run sequence, not just within one file. |
| `rfa2_temp`, `rfc2_temp` | RF generator temperatures at that retention time, when temperature logs are supplied. |

### Features are selected, not assumed

Not every run supports every feature. A file with no `MS:1000927 ion injection time`
cvParam loses fourteen of the twenty-two at once, because none of the injection-time or
space-charge features are defined without it. The fitted model records the names it
actually used, and loading a model whose feature list does not match the extractor is a
hard error rather than a warning.

**No feature is ever NaN.** Rows that cannot supply a selected feature are dropped before
training. This is a hard requirement, not tidiness: the tree implementation maps NaN to the
lowest bin while the tree walk sends it right, so a NaN reaching the model would be routed
inconsistently between fitting and scoring.

## Step 3: Model training

Gradient boosted trees with a squared-error objective, from
[`Osprey.ML`](https://github.com/ProteoWizard/pwiz/tree/master/pwiz_tools/Osprey/Osprey.ML) -
the same implementation Osprey uses for FDR scoring, so there is one boosting implementation
to maintain rather than one per tool.

| Hyperparameter | Value |
|---|---|
| objective | squared error |
| boosting rounds | 100 |
| max depth | 6 |
| learning rate | 0.1 |
| min child weight | 1.0 |
| subsample / colsample | 1.0 / 1.0 |
| L2 (lambda) / L1 (alpha) | 1.0 / 0.0 |
| histogram bins | 256 (clamped to 255) |
| seed | 42 |

Rows are weighted by observed peak intensity, normalized to mean 1. The normalization
matters: under squared error the hessian **is** the sample weight, so raw detector counts
would put the summed hessian in the millions and make `min_child_weight` meaningless.

By default MARS trains five models, one per fold, with **folds split by peptide**, and
every reported number comes from a model that never saw the peptide it is scoring. A
peptide's fragments recur across hundreds of spectra with the same theoretical m/z, and
`fragment_mz` is a feature, so splitting rows rather than peptides would let the model
memorize a peptide's error and report an accuracy it cannot reach on anything new. See
[model.md](model.md#cross-validation).

> **A note on `min_child_weight`.** It thresholds the summed hessian, and the hessian means
> different things under different objectives. Under logistic loss it is p(1-p), never above
> 0.25, so a threshold of 1.0 means several samples. Under squared error it is the weight,
> so with unit weights 1.0 means exactly **one** sample. Hyperparameters do not transfer
> between objectives.

Below `--min-training-rows` (default 1000) MARS refuses to fit and exits 2, rather than
producing a model built on noise.

[model.md](model.md) goes further: why boosted trees rather than a polynomial, how the
objective and histogram splits work, how the model file is laid out, and how the
predictions compare against Python's XGBoost on identical rows.

## Step 4: Correction

For every peak of every qualifying MS2 spectrum:

```
corrected_mz = observed_mz - model.PredictDelta(features)
```

The sign follows from the label: `delta_mz` is observed minus theoretical, so the predicted
error is subtracted. Getting this backwards roughly doubles the error instead of halving it,
which is at least a loud failure.

Two differences from training are worth knowing:

- **`fragment_mz` is the observed m/z**, because at correction time there is no library to
  supply a theoretical one. The two differ by at most the matching tolerance.
- **The neighbor windows are anchored on each observed peak**, for the same reason.

MS1 spectra, intensity arrays, chromatograms and all metadata are untouched. Spectra whose
isolation window is wider than `--max-isolation-window` are skipped entirely.

### Keeping m/z ascending

A per-peak correction can in principle reorder two adjacent peaks, and mzML consumers assume
a sorted m/z array. `--on-reorder` chooses what happens:

| Mode | Behavior |
|---|---|
| `clamp` (default) | Raise the offending peak to the next representable double above its predecessor. |
| `revert` | Leave that whole spectrum uncorrected. |
| `allow` | Write the values as they came out. For diagnosing how often it happens. |

Violations are counted and reported under every mode. On the reference Stellar cohort the
count is **zero** across 565,498 corrected spectra - corrections are two orders of magnitude
smaller than typical peak spacing, so this is a guard against a pathological model rather
than a routine event.

## What the model actually learns

Feature importance from the reference Stellar cohort, as reported by the Python
implementation's gain importance:

| Feature | Importance |
|---|---|
| `ions_above_0_1` | 0.346 |
| `adjacent_ratio_0_1` | 0.292 |
| `fragment_mz` | 0.156 |
| everything else | < 0.02 each |

Two thirds of the model is the **first neighbor window above the peak** and its ratio to the
peak's own intensity. That is a space-charge effect: an ion cloud sitting roughly one Th
above a peak perturbs the measured m/z of that peak, and the size of the perturbation scales
with how much charge is there relative to the peak itself. The mass-dependence term
(`fragment_mz`) comes third.

This is why the six neighbor windows exist at all, and why the injection-time scaling
matters: what perturbs the measurement is the number of ions, not the rate at which they
arrived.

### Results

Measured on the corrected files, by rematching the library against the written output:

| Stellar HeLa GPF-DIA, 5 files | Uncorrected | Corrected |
|---|---|---|
| Fragments matched | 352,349 | 358,340 |
| Mean delta m/z | -0.0134 Th | -0.0057 Th |
| Median delta m/z | -0.0082 Th | -0.0048 Th |
| Std delta m/z | 0.1180 Th | 0.0883 Th |
| MAD delta m/z | 0.0800 Th | 0.0469 Th |
| RMS delta m/z | 0.1188 Th | 0.0885 Th |

A 41% reduction in MAD and a 25% reduction in standard deviation.

**MARS does not help every instrument.** On an Astral plate the same pipeline moves the
spread by under 2%, because the data arrives already calibrated to about 4 ppm and there is
essentially nothing systematic left to remove. That is a real result about the data, not a
failure of the method: run `mars qc` first and see whether there is anything to correct
before correcting it.

## Determinism

MARS writes m/z values into files that get reprocessed and compared, so determinism is a
correctness requirement rather than a nicety.

**Identical input produces identical decoded m/z values** - on any thread count, on any
platform, on every run. The guarantees behind that:

- Histogram accumulation parallelizes **across features only**. One thread owns one
  feature's histogram and walks the node's rows in ascending order, so no summation order
  can depend on the thread count.
- Subsampling draws from a seeded `XorShift64`, never `System.Random`, whose seeded stream
  is a runtime implementation detail.
- Split selection walks features and bins in ascending order and takes a new best only on a
  strict improvement, so ties resolve to the lowest (feature, bin).
- Row partitioning is stable.
- Inference has no cross-row accumulation, so parallelizing it cannot change a value.

**File bytes are a different matter.** Compressed bytes are *not* guaranteed identical
across platforms, because the zlib each runtime ships is not the same. Verified on the same
input:

| | Windows | Linux |
|---|---|---|
| Output size | 1,176,380 bytes | 1,176,172 bytes |
| Raw bytes | differ | |
| Decoded m/z and intensity | **identical**, 0 of 10,283 peaks differ | |

Equivalence is defined on decoded values, which is what any consumer actually reads. Use
`mars compare a.mzML b.mzML` to check two files on that basis rather than with `cmp`.

## Limits

- **Centroided input is assumed.** Neither implementation currently detects or rejects
  profile-mode spectra.
- **MS2 only.** MS1 peaks are never corrected.
- **One model per invocation**, fitted across all input files together. That is what makes
  `absolute_time` meaningful - it spans the cohort, so the model can learn drift across a
  run sequence.
- **The correction is only as good as the library.** A library whose fragment m/z values
  are observed rather than theoretical teaches the model to reproduce another run's
  calibration error. See [spectral-libraries.md](spectral-libraries.md).

## See also

- [model.md](model.md) - the gradient boosted trees in depth, and the model file format
- [spectral-libraries.md](spectral-libraries.md) - the four library sources and their quirks
- [qc-report.md](qc-report.md) - how to read the figures MARS writes
- [mzml-passthrough.md](mzml-passthrough.md) - how the output file is written
- [cli-reference.md](cli-reference.md) - every command and option
- [architecture.md](architecture.md) - a map of the code
- [python-parity.md](python-parity.md) - how this is checked against the Python implementation
- [dotnet-port-spec.md](dotnet-port-spec.md) - the port specification and acceptance gates
