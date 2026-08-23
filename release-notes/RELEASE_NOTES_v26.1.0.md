# MARS v26.1.0 Release Notes

MARS is now a self-contained, cross-platform command-line tool that reads Thermo, Bruker and
Sciex data directly, so a run can be calibrated straight off the instrument with no conversion
step. This is the first release of the C# implementation, which is MARS going forward. The
Python implementation is frozen to bug fixes and will be archived once the C# one has been used
in earnest; it is no longer published to PyPI.

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
  spread across folds, and the gap between the two. On the reference Stellar run that gap is
  0.0014 Th, about 3% of the error being corrected, so the fit is describing the instrument
  rather than the particular peptides in the run; on a data-poor window of the same cohort it
  is 20%, which is a real warning that the fit is thin. `--cv-folds 0` skips cross-validation
  and reports only the in-sample figure; the held-out split in that mode is also by peptide.
- **The correction model is fitted to all the data, and the report gives two numbers.**
  Calibration is in-sample by nature - it is what mass calibration has always been, measuring
  known species present in the run and correcting the axis from them - and the correction
  moves a peak onto a fitted surface rather than onto its theoretical m/z, so there is little
  scope to memorize individual peaks. The report therefore states both what the correction
  achieved on these files and what cross-validation says it would achieve on a run it was not
  fitted to, labelled:

  ```
  After Calibration (these files, corrected):   MAD 0.0431 Th
  Expected on data not used to fit:             MAD 0.0445 Th
  ```

  Cross-validation costs a few extra training rounds and nothing at correction time: 66 s
  against 52 s for `--cv-folds 0`, on one 1.47 GB file.
- **The QC report shows how much the folds disagree.** A per-fold table with a spread
  row, two figures plotting each fold's accuracy against the pooled figure with a
  one-standard-deviation band, and a plain-language reading of both the spread and the
  in-sample/out-of-fold gap. One held-out number says how the model did on one split;
  five say whether that number was luck.

- **`--robust` (default `trim`) fits twice**, dropping training rows the first pass could not
  explain. Matching takes the most intense peak in the tolerance window and sometimes that
  peak is not the fragment; those rows carry a delta that is not a mass error, and squared
  error lets them pull the fit. They are identifiable as a population - on the reference
  Stellar run the 7.6% of rows with a residual beyond 0.15 Th are three times weaker, sit in
  spectra with a quarter as many fragment ions, and are seven times more likely to lie
  against the edge of the matching window. Removing them improves out-of-fold accuracy from
  0.0445 to 0.0442 Th, and the improvement is flat from 2 to 3 sigma, which says it is
  removing a contaminant rather than tuning against the folds. Only training rows are
  trimmed; held-out rows are always scored in full. `--robust none` disables the second
  pass, and `--robust-sigma` moves the threshold.
- **`--robust huber`** is available and measures slightly worse, which is worth knowing:
  a robust loss assumes an outlier is an extreme measurement of the right quantity, but a
  mismatched peak is an accurate measurement of a different ion, and at three robust sigma
  Huber still leaves such a row 79% of its weight. It is the better choice when the tail
  is heavy but real rather than mislabelled.
- **Verified on high-resolution data.** One 4.9 GB Orbitrap Astral run at `--tolerance-ppm 10`
  against a 16.1 GB, 67-million-row Skyline report: 1,408,902 matched fragments over 81,184
  peptides, seven minutes end to end. The correction is small and honestly reported as such -
  5.6% off the median absolute error, mostly by removing a -1.5 ppm constant offset - and
  cross-validation puts the in-sample and out-of-fold figures within 0.0000 Th of each other,
  so the small gain is real rather than an artifact. Pearson r is 0.144 against 0.69 on
  Stellar, which is the tool correctly reporting that a well-calibrated instrument leaves
  little systematic error to find.

