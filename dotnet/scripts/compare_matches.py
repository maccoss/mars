"""Difference two match dumps row by row and column by column.

Takes the CSV written by ``mars calibrate --dump-matches`` and the one written by
dump_python_matches.py, joins them on the rows they both found, and reports where the two
implementations disagree.

    python compare_matches.py --csharp cs.csv --python py.csv

The join key is (scan_number, ion_annotation, expected_mz). Peptide sequence is
deliberately not part of it: the two implementations carry the modified sequence in
whatever form their library reader produced, and a formatting difference there is not a
calibration difference. Rows whose key appears more than once on either side are reported
and excluded, since there is no way to say which copy pairs with which.

Exit status is 1 if any column disagrees beyond its tolerance, so this can gate a build.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
import pandas as pd

BASE_KEY = ["scan_number", "ion_annotation", "expected_mz_key"]

# A handful of precursors appear in more than one block of a PRISM report, so the same
# fragment can be matched twice in one scan through two library entries. Both
# implementations produce those duplicates, so rather than discarding the rows, order
# each group identically on both sides and pair them off by position. If the two ever
# produced different multisets the surplus would show up as an unmatched row.
TIEBREAK = ["observed_mz", "delta_mz", "observed_intensity"]
KEY = BASE_KEY + ["occurrence"]

# Per-column absolute tolerance. Most of these are computed by the same arithmetic on both
# sides and should agree to the last bit; the tolerance exists to absorb the decimal
# round-trip through CSV, not to excuse a real difference.
EXACT = 0.0
DEFAULT_TOLERANCE = 1e-9

TOLERANCES = {
    # Intensities are float32 in the mzML and stay float32 through the C# reader, so a
    # value that passes through float64 arithmetic on one side only can differ in the last
    # float32 digit.
    "observed_intensity": 1e-3,
    "log_intensity": 1e-9,
    "log_tic": 1e-9,
    # Sums over thousands of float32 intensities, where accumulation order is visible.
    "fragment_ions": 1e-3,
    "ions_above_0_1": 1e-3,
    "ions_above_1_2": 1e-3,
    "ions_above_2_3": 1e-3,
    "ions_below_0_1": 1e-3,
    "ions_below_1_2": 1e-3,
    "ions_below_2_3": 1e-3,
    "tic_injection_time": 1e-3,
}


def load(path: Path, label: str) -> pd.DataFrame:
    # float_precision="round_trip" is not optional here. The default parser is faster and
    # drops the last digit, which invents differences of ~1e-16 in every column and buries
    # the real ones.
    frame = pd.read_csv(path, float_precision="round_trip")
    if "expected_mz" not in frame.columns:
        raise SystemExit(f"{label} dump has no expected_mz column: {path}")
    # A float is a poor join key. Round to a tenth of a milli-Thomson, far finer than any
    # real difference between two theoretical m/z values and far coarser than float noise.
    frame["expected_mz_key"] = frame["expected_mz"].round(4)
    return frame


def number_occurrences(frame: pd.DataFrame, label: str) -> pd.DataFrame:
    """Make a repeated key unique by position within its group."""
    duplicated = int(frame.duplicated(subset=BASE_KEY, keep=False).sum())
    if duplicated:
        print(f"  {label}: {duplicated:,} rows share a key; paired by position within the group")
    frame = frame.sort_values(BASE_KEY + TIEBREAK, kind="mergesort")
    frame["occurrence"] = frame.groupby(BASE_KEY, sort=False).cumcount()
    return frame


def describe(value: float) -> str:
    """Shortest round-trip form. repr() on a numpy scalar prints np.float64(...)."""
    return "NaN" if pd.isna(value) else repr(float(value))


def compare_column(merged: pd.DataFrame, column: str) -> dict:
    left = pd.to_numeric(merged[f"{column}_cs"], errors="coerce").to_numpy(dtype=float)
    right = pd.to_numeric(merged[f"{column}_py"], errors="coerce").to_numpy(dtype=float)

    left_nan, right_nan = np.isnan(left), np.isnan(right)
    # Both undefined counts as agreement: NaN is how an undefined ratio is represented and
    # the row is dropped on it downstream, so the two agree about the row's fate.
    nan_mismatch = int((left_nan != right_nan).sum())

    both = ~left_nan & ~right_nan
    if not both.any():
        return {"n": 0, "max_abs": 0.0, "over": 0, "nan_mismatch": nan_mismatch}

    difference = np.abs(left[both] - right[both])
    tolerance = TOLERANCES.get(column, DEFAULT_TOLERANCE)
    return {
        "n": int(both.sum()),
        "max_abs": float(difference.max()),
        "over": int((difference > tolerance).sum()),
        "nan_mismatch": nan_mismatch,
        "tolerance": tolerance,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csharp", required=True, type=Path)
    parser.add_argument("--python", required=True, type=Path)
    parser.add_argument(
        "--examples", type=int, default=3,
        help="worst-offending rows to print per failing column")
    args = parser.parse_args()

    csharp = load(args.csharp, "C#")
    python = load(args.python, "Python")

    print("Row counts")
    print(f"  C#     {len(csharp):,}")
    print(f"  Python {len(python):,}")
    print()

    print("Repeated keys")
    csharp = number_occurrences(csharp, "C#")
    python = number_occurrences(python, "Python")
    print()

    merged = csharp.merge(python, on=KEY, how="inner", suffixes=("_cs", "_py"))
    only_csharp = len(csharp) - len(merged)
    only_python = len(python) - len(merged)

    print("Row agreement")
    print(f"  matched by both  {len(merged):,}")
    print(f"  C# only          {only_csharp:,}")
    print(f"  Python only      {only_python:,}")
    print()

    if merged.empty:
        print("No rows in common; nothing to compare.")
        return 1

    shared = sorted(
        {c[:-3] for c in merged.columns if c.endswith("_cs")}
        & {c[:-3] for c in merged.columns if c.endswith("_py")}
    )
    # entry_index and fragment_index are internal to the C# library layout and have no
    # Python counterpart, so they never appear on both sides. peptide is text.
    shared = [c for c in shared if c not in ("peptide",)]

    print(f"{'column':<26} {'n':>9} {'max abs diff':>16} {'over tol':>9} {'NaN mismatch':>13}")
    print("-" * 78)

    failed = []
    for column in shared:
        result = compare_column(merged, column)
        flag = ""
        if result["over"] or result["nan_mismatch"]:
            flag = "  <-- differs"
            failed.append(column)
        print(
            f"{column:<26} {result['n']:>9,} {result['max_abs']:>16.3e} "
            f"{result['over']:>9,} {result['nan_mismatch']:>13,}{flag}")

    if only_csharp or only_python:
        failed.append("row set")

    print()
    if not failed:
        print("Every shared column agrees within tolerance, on every row both found.")
        return 0

    print(f"Disagreements: {', '.join(failed)}")
    for column in failed:
        if column == "row set" or args.examples <= 0:
            continue
        left = pd.to_numeric(merged[f"{column}_cs"], errors="coerce")
        right = pd.to_numeric(merged[f"{column}_py"], errors="coerce")

        # A row where only one side is NaN has no magnitude to rank by, and it is the more
        # interesting failure - the two disagree about whether the feature is defined at
        # all, which decides whether the row survives to training - so show those first.
        undefined_on_one_side = left.isna() != right.isna()
        rows = list(merged.index[undefined_on_one_side][: args.examples])
        magnitude = (left - right).abs()
        magnitude = magnitude[magnitude > 0]
        rows += [i for i in magnitude.nlargest(args.examples).index if i not in rows]

        print(f"\n  {column}:")
        for index in rows:
            print(
                f"    scan {merged.at[index, 'scan_number']} "
                f"{merged.at[index, 'ion_annotation']} "
                f"expected {merged.at[index, 'expected_mz_key']}: "
                f"C# {describe(left[index])} vs Python {describe(right[index])}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
