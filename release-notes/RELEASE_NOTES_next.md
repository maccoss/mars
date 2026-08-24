# MARS vNEXT Release Notes

One-sentence summary of the release, written when there is something to summarise.

## New Features

- **The Skyline PRISM report can be parquet.** Skyline picks parquet from a `.parquet` output
  extension, and [PRISM](https://github.com/maccoss/skyline-prism) asks for it because it is far
  smaller - the five-file Stellar report here is 19.6 MB as CSV and 1.5 MB as parquet. MARS now
  reads either.

  Which reader runs is decided by what the file contains, not by its extension. A DIA-NN library
  and a Skyline PRISM report both arrive as `.parquet`, and until now every `.parquet` was
  assumed to be DIA-NN, so a PRISM report would have been read against the wrong schema and
  reported a missing DIA-NN column - which says nothing about what the user actually did.

  The option is now `--prism-report`, since the old name says CSV and the file need not be one.
  **`--prism-csv` still works** and means the same thing; every existing script, and every
  example in these docs, keeps running.

  The same report read either way produces the same library. On the reference cohort both paths
  give byte-identical QC output - 1,178 precursors, 5,890 fragments, 41,283 matches - and the
  equivalence is asserted in the test suite rather than left to a one-off check. A real
  32.8-million-row plate report streams in 31 seconds, a row group at a time, so memory does not
  scale with the report.

## Bug Fixes

## Performance

## Breaking Changes
