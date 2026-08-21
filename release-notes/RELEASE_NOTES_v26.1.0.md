# MARS v26.1.0 Release Notes

MARS is now a self-contained, cross-platform command-line tool. This is the first
release of the C# implementation, which is MARS going forward. The Python
implementation is frozen to bug fixes and will be archived once the C# one has been
used in earnest; it is no longer published to PyPI.

Versions from here follow `YY.feature.patch`, so this is the first feature release of
2026 rather than a continuation of the Python package's `0.1.x` line.

## New Features

- **`mars` CLI for Windows, Linux and macOS.** Five commands: `calibrate` (learn a
  correction and write recalibrated mzML), `apply` (reuse a trained model), `qc` (report
  mass accuracy without training or writing), `verify` (round-trip a file with a null
  correction and check it), and `compare` (diff two mzML files on decoded values).
- **`.blib` libraries read without native code.** A managed SQLite reader written for this
  purpose replaces `Microsoft.Data.Sqlite`, so that path pulls in no per-platform native
  binary. The only native code in the tree comes from `Parquet.Net` (DIA-NN libraries) and
  ships inside the release archives.
- **Streaming mzML.** Memory is bounded by the largest single spectrum plus the training
  matrix rather than by file size, so a 4.9 GB Astral run uses the same working set as a
  1.2 GB Stellar one.
- **Byte-splicing writer.** The output is a byte-for-byte copy of the input except for the
  m/z arrays actually corrected, which removes an entire class of serializer-induced
  compatibility problems.
- **`mars verify`.** Applies a null correction and checks that the result decodes to
  bit-identical arrays with a valid index and checksum. Run it before trusting any
  corrected file; it separates a file-format problem from a model problem.
- **Versioned JSON model files**, recording the format version, MARS version, ordered
  feature names, every hyperparameter, the acquisition-time offset and the training row
  counts. Loading a model whose feature list does not match the extractor is a hard error.
- **`--min-training-rows`** (default 1000). MARS refuses to fit below this and exits 2,
  rather than producing a model built on noise.
- **`--on-reorder`** controls what happens if a per-peak correction would break ascending
  m/z order: `clamp` (default), `revert` or `allow`. Violations are counted and reported
  under every mode.
- **Shared model implementation.** The gradient boosted trees come from `Osprey.ML`, so one
  boosting implementation is maintained rather than one per tool.
- **A QC report with figures, as one self-contained HTML file.** `mars calibrate` writes
  `mars_qc_report.html` alongside the text summary: the error distribution before and
  after correction, median error across retention time and fragment m/z, permutation
  importance, and a density panel with median-error trend lines for every active feature.
  Everything is embedded - no scripts, no external references, nothing fetched when it is
  opened - so the file can be attached to an email and read by someone who has neither the
  data nor the tool. A 22-feature report is around 210 KB. `--no-html-report` skips it and
  `--html-report <path>` moves it.
- **`mars qc` writes the figures too**, minus the ones that need a model: the error as
  measured, how it varies across retention time and fragment m/z, and a panel per feature.
  That is the report the decision to calibrate actually turns on, since `qc` is what you run
  first. It stops short of predicting how much of the error is removable, because nothing
  short of fitting a model answers that. `mars qc` also accepts `--temperature-dir` now, so
  the temperature panels appear there as well.
- **`--dump-matches`** writes every matched fragment to CSV with all computed features, for
  answering "which peak did MARS match, and what did it compute from it" without a
  debugger. It is also what makes this implementation checkable against the Python one row
  by row: across two Stellar runs, 160,947 matched fragments agree on all 24 shared columns
  with a maximum absolute difference of zero, including the space-charge features that
  carry the most weight in the model. See `docs/python-parity.md`.