- **The fragment tolerance is chosen from the file.** MARS reads the mass analyzer from the
  mzML's `instrumentConfiguration` and defaults to 0.3 Th on an ion trap or quadrupole and
  10 ppm on an orbitrap, FT-ICR, TOF or Astral. It says which in the log. `--resolution
  unit|hram|auto` forces the choice; `--tolerance` and `--tolerance-ppm` still override
  everything, because detection can be wrong on a file MARS has not seen the shape of and
  the person running it can be certain in a way a heuristic cannot.

  Detection reads the analyzer for the **MS2** spectra specifically, which on a hybrid
  instrument is not the run default. An Orbitrap Astral file declares an orbitrap as the run
  default because that takes the MS1 survey, and points only its MS2 spectra at the Astral
  analyzer. MS2 is what MARS calibrates, so that is what decides.

  This matters more than it looks, because getting it wrong is quiet. Matching the Astral
  test file at the old 0.3 Th default returns 3,414,802 fragments rather than 1,408,902 - a
  window about 430 ppm wide at m/z 700, filled with wrong matches - and reports a standard
  deviation of 162 ppm against the 4.1 ppm really there. The run completes, writes corrected
  files and produces a full report, all of it meaningless.

- **QC reports are drawn in the units the instrument is specified in.** On high-resolution
  data every axis, table and verdict is now in ppm; on trap data they stay in Th. The text
  summary reports both scales side by side either way. Conversion is per row from each
  fragment's own m/z, not an aggregate divided by a nominal mass - the fragments in one run
  span most of a factor of four in m/z, so the shortcut would be wrong at both ends. The two
  columns are therefore summaries of different per-row quantities rather than rescalings of
  one another.

- **Density figures use a viridis color scale.** The feature-versus-error panels were a
  single-hue blue ramp, which has one usable dimension and spends most of it on pale values,
  so the dense core and the sparse tail looked alike. They now run dark purple through green
  to yellow, with a fragment-count colorbar, before and after correction side by side. Each
  panel is normalized to its own busiest cell, because correcting concentrates the
  distribution and a shared count scale would flatten the before panel to nearly empty; both
  peaks are printed so the difference is not hidden. Both panels share one vertical range,
  because the after panel being visibly tighter is the result.

  Counts map onto the ramp as a power law (`count / peak` to the 0.4, as in matplotlib's
  `PowerNorm`) rather than linearly or logarithmically. Linear leaves one bright cell in a
  dark field, since the core of these densities runs orders of magnitude above the tails; a
  log overcorrects, putting a 500-count cell at 0.78 of the ramp against a peak of 2,854 so
  that most of the core saturates and the structure inside it washes out. The power law puts
  that same cell at 0.50.

- **Titles and axis labels read as prose.** `log_intensity` renders as "log10 peak
  intensity", `tic_injection_time` as "TIC x injection time", and the space-charge features
  as "ions above +1 to 2 Th". The underscored names stay exact everywhere they are data -
  the model file, the CSV dumps, the Python parity comparison - because they are identifiers
  there. Type is larger throughout.

- **timsTOF frames are collapsed rather than modelled.** pwiz presents an uncombined TIMS
  frame as hundreds of spectra sharing one retention time and one isolation m/z, separated only
  by mobility - ProteoWizard's `diaPASEF.d` is 4,631 spectra at five distinct scan times, whose
  first MS2 holds two peaks. MARS asks pwiz to combine each frame's mobility scans into one
  spectrum per isolation window: the same file becomes 8 MS2 across 8 isolation windows and 4
  retention times, and that first spectrum becomes 8,377 peaks.

  This is what makes Bruker data usable rather than merely tidy. MARS computes twelve of its
  features from the peaks surrounding each match, and a two-peak mobility slice has no
  neighbours to measure. Reading and writing both combine, so what is written matches what was
  modelled. Data without an ion mobility stage is unaffected.

