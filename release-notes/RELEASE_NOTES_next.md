# MARS vNEXT Release Notes

MARS now reads the mass analyzer out of the mzML and configures itself for it, so
high-resolution data no longer needs to be told what it is.

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

## Bug Fixes

- **A mistyped option now stops the run instead of being ignored.** Unrecognized options were
  reported as a warning *after* the command finished, so `--tolernace-ppm 10` silently
  calibrated against the 0.3 Th default and `--output-dir` on `mars qc` wrote the report to
  the current directory. MARS now refuses unknown options before doing any work and suggests
  the nearest real one. The set of valid options is whatever the command reads, so it cannot
  drift from the code; a test passes each command its full documented option set and asserts
  none is rejected.

- **The cross-validation gap in the HTML report had the wrong sign.** It was rendered as
  in-sample minus out-of-fold, the reverse of how `CrossValidationReport.OptimismMad` defines
  it, so the figure appeared negative. The text summary was always correct.

## Performance

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
