# MARS vNEXT Release Notes

MARS reads Thermo, Bruker and Sciex data directly and writes mzXML and mzMLb as well as
mzML, so a run can be calibrated straight off the instrument with no conversion step. It also
configures itself from the file: the mass analyzer decides the fragment tolerance and the units
the QC report is drawn in.

## New Features

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

- **Ion injection time is only used as a feature when it varies.** MARS asked whether the run
  recorded one; it now also checks that it moves. A trap sets it per spectrum from its gain
  control, so it carries information. An instrument that accumulates for a fixed period reports
  the same number every time, and then `injection_time` is a constant a tree can never split
  on, while `tic_injection_time` is TIC times that constant - `log_tic` rescaled, and a
  duplicate that splits permutation importance with the feature it duplicates. Both are dropped
  together, with a line saying which case it was.

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

- **The injection-time features were being switched off on data where they vary.** This
  changes written output, and it is the largest accuracy change in this release. Two faults
  compounded:

  MARS decided whether the ion injection time varies by sampling the **first 500 MS2 spectra**.
  An ion trap holds its injection time at the method's ceiling until the trap actually fills,
  which on a gradient means the entire void volume - so the head of a run is the one stretch
  that cannot show variation, whatever the run does later. Every Stellar file tested reads as
  constant over its first few hundred spectra and varies well before the end:

  | Stellar run | distinct MS2 injection times | spectra off the ceiling | first at |
  |---|---|---|---|
  | HeLa GPF-DIA, 5-file reference cohort | 6,937 | 6% | spectrum 11,060 |
  | HeLa standard 4 m/z DIA | 65,059 | 67% | spectrum 9,253 |
  | 1 Th GPF-DIA | 2,033 | 1.8% | spectrum 8,239 |

  Whether the injection time varies is now decided from the whole matched column, which
  matching has already produced, rather than from a sample taken before it. Nothing extra is
  read.

  Second, that verdict was also switching off `fragment_ions`, the six
  `ions_above_`/`ions_below_` windows and the six `adjacent_ratio_` features - which was wrong
  independently of the sampling. They are peak sums over m/z windows, scaled by the injection
  time to turn a rate into a count: they need an injection time to exist, but not to vary,
  because a constant multiplies them all by the same factor and leaves every split available.
  They are now kept whenever the run records an injection time at all.

  On the five-file Stellar reference cohort, measured by rematching the library against the
  written files:

  | | before | after |
  |---|---|---|
  | Features | 5 | 20 |
  | MAD delta m/z | 0.0581 Th | **0.0464 Th** |
  | Std delta m/z | 0.0986 Th | **0.0872 Th** |
  | Out-of-fold MAD | 0.0589 Th | **0.0452 Th** |
  | Out-of-fold r | 0.5134 | **0.6791** |

  The out-of-fold numbers move with the rest, so this is predictive accuracy rather than a
  closer fit to the training rows. Almost all of the gain on this cohort comes from the
  ion-population features - `ions_above_0_1` carries the highest permutation importance of any
  feature in the model - because only 6% of its spectra are off the ceiling. On a run where
  two thirds of them are, the injection time itself should matter more; that has not been
  measured, for want of an annotated library for those files.

  Astral data is unaffected: its injection time varies within the first few hundred spectra, so
  it was already getting all 20 features. Same file before and after, the trained model is
  byte-identical.

  This restores agreement with the Python implementation rather than departing from it. Python
  keeps these features whenever a run records an injection time, without asking whether it
  varies, and logs `Using 20 features` on this cohort - the same 20 C# now selects. Re-running
  both over the cohort and scoring each implementation's written files with the same `mars qc`:

  | measured on the written files | uncorrected | Python | C# |
  |---|---|---|---|
  | MAD delta m/z | 0.0800 Th | 0.0472 Th | **0.0464 Th** |
  | Std delta m/z | 0.1180 Th | 0.0882 Th | **0.0872 Th** |
  | Median delta m/z | −0.0082 Th | −0.0046 Th | **−0.0025 Th** |

  Python's figures land within 0.0001 Th of the run recorded in
  [the port spec](../docs/dotnet-port-spec.md) before any of this, which is the control that
  makes the comparison meaningful: the methodology is unchanged, so the movement is in C#.

  A run that records no injection time at all still drops all of these, as it must - there is
  nothing to turn a rate into a count with. **Models trained by an earlier build of this
  release should be retrained**; files corrected with one are under-corrected rather than wrong.

- **Cross-validation folds could be split by the wrong peptide.** A library entry that
  collected no fragments is dropped as it is read, and every per-entry array shed it except
  the peptide group. After the first dropped entry the groups were off by one, so fold
  assignment used a neighbouring peptide's group - which is exactly the leak the grouped split
  exists to prevent, and it would have reported a held-out accuracy better than the truth. No
  reference library in the test set drops an entry, so the published numbers are unaffected;
  a PRISM CSV whose rows all lack a product m/z for some precursor would have triggered it.

