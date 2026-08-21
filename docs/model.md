# The model

MARS predicts one number per peak: the mass error, in Thomsons. The corrected value is
`observed - PredictDelta(features)`. Everything else in the tool exists to produce good
training rows for this, or to write the answer back out without breaking the file.

This page covers the model itself. For how the training rows are produced, see
[the algorithm](algorithm.md).

## Why gradient boosted trees

The error is not a smooth function of anything. It has a broad dependence on m/z, a drift
over acquisition time, and a strong dependence on how crowded the ion trap was when the
spectrum was taken - which shows up as sharp, interacting effects between injection time,
total ion current and the population of ions in neighbouring m/z windows.

A polynomial in m/z, which is the traditional approach, captures the first of these and
none of the rest. Boosted trees capture interactions without being told which ones to
look for, tolerate features on wildly different scales without normalization, and do not
extrapolate wildly outside the training range - a real virtue when the correction is being
applied to millions of peaks that may sit anywhere.

The cost is that the model is opaque, which is why the QC report leads with permutation
importance and per-feature trends rather than the fitted parameters.

## Where the implementation comes from

The boosting code is **`Osprey.ML.GradientBoostedTrees`** from
[ProteoWizard](https://github.com/ProteoWizard/pwiz), vendored into
`dotnet/third_party/Osprey.ML/` rather than reimplemented.

The regression objective MARS needs was contributed upstream rather than forked, so one
boosting implementation is maintained across the lab's C# tools instead of one per tool.
Osprey uses the binary-logistic path for FDR; MARS uses the squared-error path. The split
finding, histogram construction, regularization and determinism machinery are shared.

The vendored copy is byte-for-byte identical to upstream, and `MARS.Test` hashes it against
`UPSTREAM.json` on every build. Editing the local copy fails the build. Fix bugs upstream
and re-sync with `scripts/sync-osprey-ml.ps1`.

> The bit-identity of the shared path was verified when the regression objective was added:
> the same five logistic fixtures produce the same 1,925 scores, hashing to
> `242558FF0A6FCD3A5C34BE0A57BD42848A706853BD51E998E21C7B8856852A22`, before and after the
> change and at 1, 4 and 16 threads. Adding regression did not perturb Osprey.

## The objective

The regularized objective from [Chen & Guestrin
2016](https://arxiv.org/abs/1603.02754), the same one XGBoost optimizes.

Each round fits a tree to the gradient and hessian of the loss at the current prediction.
Under squared error with sample weight `w`:

```
gradient  g = w * (prediction - y)
hessian   h = w
```

The hessian being exactly the weight has a consequence worth stating plainly: **a summed
hessian is a summed weight, and with unit weights it is a sample count.** That is why
`min_child_weight` reads as "minimum samples per leaf" here, and why the same parameter
means something quite different on the logistic path, where the hessian shrinks as the
model sharpens.

A candidate split is scored by the gain it produces:

```
gain = 0.5 * ( GL^2/(HL+lambda) + GR^2/(HR+lambda) - (GL+GR)^2/(HL+HR+lambda) ) - gamma
```

and taken only if that exceeds `gamma`. Leaf values are the same expression's minimizer,
with the L1 penalty `alpha` applied by soft-thresholding the summed gradient.

The base score is the **weighted mean of the labels** - the average mass error over the
training rows - so the first tree starts from the cohort's overall bias rather than zero.

## Splits are found on histograms

Fitting a tree by sorting every feature at every node would be hopeless at nine million
rows. Instead each feature is quantile-binned once, up to `max_bins` (256) bins, and every
value is replaced by a byte bin index. Split finding then walks a histogram of summed
gradients and hessians per bin, which is a fixed cost per node regardless of how many rows
land in it.

The practical consequence: **a threshold is a bin edge, not an arbitrary value.** With 256
quantile bins the resolution is fine enough that this is invisible in the output, but it
does mean two features with identical quantiles produce identical candidate splits, and
tie-breaking then decides between them.

## Hyperparameters

The defaults are XGBoost's defaults, deliberately. The Python implementation used
`XGBRegressor` with only four parameters set, leaving the rest at library defaults, so
matching them is what makes the two implementations comparable at all.

| Option | Default | What it does |
|---|---|---|
| `--n-estimators` | 100 | Boosting rounds |
| `--max-depth` | 6 | Maximum tree depth |
| `--learning-rate` | 0.1 | Shrinkage applied to each tree's contribution |
| `--seed` | 42 | Seeds subsampling and tie-breaking |
| `--validation-split` | 0.2 | Held-out fraction; 0 trains on everything |
| - | `min_child_weight` 1 | Minimum summed hessian per leaf |
| - | `subsample` 1.0 | Row sampling per round |
| - | `colsample_bytree` 1.0 | Feature sampling per tree |
| - | `gamma` 0 | Minimum gain to take a split |
| - | `reg_lambda` 1.0 | L2 penalty on leaf weights |
| - | `reg_alpha` 0 | L1 penalty on leaf weights |
| - | `max_bins` 256 | Quantile bins per feature |

The ones without a flag are not exposed on the command line. They are recorded in the model
file, so a model always says what produced it.

There is rarely a reason to change any of this. The error surface is smooth enough that 100
shallow trees fit it comfortably, and the failure mode that actually bites is not
underfitting - it is training on rows whose "theoretical" m/z was never theoretical. See
[spectral libraries](spectral-libraries.md).

## Rows are weighted by intensity

Each training row is weighted by the matched peak's intensity, normalized so the weights
average 1.

The reasoning is that a strong peak has a better-determined centroid. A peak just above the
`--min-intensity` floor may be a few ions, and its apparent m/z carries counting noise that
has nothing to do with the instrument's calibration. Weighting by intensity lets the model
listen to the peaks that actually know where they are.

**The normalization is not cosmetic.** `reg_lambda` and `min_child_weight` are thresholds on
summed hessians, which under squared error are summed weights. Feeding raw detector counts,
which run to 10^5 and beyond, would make `reg_lambda = 1` a rounding error and
`min_child_weight = 1` meaningless. Both implementations normalize to mean 1 for this
reason.

## Which features the model gets

Not a fixed list. The feature set is chosen from what the data actually supports:

- If no MS2 spectrum carries an ion injection time, the whole injection-time group is
  dropped rather than filled with zeros.
- Temperature features appear only when the matching CSVs were supplied.
- A row with any undefined feature value is dropped, not imputed.

The model file records the ordered feature names it was trained on, and loading a model
whose features do not match what the extractor produces is a hard error. A silently
misaligned feature vector would produce plausible numbers and wrong corrections, which is
the worst possible failure mode for this tool.

The 22 features and what each one is for are documented in
[the algorithm](algorithm.md#step-2-feature-extraction).

## Determinism

Identical input produces a bit-identical model and bit-identical output at any thread
count. This is a hard requirement, not a nice property: MARS writes m/z values into files
that get reprocessed, compared, and searched, and a tool whose output depends on the thread
count makes every downstream comparison unreliable.

It is achieved by construction:

- Parallelism runs **across features only**, never across rows. Each thread owns whole
  feature columns, so no float accumulation is split across threads.
- Every float accumulation - histograms, leaf gradients and hessians - runs in a fixed
  order determined by the data, not by scheduling.
- Subsampling and tie-breaking use a seeded XorShift64 PRNG, drawn in a fixed sequence.
- Train/validation splitting shuffles a seeded permutation and then **re-sorts each side
  into ascending row order**, so downstream accumulation order is a function of the data
  rather than of the shuffle.

CI enforces this as its own job, separate from the rest of the test suite, so a failure is
unmistakable.

The one thing that is *not* bit-identical is the compressed bytes of the output file:
different platforms ship different zlib builds. Decoded values are identical. Use
`mars compare`, not `cmp`. See [mzML passthrough](mzml-passthrough.md#binary-arrays).

## Permutation importance

The QC report ranks features by permutation importance: shuffle one feature's values across
the validation rows, re-score, and measure how much the error degrades. Normalized to sum
to 1.

This is reported rather than split counts because split counts mislead. A feature with many
distinct values collects splits simply by offering more places to cut, whether or not those
cuts help. Permutation importance measures what the model would lose without the feature,
which is the question actually being asked.

A feature near zero is carrying no weight and could be dropped. On the reference Stellar
cohort the two RF temperature features score below 0.01 each, which is the evidence behind
the advice that they are worth having when the logs exist and not worth chasing when they
do not.

## The model file

Versioned JSON, written to `mars_model.json`. Not interchangeable with the Python
implementation's pickled XGBoost booster; retrain rather than convert.

```jsonc
{
  "formatVersion": 1,
  "marsVersion": "26.1.0",
  "featureNames": ["precursor_mz", "fragment_mz", ...],  // ordered; must match at load
  "absoluteTimeOffset": 1733158420.0,                    // seconds, see below
  "options":  { "nEstimators": 100, "maxDepth": 6, ... },
  "model":    { "baseScore": ..., "objective": "SquaredError", "featureCount": 20,
                "feature": [...], "threshold": [...], "left": [...], "right": [...],
                "leaf": [...], "treeRoot": [...] },
  "training": { "rowsMatched": ..., "rowsTrain": ..., "trainMae": ..., ... }
}
```

The trees are stored as flat parallel arrays rather than nested objects: one entry per
node, with `treeRoot` indexing where each tree starts. A hundred trees of depth six is
several thousand nodes, and a nested representation triples the file size for no benefit.

`absoluteTimeOffset` is the part most worth understanding. Acquisition time is re-based to
the earliest matched spectrum before training, so the feature starts near zero. The offset
therefore has to travel with the model and be subtracted again at correction time. The
Python implementation re-bases for training but feeds raw Unix timestamps back in when
writing, so every inference row lands far above the largest value the model ever saw and
the feature collapses to a single branch. That is one of the four defects the port
deliberately does not reproduce; see
[port spec section 10a](dotnet-port-spec.md#10a-defects-found-in-the-python-implementation).

## How close is this to XGBoost?

Close, and slightly better on the reference data. Both were trained on identical rows - the
features are verified bit-identical, see [parity](python-parity.md) - with identical
hyperparameters and identical weighting, on 146,515 fragments from one Stellar run:

| | value |
|---|---|
| Pearson r between the two predictions | 0.9955 |
| Median absolute difference | 0.0034 Th |
| RMS difference | 0.0079 Th, which is 6.6% of the uncorrected spread |
| Max absolute difference | 0.127 Th |

| Residual after correction | std | MAD |
|---|---|---|
| uncorrected | 0.1183 | 0.0802 |
| C# (`Osprey.ML`) | **0.0839** | **0.0431** |
| Python (XGBoost) | 0.0843 | 0.0433 |

Two independent boosting implementations will never agree tree for tree, and per-peak
corrected m/z values do differ. What matters is that they learn the same function and leave
the same amount of error behind, and they do.

Reproduce it with:

```bash
mars calibrate --mzml run.mzML --prism-csv report.csv --no-dedupe-library \
    --validation-split 0 --no-recalibrate --dump-predictions cs.csv --output-dir out/
python dotnet/scripts/compare_models.py --csharp cs.csv
```

> The comparison above is in-sample: both models were trained on every row and scored on
> the same rows. That is the right way to ask whether two implementations learn the same
> function, but it does not distinguish a better fit from more overfitting. A held-out
> comparison needs both to train on an identical subset, which the dump does not currently
> mark.
