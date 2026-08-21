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
unmistakable, and a unit test saves two independently fitted models and compares the files
byte for byte.

Verified end to end on real data: the same `mars calibrate` invocation run twice on a
1.2 GB Stellar file, plus a third run at `--threads 1`, produce a byte-identical
`mars_model.json` (4,194,948 bytes), a byte-identical `mars_qc_summary.txt`, and a
byte-identical 1,510,067,312-byte corrected mzML. The per-fold cross-validation figures
match to every printed digit across thread counts.

The one thing that is *not* bit-identical is the compressed bytes of the output file:
different platforms ship different zlib builds. Decoded values are identical. Use
`mars compare`, not `cmp`. See [mzML passthrough](mzml-passthrough.md#binary-arrays).

## Cross-validation

By default MARS trains **five models, one per fold, with folds split by peptide**, and
reports what each scored on the peptides it did not see.

### Why the split has to be by peptide

This is the part that matters most, and it is easy to get wrong.

A peptide's fragments recur across hundreds of spectra, always with the same theoretical
m/z - and `fragment_mz` is a model feature. Split rows at random and the same peptide lands
on both sides of the boundary, so the model can memorize "this exact m/z has that error"
instead of learning anything about the instrument. The held-out number then measures recall
rather than generalization, and it comes out flattering.

Splitting by peptide closes that route. Every reported number comes from a model that never
saw the peptide it is scoring, which is what makes it an estimate of performance on data
MARS was not trained on.

Folds are assigned by sorting the distinct peptides and dealing them round-robin. No random
seed is involved, so the split is reproducible from the input alone, and every fold gets an
equal number of peptides. This follows Osprey's Percolator implementation, which splits its
folds the same way (`PercolatorSampling.CreateStratifiedFoldsByPeptide`).

### What gets applied: the folds merged into one model

Cross-validation produces five models, and something has to be written to
`mars_model.json`. MARS merges them into **one** model that predicts exactly what averaging
them would.

That is possible because a boosted ensemble's score is linear in its trees:

```
score(x) = baseScore + sum over trees of leaf(traverse(tree, x))
```

so the average over K models rearranges into a single model:

```
(1/K) * sum_k [ base_k + sum_i tree_ki(x) ]
    = mean(base_k) + sum over ALL trees of ( tree(x) / K )
```

Keep every tree from every fold, divide each leaf value by K, average the base scores. Not
an approximation - the predictions are identical to the last bit, and a test checks that
over 300 random feature vectors on fold models trained to deliberately disagree.

**This is not a refit.** No model is trained on data that was held out from it and then
quietly promoted. The object that ships is the ensemble that was measured, written as one
model.

#### Is averaging trees sound?

It is the same operation random forests are built on, and the reason it is safe is worth
being precise about: **MARS averages functions, not parameters.**

Averaging *parameters* of non-linear models can produce nonsense - the midpoint of two
neural networks' weights is generally not a working network, and the midpoint of two trees'
split thresholds is not a meaningful tree. Averaging *outputs* cannot. For squared error the
ambiguity decomposition gives

```
error(ensemble) = mean(error of members) - disagreement among members
```

with the second term never negative. The average is never worse than the average member,
and the more the members disagree the bigger the gain. Three very different tree solutions
therefore cannot average into something that fails; disagreement is what makes averaging
worth doing.

The merge implements output averaging exactly, by scaling leaf contributions. It never
merges tree structures or averages thresholds, which is the operation that would break.

#### What it costs

Merging buys one model object, one scoring path and a simpler file. It does **not** buy
back time:

| | |
|---|---|
| single fit, 100 trees | 52 s on a 1.47 GB Stellar file |
| 5 folds merged, 500 trees | 266 s on the same file |

Scoring cost is the number of trees traversed, and the trees add up. This is the one place
the tree case is genuinely worse than Percolator's linear one: averaging K weight vectors
gives another vector of the same size, so applying it is free, whereas averaging K tree
ensembles gives K times the trees. `--cv-folds 0` trains a single model if that trade is
not worth it, at the cost of an in-sample accuracy figure rather than an honest one.

### What it reports

Per fold and pooled: median absolute residual, RMS, standard deviation, the reduction in
median absolute error, and Pearson r between predicted and observed error. Plus the
standard deviation of each across folds, which is what says whether a single held-out
number was luck.

On one Stellar run, 146,515 fragments over 2,966 peptides:

```
  fold        rows      MAD Th     RMS Th   reduction   Pearson r
     1      29,542      0.0452     0.0860       44.6%      0.6904
     2      29,590      0.0442     0.0856       44.7%      0.6872
     3      29,386      0.0443     0.0856       44.1%      0.6899
     4      28,902      0.0448     0.0860       44.3%      0.6889
     5      29,095      0.0445     0.0861       44.3%      0.6845

  pooled out-of-fold: MAD 0.0446 Th, RMS 0.0858 Th, r 0.6883
  spread across folds: MAD 0.0004 Th
  in-sample MAD 0.0431 Th; optimism 0.0015 Th
```

**Optimism** is the gap between what the model scores on rows it was built from and what it
scores on unseen peptides. Here it is 0.0015 Th, about 3% of the error being corrected, so
the model is generalizing rather than memorizing. A large gap would mean the opposite, and
would say that any in-sample figure is not worth quoting.

The before/after numbers everywhere else - the QC summary, the figures, the verdict line -
use these out-of-fold predictions. No optimistic number is reported anywhere as though it
were the result.

### When there are too few peptides

Cross-validation needs at least as many distinct peptides as folds, and in practice many
more. MARS refuses rather than producing folds of one or two peptides, and says how many it
found. `--cv-folds 0` falls back to a single fit with a held-out split - which is also
split by peptide, for the same reason.

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

Indistinguishable, once both are measured honestly. Both were trained on identical rows -
the features are verified bit-identical, see [parity](python-parity.md) - with identical
hyperparameters, identical weighting, and the same peptide-grouped fold split, on 146,515
fragments over 2,966 peptides from one Stellar run.

**Out-of-fold, which is the comparison that counts:**

| fold | C# MAD (Th) | Python MAD (Th) |
|---|---|---|
| 1 | 0.0452 | 0.0451 |
| 2 | 0.0440 | 0.0440 |
| 3 | 0.0440 | 0.0440 |
| 4 | 0.0448 | 0.0448 |
| 5 | 0.0445 | 0.0446 |
| **pooled** | **0.0445** | **0.0445** |

Same rows, same held-out peptides, same answer to four decimal places. An earlier
in-sample comparison put C# marginally ahead; cross-validation shows that gap was not real.

**In-sample**, both trained on everything, which answers a different question - whether the
two learn the same *function* rather than merely reach the same accuracy:

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

The dump carries a `peptide_group` column, and `compare_models.py` reproduces MARS's fold
assignment from it - sort the distinct peptides, deal round-robin - so both sides train on
exactly the same rows and score exactly the same held-out peptides, rather than merely
similar ones. Pass `--cv-folds 0` to skip the Python-side cross-validation and compare
in-sample only.