- **Ion injection time earns its place feature by feature, decided over the whole run.** A trap
  sets it per spectrum from its automatic gain control, so it says how full the trap was. An
  instrument that accumulates for a fixed period reports the same number every time, and then
  `injection_time` is a constant a tree can never split on while `tic_injection_time` is
  `log_tic` rescaled - a duplicate that splits permutation importance with the feature it
  duplicates. Those two are dropped when the value does not move.

  The features the injection time merely *scales* are kept. `fragment_ions`, the six
  `ions_above_`/`ions_below_` windows and the six `adjacent_ratio_` features are peak sums
  multiplied by it to turn an ion rate into an ion count: they need an injection time to exist,
  but not to vary, because a constant multiplies them all alike and leaves every split a tree
  could make still available. A run recording no injection time at all - Bruker and Sciex files
  are like this - loses all fifteen, since nothing then turns a rate into a count.

  Whether it varies is decided from every matched row rather than from a sample of the head of
  the run. An ion trap holds its injection time at the method's ceiling until the trap actually
  fills, which on a gradient is the entire void volume, so the start of a run is the one stretch
  that cannot show variation. Every Stellar file tested reads as constant there and varies well
  before the end:

  | Stellar run | distinct MS2 injection times | spectra off the ceiling | first at |
  |---|---|---|---|
  | HeLa GPF-DIA, the reference cohort | 6,937 | 6% | spectrum 11,060 |
  | HeLa standard 4 m/z DIA | 65,059 | 67% | spectrum 9,253 |
  | 1 Th GPF-DIA | 2,033 | 1.8% | spectrum 8,239 |

  This decides most of the correction rather than a detail of it. On the reference cohort,
  keeping the ion-population features puts the corrected MAD at 0.0464 Th against 0.0581
  without them, and the out-of-fold correlation at 0.679 against 0.513; `ions_above_0_1` alone
  carries the highest permutation importance of any feature in the model.

  It also puts MARS on the same 20 features the Python implementation selects here - Python
  keeps these whenever a run records an injection time, without asking whether it varies.
  Running both over the cohort and scoring each one's written files with the same `mars qc`:

  | measured on the written files | uncorrected | Python | C# |
  |---|---|---|---|
  | MAD delta m/z | 0.0800 Th | 0.0472 Th | **0.0464 Th** |
  | Std delta m/z | 0.1180 Th | 0.0882 Th | **0.0872 Th** |
  | Median delta m/z | -0.0082 Th | -0.0046 Th | **-0.0025 Th** |

  Python's figures land within 0.0001 Th of the run recorded in
  [the port spec](../docs/dotnet-port-spec.md), which is the control that makes the comparison
  meaningful: the methodology is unchanged, so the difference is in what MARS does.

- **Bruker and Sciex read directly too**, alongside Thermo. Bruker `.d`, `.tdf`, `.tsf` and
  `.baf` on Windows and Linux; Sciex `.wiff` and `.wiff2` on Windows, which is as far as that
  SDK goes. Bruker and Agilent runs are directories rather than files, and `--mzml`,
  `--mzml-dir` and bare arguments all accept them.

  Verified against ProteoWizard's own vendor test files - a Bruker `diaPASEF.d`, a ZenoTOF 7600
  `.wiff2`, a SWATH `.wiff2` and a legacy `.wiff`. Bruker records no ion injection time; MARS
  turns that feature group off, as it already does for an mzML without it.

- **Thermo `.raw` read directly.** `qc`, `calibrate` and `apply` open a Thermo raw file
  without a conversion step, through the pwiz-sharp vendor reader. `--mzml`, `--mzml-dir` and
  bare file arguments all accept it; a directory now picks up every format MARS can read.

  It gives the same answer as the converted mzML. The same Astral run matched against the same
  DIA-NN library returns 230,781 fragment matches either way, with the same median, standard
  deviation and MAD to every reported digit. The mass analyzer is detected from the vendor file
  as it is from an mzML, so an Astral raw picks 10 ppm and a ppm-scaled report on its own.

  Reading a raw is not faster - 53 s against 15 s for the converted mzML on that run, and
  vendor reading does not thread - so what is saved is the conversion and its intermediate
  file, not the read.

  mzML written from a raw is built by pwiz rather than spliced, because there is no input mzML
  to copy: the passthrough guarantee covers mzML in and mzML out, and is not claimed otherwise.

  Thermo only for now. Other vendors are recognized well enough to report what is missing
  rather than "unrecognized file".

