# Spectral libraries

MARS needs a source of **theoretical** fragment m/z values. Everything downstream depends on
that word: the label is `observed - theoretical`, so if the "theoretical" side carries
measurement error of its own, the model learns to reproduce someone else's calibration
rather than to remove yours.

Four sources are supported, and they differ in how well they satisfy that requirement.

| Source | Option | m/z quality | Notes |
|---|---|---|---|
| Skyline PRISM report | `--prism-report` | Theoretical | Recommended. `.csv` or `.parquet`. Carries per-replicate RT windows. |
| DIA-NN library | `--library report-lib.parquet` | Theoretical | Needs `report.parquet` for RT windows. |
| BiblioSpec `.blib` | `--library lib.blib` | Recomputed from sequence | Requires peak annotations. |
| PRISM report as `--library` | `--library report.csv` | Theoretical | Same reader as `--prism-report`. |

`--prism-csv` is still accepted and does the same thing. The option was named before the report
could arrive as anything but CSV.

## Skyline PRISM report (recommended)

**This is the report Skyline exports, not anything PRISM produces.** The name is easy to read
the wrong way round: [PRISM](https://github.com/maccoss/skyline-prism) consumes a
transition-level Skyline report and writes normalised peptide and protein quantities, and it is
the *input* to that - the same file, exported with the same template - that MARS wants. PRISM's
own output has no fragment m/z in it and is no use here.

A Skyline transition report exported with the
[PRISM report template](../Skyline-PRISM-Report/Skyline-PRISM.skyr). `Product Mz` is
Skyline's computed theoretical value, which is exactly what MARS wants, and `Start Time` /
`End Time` give a real per-peptide elution window rather than a guess.

### CSV or parquet

Skyline writes whichever the output name asks for - it picks parquet from a `.parquet`
extension - and PRISM asks for parquet, because it is far smaller: the five-file Stellar report
here is 19.6 MB as CSV and 1.5 MB as parquet. MARS reads either, and decides which by looking
at the file rather than at its name, so a report is a report whatever it has been called.

The parquet carries the same columns with the spaces removed - `ProductMz` for `Product Mz` -
and native types where the CSV has text. A report that has been through a converter and kept
the spaced names is also accepted; the distinction carries no meaning.

Nothing else changes. The same report read either way produces the same library, which is
asserted in the test suite, and on the reference cohort both give byte-identical QC output.

Required columns:

```
Peptide Modified Sequence Unimod Ids, Precursor Charge, Precursor Mz,
Fragment Ion, Product Charge, Product Mz, Start Time, End Time
```

Optional: `Area` (carried as library intensity), `Retention Time`, `Protein Accession`,
`File Name`, `Replicate Name`.

Rows whose `Fragment Ion` is `precursor` are skipped - MARS corrects fragments.

### Replicate filtering and de-duplication

A Skyline report lists every transition once per replicate. When several runs are processed
together, that means the same theoretical m/z appears many times over.

MARS matches the report's `File Name` (or `Replicate Name`) against the mzML files being
processed, trying an exact base-name match first and falling back to a substring test, and
then **collapses transitions that repeat across replicates**. The copies are exact
duplicates - identical theoretical m/z, identical ion annotation - so keeping them would
multiply matching work and training rows without adding information.

On the reference Astral plate this collapsed 1,462,106 duplicate transitions out of
2.2M rows, cutting matching work roughly threefold. Pass `--no-dedupe-library` to keep them.

> The Python implementation does not de-duplicate, which is why its Astral run reports
> 9.1M matches where the C# reports 4.2M. Duplicating every row uniformly does not change
> what the model learns.

Files this large stream rather than load: the reference plate report is 16.1 GB and
67,119,180 rows, read in 97 seconds.

## DIA-NN

Two files are needed, and they are not interchangeable:

- **`report-lib.parquet`** - the spectral library, carrying `Product.Mz`, `Fragment.Type`
  and the rest of the fragment information.
- **`report.parquet`** - the per-run identifications, carrying `RT.Start` and `RT.Stop`.

```bash
mars calibrate --mzml-dir runs/ \
  --library out/report-lib.parquet \
  --diann-report out/report.parquet
```

`report.parquet` is found automatically if it sits beside the library. Handing MARS a
`report.parquet` where the library belongs is the usual mistake, and the error message says
so by name rather than listing missing columns.

RT windows are widened across the runs being processed, so a spectrum from any of them falls
inside the window.

> This reader has been exercised against parquet files written by the test suite, not against
> real DIA-NN output. Treat the first run against a real DIA-NN result as a check of the
> reader as much as of the data.

### Compression codecs on Arm Windows and Intel macOS

`Parquet.Net` delegates some codecs to a native library that is only published for
`win-x64`, `linux-x64`, `linux-arm64` and `osx-arm64`. On the other two platforms MARS
ships for - **Windows on Arm and Intel macOS** - that library is absent, and parquet falls
back to managed implementations for most codecs:

| Codec | Without the native library |
|---|---|
| uncompressed, Snappy, Gzip, Brotli, Zstd | works |
| LZ4, LZO | fails: "No compression codec for LZ4 is available on this platform" |

Snappy is parquet's usual default and what DIA-NN writes, so this is unlikely to bite. If
it does, the message names the codec, and the fix is to read the library on another
platform or re-export it with Snappy. Nothing else in MARS is affected: PRISM CSV, `.blib`
and every part of mzML processing are pure managed.

## BiblioSpec (.blib)

A `.blib` stores the **observed** m/z of each reference peak. Used directly, matching against
it measures the difference between two runs' calibration errors rather than an absolute mass
error - which is not what MARS is for.

So for annotated peaks MARS recomputes b and y fragment m/z from the peptide sequence, using
the per-position mass deltas in the library's own `Modifications` table. That yields a real
theoretical value, modifications included.

> The Python implementation recomputes from the *stripped* sequence, discarding
> modifications, so every fragment of a modified peptide that spans the modified residue gets
> a theoretical m/z wrong by the modification mass.

With no `Modifications` table to read, MARS falls back to the mass deltas written into the
modified sequence itself - `M[+15.9949]` gives one, `M[Carbamidomethyl]` and `M(unimod:35)`
name a modification without saying what it weighs. Where an entry carries a modification MARS
cannot weigh, its recorded m/z is used as-is rather than recomputed, and the count of such
entries is reported. Recomputing from a sequence with a modification silently dropped does not
produce a missing answer, it produces a confident wrong one: the residue keeps its unmodified
mass and every fragment past that position is off by the delta.

**Peaks the library does not annotate are skipped by default.** There is no way to know which
fragment ion an unannotated peak is, so its only available m/z is the observed one. A library
with hundreds of unannotated peaks per spectrum would swamp the real fragments with rows
whose label is meaningless.

If a library has no annotations at all, MARS refuses it and names the alternatives rather
than producing a model from it. `example-data/Stellar-HeLa-GPF.blib` is such a library: its
`RefSpectraPeakAnnotations` table is empty, and running the Python implementation against it
yields 7.9M pseudo-matches from a single file and a model that reduces the spread by 2.2%.

`--rt-window` sets the half-width of the RT window placed around each entry's library RT
(default 0.083 min, five seconds). A PRISM report's real elution windows are better when
available.

### No native SQLite

`.blib` is a SQLite database, but MARS reads it with a small managed reader written for this
purpose - B-tree walking, overflow page chains, record decoding - rather than
`Microsoft.Data.Sqlite`. That keeps this path free of per-platform native binaries, which is
a stated goal of the port.

The goal is not fully met today: `Parquet.Net`, used for DIA-NN libraries, brings a native
compression library with it. It is confined to `MARS.IO`, so `MARS.Core` remains pure
managed, but a build that reads DIA-NN parquet is not native-free. Splitting the DIA-NN
reader into its own assembly would restore the property for consumers that do not need it.

The reader is read-only and supports table B-trees, overflow chains, the record format and
UTF-8/UTF-16 text. It does not support indices, WAL or writing, none of which a library scan
needs.

## RF temperature logs (optional)

Two extra features become available when RF generator temperature traces are supplied:

```bash
mars calibrate ... --temperature-dir temperature_csvs/
```

Files are matched by name: `RFA2-{mzml base name}.csv` and `RFC2-{mzml base name}.csv`,
exported from Xcalibur as chromatogram CSVs. Lookups are nearest-neighbor in retention time.

They contributed little on the reference cohort (importance below 0.01 each), so this is
worth having when the logs exist and not worth chasing when they do not.

## Choosing a tolerance

Usually you do not have to. MARS reads the mass analyzer out of the mzML and picks:

| Detected | Tolerance | QC report drawn in |
|---|---|---|
| Ion trap, quadrupole | 0.3 Th | Th |
| Orbitrap, FT-ICR, TOF, Astral | 10 ppm | ppm |

It says which in the log, on the line above the first match:

```
INFO   high-resolution data; fragment tolerance 10 ppm (--tolerance or --tolerance-ppm to override)
```

Override it with `--tolerance`, `--tolerance-ppm`, or `--resolution unit|hram` - see
[the CLI reference](cli-reference.md#resolution-and-tolerance).

**Why it matters more than it looks.** An absolute tolerance on high-resolution data is far
too wide: 0.3 Th is about 430 ppm at m/z 700, so the window is two orders of magnitude wider
than the error and the most-intense-peak rule routinely selects a different ion. A ppm
tolerance on ion-trap data is far too narrow to catch the error MARS exists to measure.
Neither mistake stops the run. Matching the Astral file below at 0.3 Th returns 3,414,802
fragments rather than 1,408,902, and reports a standard deviation of 162 ppm against the
4.1 ppm that is really there - a complete report, all of it meaningless. That failure being
silent is why the analyzer is detected rather than documented.

`mars qc` reports the error in both Th and ppm whichever scale it draws in, which is the
quickest way to check the tolerance is sane before training anything.
