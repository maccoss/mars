# Open questions

Things deliberately left undone, with enough context to pick up cold. Each says what was
measured, what is unresolved, and what would settle it.

## Generalizing a model across datasets

**The goal:** train once on a well-characterized run and apply that model broadly to new
data from the same instrument platform, rather than fitting a fresh model per cohort.

This is the most valuable open question here, and MARS is already built for it - `mars apply`
exists, the model file carries its feature list and acquisition-time offset, and
cross-validation already reports the number that matters for it. What has never been done is
the experiment.

What is known so far:

- Cross-validation estimates within-cohort generalization: a model scored on peptides it did
  not train on. On the reference Stellar run that costs 0.0014 Th against a corrected error
  of 0.0446 Th, about 3%.
- That is **not** the same question. Held-out peptides from the same run share its
  instrument state, its acquisition window, its space-charge conditions. A different run does
  not.
- The data-poor 400-500 window shows a 20% gap where the data-rich 600-700 window shows 3%,
  so the estimate is sensitive to how much the fit had to work with.

What would settle it, roughly in order:

1. **Train on run A, apply to run B, re-match and measure.** The infrastructure is all
   there: `mars calibrate --no-recalibrate` on A, `mars apply --model` to B, then `mars qc` on
   the corrected B against the same library. Compare against fitting B directly.
2. **Vary the gap between A and B**: same plate, same day, different day, different column,
   different instrument of the same model. The interesting output is where transfer stops
   working, not whether it works at all.
3. **Watch `absolute_time` specifically.** It is re-based per fit, so a model carries A's
   time origin. Transfer either has to re-base against B or drop the feature. This is the
   most likely thing to break quietly.
4. **Consider what "same platform" means for the feature set.** A model trained with RF
   temperature features cannot be applied to a run without those logs; loading fails cleanly
   rather than silently, which is correct, but it constrains what a broadly applicable model
   can use.

If transfer works, the practical payoff is large: no library required for routine runs, and
a correction that does not depend on how many peptides happened to be identified.

## Whether a robust loss beats trimming on more data

`--robust trim` is the default and `--robust huber` is available. The measurement behind that
choice is thinner than it first looked:

| | 600-700 window | 400-500 window |
|---|---|---|
| `trim` | 0.0442 Th | 0.0505 Th |
| `huber` | 0.0443 Th | 0.0518 Th |
| *fold-to-fold spread* | *0.0005 Th* | *0.0021 Th* |

On the larger window the difference is one part in four thousand - noise. On the smaller one
trim is ahead, but by less than the spread between folds. Trim is the safer default, not a
demonstrated winner.

The mechanism suggests Huber should do relatively better with more data: it errs by leaving a
mislabelled row about 79% of its weight, which costs most when there is little real signal to
outvote it. Worth re-running on a full cohort, and on Astral data, before treating the
default as settled. The Astral run is a particularly good test of this, at 1.4 million rows
over 81 thousand peptides - an order of magnitude more of both than the Stellar windows the
current default was chosen on. See [model.md](model.md#why-trimming-rather-than-a-robust-loss).

## A redescending weight

The untried middle between trimming and Huber. Tukey's biweight,
`w = (1 - (r/c)^2)^2` for `|r| <= c` and `0` beyond, goes to exactly zero past a multiple of
the threshold instead of decaying as `1/|r|`. That would soften the boundary - no cliff for a
row to sit astride - while still eliminating the far tail, which is the property trimming has
and Huber lacks.

About ten lines in `MzCalibrator.TrainRobust`, plus a decision about how `--robust-sigma`
should scale for it: Tukey down-weights *within* the threshold too, so the conventional
constant is around 4.685 sigma rather than 3.

## A native Huber objective in Osprey.ML

The current `--robust huber` reaches Huber by reweighting and refitting, which is exact for
the gradient but applies the robustness once rather than at every boosting round. A real
objective upstream would re-clip each round as the residuals shrink, and would avoid the
second full pass.

Only worth doing if the reweighted version proves valuable first. It is a pwiz change with
the same bit-identity discipline PR #4595 established, and should follow that PR rather than
stack on it.

## Hyperparameter tuning

Settled for now: **do not**. A sweep of 13 configurations from 50 trees at depth 4 to 400 at
depth 8 leaves out-of-fold MAD flat at 0.0445-0.0449 while in-sample drops from 0.0446 to
0.0347, and the largest configuration is the worst out-of-fold. See
[model.md](model.md#hyperparameters).

Worth revisiting only if the error floor moves - if the mismatched-peak population is dealt
with better, or if a transferred model turns out to be capacity-limited rather than
noise-limited.

## The Python CLI's import cost

`mars/cli.py` imports `__version__` from the package root, which runs
`mars/__init__.py` and so eagerly imports the submodules and their dependencies on
every invocation - including `mars --version`. Reading the distribution metadata
directly would avoid both the side effects and the startup cost.

Raised by the Copilot review on PR #9 and **not applied**: the Python implementation is
frozen to bug fixes, and startup cost is not a bug. Worth doing only if that track is
unfrozen; if it is retired as planned, this closes with it.
