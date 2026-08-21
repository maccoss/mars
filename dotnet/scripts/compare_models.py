"""Compare the C# gradient boosted trees against Python's XGBoost on identical data.

The two implementations will never agree tree for tree - they are different codebases with
different split-finding and different tie-breaking. The question that matters is whether
they learn the same function, and whether either corrects the data better.

This trains XGBoost on exactly the rows the C# model was trained on, taken from the C#
prediction dump, and compares the two predictions row by row.

    mars calibrate --mzml run.mzML --prism-csv report.csv --no-dedupe-library \\
        --validation-split 0 --no-recalibrate --dump-predictions cs.csv --output-dir out/

    python compare_models.py --csharp cs.csv

Use --validation-split 0. Otherwise the C# model is fitted on a subset chosen by its own
splitter and scored on rows it never saw, while XGBoost here would see everything, and the
difference between the two would be mostly the split rather than the implementation.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
import pandas as pd
import xgboost as xgb

# Columns the dump carries that are not model features.
NON_FEATURES = {
    "scan_number", "retention_time", "entry_index", "fragment_index", "peptide",
    "ion_annotation", "expected_mz", "observed_mz", "delta_mz", "observed_intensity",
    "predicted_delta_mz", "residual",
}

# MzCalibrator's defaults, which are XGBoost's defaults apart from these four.
N_ESTIMATORS = 100
MAX_DEPTH = 6
LEARNING_RATE = 0.1
SEED = 42


def robust(values: np.ndarray) -> tuple[float, float]:
    """Standard deviation and median absolute deviation, in Th."""
    median = float(np.median(values))
    return float(np.std(values)), float(np.median(np.abs(values - median)))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csharp", required=True, type=Path, help="--dump-predictions output")
    parser.add_argument("--n-estimators", type=int, default=N_ESTIMATORS)
    parser.add_argument("--max-depth", type=int, default=MAX_DEPTH)
    parser.add_argument("--learning-rate", type=float, default=LEARNING_RATE)
    parser.add_argument("--seed", type=int, default=SEED)
    args = parser.parse_args()

    frame = pd.read_csv(args.csharp, float_precision="round_trip")
    if "predicted_delta_mz" not in frame.columns:
        raise SystemExit(
            f"{args.csharp} has no predicted_delta_mz column. It must come from "
            "--dump-predictions, not --dump-matches.")

    features = [c for c in frame.columns if c not in NON_FEATURES]

    # The C# model scores NaN for a row with any undefined feature, which is exactly the set
    # of rows it could not train on. Use the same set so both see identical data.
    usable = frame["predicted_delta_mz"].notna() & frame[features].notna().all(axis=1)
    used = frame[usable]

    print(f"Rows in dump          {len(frame):,}")
    print(f"Rows both can use     {len(used):,}")
    print(f"Features             {len(features)}  ({', '.join(features)})")
    print()

    x = used[features].to_numpy(dtype=float)
    y = used["delta_mz"].to_numpy(dtype=float)

    # Both implementations weight by observed intensity normalized to mean 1. The
    # normalization is not cosmetic: reg_lambda and min_child_weight are thresholds on
    # summed hessians, which under squared error are summed weights.
    weight = used["observed_intensity"].to_numpy(dtype=float)
    weight = weight / weight.mean()

    model = xgb.XGBRegressor(
        n_estimators=args.n_estimators,
        max_depth=args.max_depth,
        learning_rate=args.learning_rate,
        random_state=args.seed,
        n_jobs=-1,
        objective="reg:squarederror",
    )
    model.fit(x, y, sample_weight=weight, verbose=False)

    python_prediction = model.predict(x).astype(float)
    csharp_prediction = used["predicted_delta_mz"].to_numpy(dtype=float)

    difference = csharp_prediction - python_prediction
    correlation = float(np.corrcoef(csharp_prediction, python_prediction)[0, 1])

    print("Predictions on the same rows")
    print(f"  Pearson r                    {correlation:.6f}")
    print(f"  mean difference              {difference.mean():+.6f} Th")
    print(f"  RMS difference               {np.sqrt((difference ** 2).mean()):.6f} Th")
    print(f"  median absolute difference   {np.median(np.abs(difference)):.6f} Th")
    print(f"  95th percentile |difference| {np.percentile(np.abs(difference), 95):.6f} Th")
    print(f"  max |difference|             {np.abs(difference).max():.6f} Th")
    print()

    # The number that decides whether a difference matters: what is left after correcting.
    before_std, before_mad = robust(y)
    cs_std, cs_mad = robust(y - csharp_prediction)
    py_std, py_mad = robust(y - python_prediction)

    print("Residual after correction (the number that matters)")
    print(f"  {'':<12} {'std (Th)':>10} {'MAD (Th)':>10}")
    print(f"  {'uncorrected':<12} {before_std:>10.4f} {before_mad:>10.4f}")
    print(f"  {'C#':<12} {cs_std:>10.4f} {cs_mad:>10.4f}")
    print(f"  {'Python':<12} {py_std:>10.4f} {py_mad:>10.4f}")
    print()
    print(f"  spread reduction   C# {100 * (1 - cs_std / before_std):5.1f}%    "
          f"Python {100 * (1 - py_std / before_std):5.1f}%")
    print(f"  MAD reduction      C# {100 * (1 - cs_mad / before_mad):5.1f}%    "
          f"Python {100 * (1 - py_mad / before_mad):5.1f}%")
    print()

    # A prediction difference only matters relative to the error being corrected. Stating it
    # as a fraction of the uncorrected spread is what says whether it is worth caring about.
    relative = np.sqrt((difference ** 2).mean()) / before_std
    print(f"RMS prediction difference is {100 * relative:.1f}% of the uncorrected spread.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
