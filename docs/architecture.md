# How MARS is put together

A map of the code, for anyone modifying it. For what the tool does, start with
[the algorithm](algorithm.md).

## Projects

```
dotnet/
  MARS/            the CLI: argument parsing, the five commands, the QC report
  MARS.Core/       matching, features, the calibrator, correction. No I/O
  MARS.IO/         mzML reading and writing, library readers, the SQLite reader
  MARS.Pwiz/       vendor formats in, non-mzML formats out, through pwiz-sharp. Optional
  MARS.OspreyML/   compiles the vendored Osprey.ML sources
  MARS.Test/       179 tests
  third_party/
    Osprey.ML/     verbatim copies of the boosting code, hash-guarded
```

The split that matters is `MARS.Core` having no I/O. Matching, feature extraction and
correction take arrays and return arrays, which is what makes them testable without
fabricating files and what keeps the parallel correction path free of anything that could
touch a stream.

`MARS.Core` is also the only assembly with no package references at all. `MARS.IO` has one,
`Parquet.Net`, and that is the only native code in the tree. See
[dependencies](#dependencies).

## What happens during `mars calibrate`

```
                library file                      mzML files
                     |                                 |
      PrismCsv / Blib / DiannParquet          MzMLFile.Inspect
        LibraryReader                          (index, byte offsets)
                     |                                 |
              SpectralLibrary  <---- FragmentMatcher ----> SpectrumRecord stream
              (column arrays)              |
                                           v
                                      MatchTable            one row per matched fragment,
                                   (column-major)           22 feature columns
                                           |
                                           v
                                     MzCalibrator.Fit
                                           |
                            +--------------+--------------+
                            |                             |
                       MzCalibrator                   QcHtmlReport
                            |                          QcReport
                            v
                     SpectrumCorrector  ---->  MzMLWriter (byte splice)
                                                        |
                                                        v
                                              {input}-mars.mzML
```

Two passes over each file: one to match, one to correct. The alternative - hold every
spectrum in memory and correct in place - is what makes a naive implementation fail on a
5 GB Astral run.

## Data structures worth knowing

**`SpectralLibrary`** is column-major, not a list of objects. `FragmentStart[entry]` indexes
into flat `FragmentMz` / `FragmentIonType` / `FragmentCharge` arrays. A plate-scale Skyline
report is 67 million rows; a managed object per fragment would not fit.

**`MatchTable`** is likewise column-major, one `GrowableArray<double>` per feature. It is
also exactly the layout the model wants, so training does not copy. Detail columns (scan
number, fragment index, observed m/z, retention time) are allocated only when something
needs them - a dump or the HTML report - because they cost 16 bytes a row across millions
of rows.

**`SpectrumRecord`** carries the decoded m/z and intensity arrays plus the metadata the
features need. Buffers are pooled and reused between spectra.

## The pwiz path

`MARS.Pwiz` is where every format that is not mzML enters or leaves. It is **optional**:
pwiz-sharp has no package feed, so the reference points at an external checkout and the project
compiles either way. Without one, `MARS_NO_PWIZ` drops the backend, `PwizOutput` reports itself
unavailable, and MARS reads and writes mzML exactly as it always has. That is the same shape
pwiz's own vendor projects use for their SDKs.

| Type | Does |
|---|---|
| `SpectrumSources` | Picks a reader from the path: mzML to `MARS.IO`, everything else to pwiz |
| `PwizSpectrumSource` | Vendor formats in, as `SpectrumRecord` - the same type the mzML reader yields |
| `MarsSpectrumList` | A pwiz `SpectrumList` that applies the correction as spectra are pulled through |
| `PwizWriteBackend` | mzXML, mzMLb and mgf out, and mzML when the input was a vendor file |
| `VendorReaders` | Registers the vendor readers with pwiz's dispatcher, from a module initializer |
| `MzMLEncoding` | Reads the input's binary encoding so the output matches it |

Three things about it are load-bearing:

**`ISpectrumSource` is the seam**, and it lives in `MARS.Core`. The matcher, the features and
the model consume `SpectrumRecord` and neither know nor care which reader produced it, so
`MARS.Core` depends on neither `MARS.IO` nor `MARS.Pwiz`.

**The correction is applied identically on both paths.** `MarsSpectrumList` calls the same
`SpectrumCorrector` the byte-splice writer does. Writing one file both ways and diffing with
`mars compare` found no difference across 82,349,582 peaks.

**Ion mobility is collapsed, not modelled.** pwiz is asked to combine each TIMS frame's
mobility scans into one spectrum per isolation window, on read and on write. Uncombined, a
diaPASEF frame is hundreds of two-peak spectra sharing one retention time, and fourteen of
MARS's features are computed from the peaks surrounding a match.

## The mzML path

The one design decision that shapes everything else: **MARS does not parse mzML into a
document and write it back out.** It scans for spectrum elements, decodes only the binary
arrays it needs, and splices corrected arrays into a byte-for-byte copy of the input.

- `MzMLSpanScanner` walks the file finding element boundaries without building a tree.
- `MzMLSpectrumParser` pulls out the cvParams and binary arrays for one spectrum.
- `MzMLBinaryCodec` does base64 and zlib.
- `MzMLWriter` copies input bytes through, substituting re-encoded m/z arrays and fixing up
  the index offsets and checksum.

Consequences, good and bad, are in [mzML passthrough](mzml-passthrough.md). The short
version: an entire class of "MARS broke my file" problems cannot happen, because the bytes
MARS did not mean to change are the input's bytes.

## Library readers

Three formats, one output type:

| Reader | Source | Notes |
|---|---|---|
| `PrismCsvLibraryReader` | Skyline transition report | Streams; the reference report is 16.1 GB |
| `BlibLibraryReader` | BiblioSpec `.blib` | Via the managed SQLite reader |
| `DiannParquetLibraryReader` | DIA-NN `report-lib.parquet` | Needs `report.parquet` for RT |

They differ in more than format - they differ in whether their m/z values are theoretical at
all, which is the single most important thing about a MARS library. See
[spectral libraries](spectral-libraries.md).

## The managed SQLite reader

`.blib` is a SQLite database, and MARS reads it with a small reader written for this purpose
rather than `Microsoft.Data.Sqlite`.

The reason is dependency shape. `Microsoft.Data.Sqlite` brings `SQLitePCLRaw` and a native
`e_sqlite3` for every runtime identifier - exactly the per-platform native payload the port
exists to avoid. A BiblioSpec library only ever needs sequential scans of a handful of
tables, which is a small and well-specified subset of the format.

What it supports:

- Table b-trees, interior and leaf pages
- Overflow page chains, for rows too large for one page
- The record format: varints, serial types, and the type-code encoding of integers, floats,
  text and blobs
- UTF-8 and UTF-16 text

What it does not: indices, WAL, encryption, writing. None of which a library scan needs.

**The one subtlety that caused a real bug.** SQLite's `INTEGER PRIMARY KEY` *aliases the
rowid*: the column is stored as NULL in the record, and the value lives in the b-tree's
rowid. A reader that trusts the record gets NULL for every id. This surfaced as reading 1
precursor out of 8,587 - the join silently matched nothing - which is the characteristic
failure of a hand-written binary format reader: not a crash, just quietly wrong data. The
reader now falls back to `row.RowId` when such a column is null.

That episode is also why this reader is on the list of things most deserving of more tests;
see [the coverage note](#testing).

## Determinism

Identical input, bit-identical output, at any thread count. Enforced by a dedicated CI job.

- Model training parallelizes across features only, never across rows, so no float
  accumulation is split across threads.
- The correction pass parallelizes across spectra, and each spectrum's output bytes are
  independent of the others.
- Seeded XorShift64 for subsampling and tie-breaking.

The exception is compressed bytes: different platforms ship different zlib builds, so output
files are not byte-identical across platforms even though every decoded value is. Use
`mars compare`, not `cmp`.

## Dependencies

`MARS.Core` has none. `MARS.Pwiz` has whatever a pwiz-sharp checkout brings, and nothing when
there is not one. `MARS.IO` has `Parquet.Net`, which brings `IronCompress` and a native
compression library - the only native code in the tree.

It is confined to the DIA-NN path, and it does not fail closed: without the native library,
Snappy (what DIA-NN writes), Gzip, Brotli, Zstd and uncompressed all still work through
managed fallbacks, and only LZ4 and LZO fail, with a message naming the codec. That matters
on the two shipped platforms `IronCompress` publishes no native for: **Windows on Arm and
Intel macOS**.

Splitting `DiannParquetLibraryReader` into its own assembly would restore the pure-managed
property for consumers that do not read DIA-NN libraries. Not done yet.

## Testing

179 tests. The parts with the strongest evidence are not the ones with the most tests:

- **Fragment matching and every model feature** are verified against the Python
  implementation row by row - 160,947 fragments, 24 columns, maximum absolute difference
  zero. See [parity](python-parity.md). This is stronger evidence than unit tests with
  invented expected values.
- **mzML passthrough** is covered by round-trip tests and by `mars verify` on real files.
- **The vendored boosting code** is hash-guarded against upstream and bit-identity-checked.

The thinnest areas, in rough order of risk: the managed SQLite reader and the `.blib` path
(exercised once, manually, and it was wrong the first time); `PrismCsvLibraryReader`'s
replicate filtering and de-duplication; and `CommandLineArgs`, where a parsing bug means
silently running with the wrong tolerance.

## Where things live

| I want to change... | Look at |
|---|---|
| which peak gets matched | `MARS.Core/FragmentMatcher.cs`, `PeakSearch.cs` |
| a feature's definition | `MARS.Core/MarsFeature.cs`, `FragmentMatcher.cs` |
| training or hyperparameters | `MARS.Core/MzCalibrator.cs` |
| the boosting itself | upstream in pwiz, then re-sync. Not the vendored copy |
| how corrected files are written | `MARS.IO/MzMLWriter.cs` |
| a library format | `MARS.IO/*LibraryReader.cs` |
| the QC figures | `MARS/Report/` |
| a command-line option | `MARS/*Command.cs` and its `PrintHelp` |
