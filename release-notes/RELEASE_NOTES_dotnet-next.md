# MARS .NET vNEXT Release Notes

First release of the C# implementation of MARS: the same recalibration, as a
cross-platform CLI with no Python installation.

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
- **Prebuilt binaries for six platforms**, published to GitHub Releases and self-contained
  so no .NET install is needed: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-arm64` and `osx-x64`, with `SHA256SUMS.txt`. Each is built and smoke tested on its
  own platform where a hosted runner exists; `win-arm64` and `linux-arm64` are
  cross-compiled because none is available.

## Bug Fixes

Four defects found while transcribing the Python implementation. Three affect files that
have already been written.

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

- **Model files are not interchangeable with the Python implementation.** The Python model
  is a pickle of an XGBoost booster; the C# model is versioned JSON. Retrain rather than
  convert.
- **Corrected mzML files are not byte-identical to the Python implementation's output**, and
  are not byte-identical across platforms either, because runtimes ship different zlib
  builds. Decoded m/z and intensity values are identical, which is what any consumer reads.
  Use `mars compare` rather than `cmp` to compare two files.