- **`--output-format mzXML`, `mzMLb` or `mgf`,** on `calibrate` and `apply`. mzML remains the
  default and is still written by MARS's own byte-splice writer, which copies the input and
  replaces only the m/z arrays it corrected. The other formats have no input to splice into,
  so they are serialized by [pwiz-sharp](https://github.com/ProteoWizard/pwiz/pull/4178) - the
  same code msconvert uses, and the code that wrote the mzML MARS reads in the first place.

  Both paths run the same correction over the same values. Writing one Stellar file both ways
  and diffing with `mars compare` finds no difference: 114,021 spectra, 82,349,582 peaks, zero
  m/z values differing. mzMLb is worth a look on size alone - 0.56 GB where the input was
  1.22 GB.

  The binary encoding is read from the input and matched per array. Left to its defaults pwiz
  writes 64-bit *uncompressed*, which inflated a Stellar run by 61%.

  The pwiz reference is **optional**: pwiz-sharp has no package feed yet, so a MARS built
  without a checkout writes mzML exactly as before and refuses the other formats with an
  explanatory error. Build with `-p:PwizSharpDir=<path>/pwiz/pwiz-sharp` to enable them.

- **The pwiz write is parallel.** Scoring the model is where the time goes - on one Astral
  run, 243 s of 308 s, against 17 s reading and about 49 s encoding - and pwiz's writers pull
  spectra one at a time, so it was all landing on one core. MARS now reads a batch ahead and
  corrects the batch in parallel, honouring `--threads`. That run goes from **318 s to 103 s**.

  Reads stay sequential: 5% of the work, and the vendor readers are not thread-safe. Only the
  correction is parallel, and it is embarrassingly so - each spectrum is independent and
  `SpectrumCorrector` holds nothing mutable. An mzXML written on 1 thread and on 12 hashes
  identically.

  Worth knowing: **mzMLb is not byte-reproducible**, and not because of anything MARS does.
  Two mzMLb writes of identical data at the same thread count differ, because the HDF5
  container records things that vary between writes. mzML and mzXML are reproducible.

- **`mars --version` reports what the binary can do.** Vendor reading and the non-mzML
  outputs depend on how a build was made and where it runs, and two identically named binaries
  were otherwise indistinguishable until one refused a file:

  ```
  26.1.0
  reads:  .mzML, .raw, .wiff, .wiff2, .d, .tdf, .tsf, .baf
  writes: mzML, mzXML, mzMLb, mgf
  ```

  It reports what is actually usable rather than what is recognized: a build without
  pwiz-sharp says `.mzML` and `mzML`, an arm64 build drops Bruker, Sciex and mzMLb because
  those need native x64 libraries, and `.lcd` is never advertised because no build carries a
  Shimadzu reader - it is only recognized well enough to be refused with a reason.

- **MARS warns when the matching window is far wider than the error in the data.** A tolerance
  set for the wrong instrument fails silently - the window fills with peaks that are not the
  fragment, and the run completes and reports numbers regardless - where one that is too narrow
  fails loudly with too few matches. Only the silent direction needs catching, so after matching
  MARS compares the window against the median absolute error and says so when it is more than
  50x. Trap data at its correct 0.3 Th sits around 4x, so this cannot fire on the case MARS was
  built for.

  Prompted by a real case: a ZenoTOF 8600 reads correctly but reports no analyzer, because
  pwiz's Sciex model table stops at the 7600. MARS then has nothing to detect from and falls
  back to 0.3 Th - about 760 ppm at m/z 400 on a TOF. See `docs/open-questions.md`.

- **Profile spectra are centroided by the vendor before use.** Sciex writes profile data - the
  ZenoTOF 8600 file is 1,619 evenly spaced points in one MS2, at 0.00233 Th, which is 16 ppm at
  m/z 142. MARS measures mass error by taking the most intense peak in a window, so on a sampled
  curve the answer is quantised to the grid and the floor on measurable error would be several
  times the error the instrument has; the twelve space-charge features would be counting
  samples of one ion rather than neighbouring ions.

  pwiz exposes the vendor's own algorithm, and MARS uses it - "ABI/Analyst peak picking" here,
  turning that spectrum into 210 peaks. Applied on reading and writing alike, because the model
  is fitted on peak lists and correcting sampled curves with it would put every feature outside
  what it saw. Only when the spectrum declares itself profile: Thermo and Bruker already deliver
  centroids and are untouched.

  A corrected file written from profile input therefore comes out centroided. That is what
  `msconvert --filter peakPicking` does routinely, but it is a change to the data rather than
  only to the m/z values.

## Bug Fixes

Defects in the Python implementation that MARS does not reproduce, and defects in this
implementation caught before it shipped. Nothing in the second group ever reached a release;
they are listed because several of them changed what a corrected file contains, and because how
a tool fails is worth knowing.

The first group came out of transcribing the Python implementation. Three of them affect files
that have already been written.

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

The two that change training - the `absolute_time` re-basing and the TIC features - are
reproducible with `--python-compat` for A/B comparison.

- **A mistyped option stops the run instead of being ignored.** Unrecognized options were
  reported as a warning *after* the command finished, so `--tolernace-ppm 10` silently
  calibrated against the 0.3 Th default and `--output-dir` on `mars qc` wrote the report to the
  current directory. Every command now refuses an unknown option before doing any work,
  suggests the nearest real one, and reports it as an input error rather than a stack trace.

  The set of valid options is whatever the command reads, which cannot drift from the code -
  and is also the trap in it, since an option resolved late has not been read when the check
  runs. `--resolution` was rejected as a typo for exactly that reason; options resolved after
  the check are now declared before it. Each command is tested against the options in its own
  `--help` output rather than a hand-written list, and against a deliberate typo, so neither
  half of that can fall behind.

- **Cross-validation folds could be split by the wrong peptide.** A library entry that
  collected no fragments is dropped as it is read, and every per-entry array shed it except
  the peptide group. After the first dropped entry the groups were off by one, so fold
  assignment used a neighbouring peptide's group - which is exactly the leak the grouped split
  exists to prevent, and it would have reported a held-out accuracy better than the truth. No
  reference library in the test set drops an entry, so the published numbers are unaffected;
  a PRISM CSV whose rows all lack a product m/z for some precursor would have triggered it.

- **`--cv-folds 0` reported no ppm figures.** The single-fit evaluation path was handed the
  per-row ppm scale and ignored it, so `beforePpm` and `afterPpm` came back empty on
  high-resolution data - the units that data is read in.

- **A `.raw` and its converted mzML could disagree on acquisition time.** The pwiz adapter
  parsed a timestamp with no UTC offset as machine-local where the mzML reader assumes UTC, so
  the `absolute_time` feature shifted by the machine's offset. Both now use one routine.

- **A named modification in a `.blib` no longer produces a confidently wrong fragment m/z.**
  `C[Carbamidomethyl]` carries no mass, and it was dropped silently: the residue kept its
  unmodified mass and every fragment past it came out wrong by the delta. With no Modifications
  table to fall back on, MARS now keeps the m/z the library recorded and says how many entries
  that applied to.

- **A cohort mixing instruments is now reported.** One fragment tolerance is chosen for the
  whole run from the first file's analyzer, so a directory holding both trap and
  high-resolution data had one of them matched at the wrong width without saying so. The
  injection-time probe now reads every file rather than the first.

- **Applying a temperature-trained model without temperature data now warns.** The features
  were substituted the way training substitutes a missing one and the run completed, with two
  features pinned to a value no real spectrum produced and nothing in the output saying so.

- **`mars verify` no longer allocates the whole file on a corrupt index offset.** The offset is
  read out of the file being validated, which is exactly the file that cannot be trusted, and a
  small one had the validator try to read the entire run as an index list.

- **`mars compare` stops when the files stop lining up.** It pairs spectra by position and
  checks the ids agree, which is right for comparing a file against a correction of itself, but
  it cannot realign. One inserted or removed spectrum put every later pair against the wrong
  spectrum and counted each as a difference, so two files differing by one spectrum reported as
  differing everywhere. It now reports where alignment was lost and stops, and says the counts
  cover only what preceded it. The doc comment claimed it matched by id, which it never did.

- **`--dump-predictions` validates its array up front.** A predictions array not parallel to
  the match table failed partway through writing millions of rows, leaving a half-written dump
  and an index-out-of-range naming nothing.

- **Retention time was read 60x too large from any vendor that records it in seconds.** The
  pwiz adapter took the scan-start-time value and assumed minutes. Thermo writes minutes so
  nothing showed; Bruker writes seconds, and a 64-minute diaPASEF run came back as 64 hours,
  which would have gone into the absolute_time feature and out again as noise. Both adapters
  now honour the unit the cvParam declares. Found by testing a second vendor.

- **Numbers could have been parsed and written in the machine's locale.** MARS got its
  locale-independence from `InvariantGlobalization`, which had to be relaxed for builds
  carrying a vendor reader - the Thermo SDK constructs `CultureInfo("en-US")` and throws when
  cultures are unavailable. Relaxing it hands `CurrentCulture` back to the operating system.

  MARS now pins the invariant culture at startup instead, so ICU is available to the SDK while
  MARS's own numbers stay locale-independent. Two places that would have broken are fixed at
  the source as well: `mars verify` formatted a timestamp without a culture, and - the one
  that mattered - the BiblioSpec reader parsed numbers stored as SQLite text with the current
  culture. Under a German locale that turns a fragment m/z of 653.835516 into 653,835,516,
  with no error to show for it.

  A test class now runs the report writer, the model round-trip and the library reader under
  `de-DE`, and the test host is built culture-capable so this runs in CI too - the
  configuration where nobody would otherwise notice.

- **The cross-validation gap in the HTML report had the wrong sign.** It was rendered as
  in-sample minus out-of-fold, the reverse of how `CrossValidationReport.OptimismMad` defines
  it, so the figure appeared negative. The text summary was always correct.

- **`mars verify` could destroy its input.** Passing `--output` pointing at the input file
  round-tripped the file onto itself and then deleted it, since `verify` removes its output
  unless `--keep` is given. It now refuses when input and output resolve to the same path -
  losing raw data to the one command whose purpose is to prove nothing was lost was the worst
  possible failure for it to have.

## Performance

Measured on 16 logical cores.

- **Null-correction round trip: 6.9 s for a 1.2 GB file** (176 MB/s), verifying 56,972,925
  peaks as bit-identical.
- **Full `calibrate` over the 5-file, 6.0 GB Stellar cohort: 229 s**, of which 13 s is
  training on 282k rows by 20 features.
- **Astral plate: 369 s end to end** - 97 s to read a 16.1 GB, 67,119,180-row Skyline
  report, 49 s to match each 4.7 GB run, 125 s to train on 3.37M rows.
- **Duplicate transitions collapsed.** A Skyline report lists every transition once per
  replicate; collapsing the exact duplicates cut matching work roughly threefold on the
  Astral plate (1,462,106 collapsed) with no effect on what the model learns.

- **`--threads` is honoured, defaults to `auto`, and says what it chose.** On the mzML write
  path the worker count was computed and then discarded, leaving concurrency bounded only by
  the 512-deep read-ahead queue - `--threads 1` ran up to 16 spectra at once. Output was never
  affected, since results are written in submission order and the correction is per spectrum,
  but a user limiting MARS to one core on a shared machine did not get one core.

  The default was already one worker per logical processor, but nothing reported it and the
  help text did not mention it. Whether the extra hardware threads of an SMT processor earn
  their place is usually asserted rather than measured, so it was measured - correcting and
  rewriting one 1.2 GB Stellar run on an 8-core i9-9900K with 16 logical processors, best of
  two passes run in both directions:

  | threads | 2 | 4 | 6 | 8 | 10 | 12 | 16 |
  |---|---|---|---|---|---|---|---|
  | seconds | 150.5 | 77.4 | 52.5 | 45.4 | 42.8 | 38.7 | 36.8 |

  All 16 are 24% faster than the 8 physical cores, so the default keeps using them. No upper
  ceiling is imposed: the curve is shallow past 8 and the writer's in-order drain has to become
  the limit on a large enough machine, but where that falls has not been measured and a guessed
  ceiling would be worse than none. A count below 1 is refused rather than silently meaning
  "all of them" - `--threads $N` with `N` unset should report the mistake.

## Breaking Changes

- **Model files are format version 2**, adding the cross-validation summary. The version has
  to match exactly - a model written by any other format version is refused rather than read on
  a best guess - so a version 1 file has to be retrained. A cross-validated model file is about
  five times larger, because the merged model holds every fold's trees.
- **Model files are not interchangeable with the Python implementation.** The Python model
  is a pickle of an XGBoost booster; the C# model is versioned JSON. Retrain rather than
  convert.
- **Corrected mzML files are not byte-identical to the Python implementation's output**, and
  are not byte-identical across platforms either, because runtimes ship different zlib
  builds. Decoded m/z and intensity values are identical, which is what any consumer reads.
  Use `mars compare` rather than `cmp` to compare two files.

- **`mars qc` and `mars calibrate` pick a different default tolerance on high-resolution
  data.** A run that previously relied on the 0.3 Th default against Orbitrap, TOF or Astral
  data will now match at 10 ppm and produce different - substantially better - numbers. Pass
  `--tolerance 0.3` or `--resolution unit` to keep the old behavior.

- **An unrecognized option is now an error (exit 1) rather than a warning.** A script passing
  an option MARS does not understand will stop instead of silently continuing with defaults.