- **Prebuilt binaries for six platforms**, published to GitHub Releases and self-contained
  so no .NET install is needed: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-arm64` and `osx-x64`, with `SHA256SUMS.txt`. Each is built and smoke tested on its
  own platform where a hosted runner exists; `win-arm64` and `linux-arm64` are
  cross-compiled because none is available.

- **Documentation.** `docs/` covers the algorithm, a full CLI reference, the model in
  depth, how to read the QC figures, the mzML passthrough contract, a map of the code, and
  how the implementation is verified against the Python one.

- **Cross-validation by default, with folds split by peptide.** MARS trains five models,
  one per fold, and every accuracy it reports comes from a model that never saw the peptide
  it is scoring. This matters because a peptide's fragments recur across hundreds of spectra
  with the same theoretical m/z, and `fragment_mz` is a feature: splitting rows rather than
  peptides lets the model memorize a peptide's error and report an accuracy it cannot reach
  on anything new. The report gives per-fold figures, the pooled out-of-fold figure, the
  spread across folds, and the gap between in-sample and out-of-fold accuracy. On the
  reference Stellar run that gap is 0.0015 Th, about 3% of the error being corrected, so the
  model generalizes rather than memorizes. `--cv-folds 0` restores a single fit; the held-out
  split in that mode is now also by peptide.
- **The applied model is the ensemble of the folds**, averaging their predictions, which is
  what Osprey's Percolator applies on its tree path. `--cv-model refit` instead fits one
  model on all rows after cross-validating, which corrects about five times faster: on one
  1.47 GB file, 266 s for the ensemble against 91 s for the refit and 52 s for a single fit.
  All three report the same accuracy; only the last reports it optimistically.
- **The QC report shows how much the folds disagree.** A per-fold table with a spread
  row, two figures plotting each fold's accuracy against the pooled figure with a
  one-standard-deviation band, and a plain-language reading of both the spread and the
  in-sample/out-of-fold gap. One held-out number says how the model did on one split;
  five say whether that number was luck.

## Bug Fixes

Four defects found while transcribing the Python implementation. Three affect files that
have already been written.

- **`mars.__version__` reported a version that never shipped.** `mars/__init__.py`
  declared `0.1.4` while `pyproject.toml` and the CLI both said `0.1.5`. All three now read
  the installed distribution metadata, so there is one source of truth. Python package only.

- **The `fileChecksum` written by the Python implementation is invalid.** It stops the SHA-1
  two bytes early, before the indentation preceding `<fileChecksum>`, where the mzML
  convention is to hash up to and including the opening tag. Every mzML the Python
  implementation has written fails checksum validation. The C# writer uses the correct
  convention and `mars verify` checks it.
- **`absolute_time` was re-based for training but not for correction.** The Python
  implementation subtracts the earliest acquisition before fitting, then feeds raw Unix
  timestamps back in when writing, so every inference row landed above the largest value the
  model had seen and the feature collapsed to a single branch. The offset now travels with
  the model and is subtracted again at correction time.
- **The TIC features were computed from different quantities in the two paths** - the summed
  intensity array when training, the `total ion current` cvParam when correcting, which
  differ on Thermo centroided data. Both paths now use the summed array.
- **A `.blib` with no peak annotations produced a meaningless model.** Every peak became a
  pseudo-fragment matched on its observed reference m/z, which measures the difference
  between two runs' calibration errors rather than an absolute mass error. MARS now refuses
  such a library and names the alternatives. Annotated peaks have their b and y m/z
  recomputed from the sequence including modification deltas, where the Python
  implementation recomputes from the stripped sequence and so gets modified peptides wrong.

Both of the middle two are reproducible with `--python-compat` for A/B comparison.

## Performance

Measured on 16 logical cores.

- **Null-correction round trip: 6.9 s for a 1.2 GB file** (176 MB/s), verifying 56,972,925
  peaks as bit-identical.
- **Full `calibrate` over the 5-file, 6.0 GB Stellar cohort: 229 s**, of which 13 s is
  training on 282k rows by 22 features.
- **Astral plate: 369 s end to end** - 97 s to read a 16.1 GB, 67,119,180-row Skyline
  report, 49 s to match each 4.7 GB run, 125 s to train on 3.37M rows.
- **Duplicate transitions collapsed.** A Skyline report lists every transition once per
  replicate; collapsing the exact duplicates cut matching work roughly threefold on the
  Astral plate (1,462,106 collapsed) with no effect on what the model learns.

## Breaking Changes

- **Model files now carry an ensemble.** The format is version 2: `models` replaces the
  single `model`, one entry per fold. Version 1 files still load, as a one-element
  ensemble, and predict identically. A five-fold model file is about five times larger.
- **Model files are not interchangeable with the Python implementation.** The Python model
  is a pickle of an XGBoost booster; the C# model is versioned JSON. Retrain rather than
  convert.
- **Corrected mzML files are not byte-identical to the Python implementation's output**, and
  are not byte-identical across platforms either, because runtimes ship different zlib
  builds. Decoded m/z and intensity values are identical, which is what any consumer reads.
  Use `mars compare` rather than `cmp` to compare two files.
