"""Dump the Python implementation's match table in the same CSV schema as ``mars
calibrate --dump-matches``, so the two can be differenced row by row.

The C# implementation agrees with the Python one on aggregate numbers - match counts and
the spread of the corrected error. That says nothing about whether any individual feature
is computed the same way, and the model leans hardest on features that aggregate
statistics cannot see. This script produces the other half of that comparison.

Usage:

    python dump_python_matches.py --mzml run.mzML --prism-csv report.csv --out py.csv

Then, having produced the C# side with

    mars calibrate --mzml run.mzML --prism-csv report.csv --no-dedupe-library \\
        --no-recalibrate --dump-matches cs.csv

compare them with compare_matches.py.

Pass --no-dedupe-library on the C# side: the Python implementation does not collapse
transitions that repeat across replicates, so leaving C# to dedupe would compare
different row sets.
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

import pandas as pd

from mars.library import load_prism_library
from mars.matching import match_library_to_spectra
from mars.mzml import read_dia_spectra

# Same order as MatchDumpWriter.KeyColumns, so a header diff is a real disagreement rather
# than a column-order artifact.
KEY_COLUMNS = [
    "scan_number",
    "retention_time",
    "peptide",
    "ion_annotation",
    "expected_mz",
    "observed_mz",
    "delta_mz",
    "observed_intensity",
]

# Features carried on the match rows themselves. The adjacent_ratio_* features are derived
# later, in MzCalibrator._prepare_features, and are added below.
MATCH_FEATURES = [
    "precursor_mz",
    "fragment_mz",
    "log_tic",
    "log_intensity",
    "absolute_time",
    "injection_time",
    "tic_injection_time",
    "fragment_ions",
    "ions_above_0_1",
    "ions_above_1_2",
    "ions_above_2_3",
    "ions_below_0_1",
    "ions_below_1_2",
    "ions_below_2_3",
]

RATIO_SOURCES = [
    ("adjacent_ratio_0_1", "ions_above_0_1"),
    ("adjacent_ratio_1_2", "ions_above_1_2"),
    ("adjacent_ratio_2_3", "ions_above_2_3"),
    ("adjacent_ratio_below_0_1", "ions_below_0_1"),
    ("adjacent_ratio_below_1_2", "ions_below_1_2"),
    ("adjacent_ratio_below_2_3", "ions_below_2_3"),
]


def add_derived_features(matches: pd.DataFrame) -> pd.DataFrame:
    """Reproduce the ratio features that MzCalibrator derives at fit time.

    They are computed there rather than during matching, so a dump of the raw match table
    would be missing exactly the two features the model weights most heavily. The
    definition here must track ``MzCalibrator._prepare_features``; the guard on
    ``fragment_ions > 0`` is what makes the ratio undefined, and the row is dropped
    downstream on the resulting NaN.
    """
    if "fragment_ions" not in matches.columns:
        return matches

    defined = matches["fragment_ions"] > 0
    for ratio, source in RATIO_SOURCES:
        if source in matches.columns:
            matches[ratio] = (matches[source] / matches["fragment_ions"]).where(defined)
    return matches


def rebase_absolute_time(matches: pd.DataFrame) -> pd.DataFrame:
    """Subtract the earliest acquisition, which is what the model is trained on.

    The C# implementation re-bases after reading every input file and dumps the re-based
    column, so the comparison has to be against the same quantity.
    """
    if "absolute_time" in matches.columns and matches["absolute_time"].notna().any():
        matches["absolute_time"] = matches["absolute_time"] - matches["absolute_time"].min()
    return matches


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mzml", required=True, type=Path, help="one mzML file")
    parser.add_argument("--prism-csv", required=True, type=Path, help="Skyline PRISM report")
    parser.add_argument("--out", required=True, type=Path, help="output CSV")
    parser.add_argument("--tolerance", type=float, default=0.3, help="m/z tolerance in Th")
    parser.add_argument("--min-intensity", type=float, default=500.0)
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)-7s %(message)s")
    log = logging.getLogger("dump")

    log.info("Loading PRISM library: %s", args.prism_csv)
    library = load_prism_library(args.prism_csv, mzml_filename=args.mzml.name)
    log.info("%d library entries", len(library))

    log.info("Matching: %s", args.mzml.name)
    matches = match_library_to_spectra(
        library,
        read_dia_spectra(args.mzml),
        mz_tolerance=args.tolerance,
        min_intensity=args.min_intensity,
        show_progress=False,
    )
    log.info("%d matches", len(matches))

    if matches.empty:
        log.error("No matches; nothing to compare.")
        return 1

    matches = rebase_absolute_time(matches)
    matches = add_derived_features(matches)

    matches = matches.rename(columns={"peptide_sequence": "peptide"})
    columns = [c for c in KEY_COLUMNS if c in matches.columns]
    columns += [c for c in MATCH_FEATURES if c in matches.columns]
    columns += [r for r, _ in RATIO_SOURCES if r in matches.columns]

    missing = [c for c in KEY_COLUMNS if c not in matches.columns]
    if missing:
        log.warning("Match table has no %s; those columns will be absent", ", ".join(missing))

    args.out.parent.mkdir(parents=True, exist_ok=True)
    # repr gives round-trip precision, which is the point: a value that rounds on the way
    # out cannot be differenced against the other implementation at m/z precision.
    matches[columns].to_csv(args.out, index=False, float_format="%r")
    log.info("Wrote %d rows, %d columns to %s", len(matches), len(columns), args.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
