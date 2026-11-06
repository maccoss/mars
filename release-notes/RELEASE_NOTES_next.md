# MARS vNEXT Release Notes

One-sentence summary of the release, written when there is something to summarise.

## New Features

- **Osprey results can be used as the library.** `--library osprey.blib` now reads the
  `.blib` Osprey writes as its search result. Osprey writes no peak annotations, and MARS used
  to refuse any `.blib` without them, because an unannotated peak usually has only an OBSERVED
  m/z, which carries the reference run's own miscalibration. Osprey's peaks are different:
  they are fragment m/z copied from the search library, so for a predicted library they are
  theoretical. MARS now checks this rather than assuming either way. It names each peak as
  the b or y ion it sits on, within 2 ppm, and reads the library only if at least 80% of its
  peaks are named. On a 21,340-precursor Stellar result all 333,198 peaks were named, the
  worst 0.11 ppm away. A library of observed spectra is still refused: on the unit-resolution
  example library 0.02% of peaks are named.
- **`.blib` entries are matched within their real elution windows.** When a library records
  elution boundaries, MARS uses them in place of the fixed `--rt-window` around the library
  RT. It takes each run's boundaries from the `RetentionTimes` table first, for the source
  files that match the mzML being processed, then the entry's own `startTime`/`endTime`.
  `--rt-window` now applies only to entries with neither. Osprey writes per-run boundaries for
  every precursor, and its peaks average 0.24 min wide against the 0.166 min fixed window.
  **This changes written output** for any `.blib` that carries boundaries, since a different
  window admits different spectra. A library built from DDA searches leaves them empty and is
  unaffected. The log reports how many entries took their window from each source.

## Bug Fixes

## Performance

## Breaking Changes
