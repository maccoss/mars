# MARS v26.2.0 Release Notes

MARS reads the Skyline PRISM report as parquet as well as CSV, and the Python implementation
it was ported from has been removed - its verification frozen first, so the guarantee outlives
the code.

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

## Breaking Changes

- **The Python implementation has been removed.** MARS is the C# tool. The Python package,
  its tests, `pyproject.toml` and the pytest/ruff workflow are gone from `main` so that nobody
  reaches for them by accident and finds a tool that is no longer maintained.

  It is not lost. `v26.1.0` and every earlier tag carry the code, its tests and the parity
  harness that drove it:

  ```bash
  git checkout v26.1.0
  ```

  `README-python.md` stays, with a banner saying where the code went, so results produced by the
  Python tool remain documented.

  **The parity guarantee survives the removal.** Every model feature was verified against the
  Python implementation row by row, and that verification was re-taken over the full five-file
  reference cohort immediately before the code was deleted: 352,349 matched fragments, every
  shared column agreeing to a maximum absolute difference of 0.000e+00. It is frozen in
  `parity/` as a digest per file - a SHA-256 per column plus its finite count, minimum, maximum
  and mean - and `dotnet/scripts/parity_digest.py check` is what a change to the matcher is
  measured against now. The full dumps are attached to the v26.1.0 release for diagnosing a
  digest that trips.

  What is lost is the ability to extend it. New reference data would need the Python
  implementation, which now means checking out a tag. The frozen set covers five Stellar
  GPF-DIA files through the PRISM CSV path, and nothing else - `parity/README.md` says so
  plainly rather than leaving it to be discovered.

  `compare_matches.py`, `compare_models.py` and `parity_digest.py` stay: none of them import the
  package. `dump_python_matches.py` went with it, since generating the Python side is exactly
  what is no longer possible.