- **`--threads` had no effect on the mzML write path.** The worker count was computed and then
  discarded, leaving concurrency bounded only by the 512-deep read-ahead queue, so `--threads 1`
  ran up to 16 spectra at once. Output was never affected - results are written in submission
  order and the correction is per-spectrum - but a user limiting MARS to one core on a shared
  machine did not get one core.

- **`mars apply`, `verify` and `compare` accepted unknown options in silence.** Only
  `calibrate` and `qc` ran the check, so a typo on the other three did what it was introduced
  to prevent: the run completed having quietly used a default. `apply` now also runs the check
  before loading the model, so a typo costs no time.

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

- **A mistyped option now stops the run instead of being ignored.** Unrecognized options were
  reported as a warning *after* the command finished, so `--tolernace-ppm 10` silently
  calibrated against the 0.3 Th default and `--output-dir` on `mars qc` wrote the report to
  the current directory. MARS now refuses unknown options before doing any work and suggests
  the nearest real one. The set of valid options is whatever the command reads, so it cannot
  drift from the code; a test passes each command its full documented option set and asserts
  none is rejected.

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

- **`--resolution` was rejected as a typo on `qc` and `calibrate`.** Moving analyzer detection
  onto the readers meant the option was read after the unknown-option check rather than before
  it, and that check learns an option is real by watching it be read. Exactly the hazard its own
  documentation warns about. The options it resolves are now declared before the check runs.

  The test that exists to prevent this did not, because its list of each command's options was
  hand-written and had drifted - `--resolution` was added to the CLI and not to the list. It now
  scrapes each command's own `--help` output, so it cannot fall behind what it is testing.

- **A mistyped option crashed instead of reporting an error.** The refusal added above is
  raised by throwing, and `Program` was not catching it, so a typo produced a stack trace
  where a one-line message belonged. Now caught and reported as an input error, with a test
  that goes through `Program.Main` rather than around it.

- **The cross-validation gap in the HTML report had the wrong sign.** It was rendered as
  in-sample minus out-of-fold, the reverse of how `CrossValidationReport.OptimismMad` defines
  it, so the figure appeared negative. The text summary was always correct.

- **`mars verify` could destroy its input.** Passing `--output` pointing at the input file
  round-tripped the file onto itself and then deleted it, since `verify` removes its output
  unless `--keep` is given. It now refuses when input and output resolve to the same path -
  losing raw data to the one command whose purpose is to prove nothing was lost was the worst
  possible failure for it to have.

## Performance

- **`--threads` now defaults to `auto` and says what it picked.** The default was already one
  worker per logical processor, but nothing reported it and the help text did not mention it,
  so there was no way to tell what a run had used. It is now named (`--threads auto`), reported
  in the log, and documented.

  Whether the extra hardware threads of an SMT processor earn their place is usually asserted
  rather than measured, so it was measured - correcting and rewriting one 1.2 GB Stellar run on
  an 8-core i9-9900K with 16 logical processors, best of two passes run in both directions:

  | threads | 2 | 4 | 6 | 8 | 10 | 12 | 16 |
  |---|---|---|---|---|---|---|---|
  | seconds | 150.5 | 77.4 | 52.5 | 45.4 | 42.8 | 38.7 | 36.8 |

  All 16 are 24% faster than the 8 physical cores, so the default keeps using them. No upper
  ceiling is imposed: the curve is shallow past 8 and the writer's in-order drain has to become
  the limit on a large enough machine, but where that falls has not been measured and a guessed
  ceiling would be worse than none.

  A count below 1 is now refused rather than silently meaning "all of them" - `--threads $N`
  with `N` unset should report the mistake - and a count above the processor count is honoured
  with a warning. Neither affects the output: thread count changes how long a run takes and
  nothing else.


Measured on an i9-9900K (16 threads), sequentially so the numbers are not contention:

| Run | Files | Input | Wall |
|---|---|---|---|
| `qc`, Stellar | 5 | 6.6 GB | 63 s |
| `qc`, Astral | 1 | 4.9 GB | 119 s |
| `calibrate --no-recalibrate`, Stellar | 5 | 6.6 GB | 140 s |
| `calibrate --no-recalibrate`, Stellar | 1 | 1.5 GB | 53 s |
| `calibrate` writing corrected mzML, Stellar | 5 | 6.6 GB | 263 s |

Writing the corrected files roughly doubles a Stellar run: 140 s of matching and training,
then 123 s to write 8.4 GB of mzML. The Astral figure is dominated by the library rather than
the data - 74 s of its 119 s is reading a 16 GB plate-scale PRISM CSV, against 41 s to match
the 4.9 GB run itself.

## Breaking Changes

- **`mars qc` and `mars calibrate` pick a different default tolerance on high-resolution
  data.** A run that previously relied on the 0.3 Th default against Orbitrap, TOF or Astral
  data will now match at 10 ppm and produce different - substantially better - numbers. Pass
  `--tolerance 0.3` or `--resolution unit` to keep the old behavior.

- **An unrecognized option is now an error (exit 1) rather than a warning.** A script passing
  an option MARS does not understand will stop instead of silently continuing with defaults.
