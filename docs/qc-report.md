# Reading the QC report

Both `mars qc` and `mars calibrate` write two reports:

- **`mars_qc_summary.txt`** - the numbers, for a pipeline or a quick look.
- **`mars_qc_report.html`** - the same numbers plus the figures, as one self-contained file.

`qc` runs before any model exists, so its report shows the error **as measured** and how it
varies with each feature. `calibrate` shows the same figures with an after-correction
overlay, plus an after-heatmap and feature importance. Everything below applies to both;
where they differ it says so.

The HTML file has no scripts, no external references, and fetches nothing when opened.
Everything is embedded, so it can be attached to an email and read by someone who has
neither the data nor the tool. A 22-feature report is around 210 KB.

`--no-html-report` skips it; `--html-report <path>` moves it.

## The verdict line

At the top, before any figure. After `calibrate`:

> **46.2% reduction** in median absolute error, 0.0802 → 0.0432 Th. The correction removed
> a substantial part of the mass error.

After `qc`, where there is no model to report on:

> **0.0802 Th** median absolute error across 146,515 matched fragments, with a median of
> -0.0042 Th. The median is close to zero, so there is no large constant offset. Whether the
> spread is systematic enough to remove is what fitting a model would show.

The `qc` line deliberately stops short of predicting how much is removable, because nothing
short of fitting a model answers that. What it can say is how much of the error is a plain
constant offset - a median far from zero - which is the most straightforwardly correctable
thing there is.

Median absolute deviation rather than standard deviation, because a handful of badly
matched peaks move a standard deviation and should not be allowed to decide whether the run
worked.

**A small number here is a legitimate result, not a failure.** On an already
well-calibrated instrument there is little systematic error to remove, and the report says
so rather than implying something went wrong. If it reports that essentially nothing was
removed, the correct response is usually to keep the original files.

## Mass error distribution

The uncorrected error against what is left after correction, overlaid. After `qc` there is
only the one distribution: the error as measured.

This is the headline figure and it is close to sufficient on its own. If the two
distributions are not visibly different, nothing further in the report matters.

What to look for:

- **A narrower after-distribution.** That is the whole point.
- **A shift toward zero.** The before-distribution is often offset - a systematic bias
  across the whole run - and correcting that alone is worth a lot.
- **A remaining spread that is roughly symmetric.** What is left should look like noise. A
  residual distribution that is still lopsided means there is structure the model did not
  capture.

The axis is bounded at a high percentile of the uncorrected error, so a few extreme rows
cannot squash the informative part into a sliver.

## Error across retention time and fragment m/z

Two panels after `calibrate`, before and after; one after `qc`. Colour is the **median**
error in each cell - median, not mean, because a few mismatched peaks in a sparse cell
would otherwise invent structure that is not there.

This is the most useful figure in a `qc` report. Visible structure means the error is
systematic, and systematic error is the kind MARS can remove; a featureless panel means it
is mostly noise, and calibrating will not achieve much.

This is the figure that tells you the error is *systematic* rather than random, and
therefore correctable at all:

- **Visible structure in the before panel** - bands, gradients, blocks - is systematic error.
  That is what MARS removes.
- **A washed-out after panel** is the goal. Colour surviving in the same places means the
  model did not capture that region.
- **Structure along the m/z axis alone** is the classic mass-axis miscalibration.
- **Structure along the time axis** is drift during acquisition.
- **Blocky vertical bands** usually track the GPF or DIA isolation scheme rather than
  anything physical, since each precursor window contributes a distinct set of fragments.

Empty cells are simply where no fragment matched.

## Feature importance

*`calibrate` only - there is no model to interrogate after `qc`.*

Permutation importance: how much the validation error degrades when one feature's values
are shuffled, normalized to sum to 1.

Split counts are not used, because a feature with many distinct values accumulates splits by
offering more places to cut whether or not those cuts help. Permutation importance measures
what the model would actually lose.

A feature near zero is carrying no weight. On the reference Stellar cohort the two RF
temperature features score below 0.01 each - which is why the advice is that temperature
logs are worth using when you have them and not worth chasing when you do not.

## Error against each feature

One panel per active feature. The background is a binned density of the measured error;
the lines are the **median error per column** - two after `calibrate`, before and after
correction, and one after `qc`.

Read the lines, not the cloud. The cloud is dominated by how many fragments happen to fall
in each region; the median line is the trend the model has to capture.

- **A sloped or curved before-line** is a real dependence, and the model should be
  flattening it.
- **A flat after-line** means the model captured that dependence.
- **An after-line that still slopes** is error the model left behind.
- **A flat before-line** means that feature carries no information about the error here, and
  should be near zero in the importance chart.

After `qc` there is only the one line, and it is read as a forecast: a sloped line is a
dependence a model could exploit, and a panel full of flat lines is a warning that there
may be little for one to learn.

A column with fewer than 20 rows is skipped rather than plotted, because a median over a
handful of points reads as signal when it is noise.

The panels cover every feature that was available, so the set changes with the data: no
injection time in the file means no injection-time panels, and temperature panels appear
only when `--temperature-dir` was supplied.

## How the figures are made

Worth knowing because it explains the one visible artifact.

Axes, labels, trend lines and bars are SVG written directly. The density layers are PNGs
embedded as data URIs, encoded by a small writer over the zlib already used for mzML. There
is no plotting dependency: every managed charting library for .NET either wraps a native
rasterizer or brings a large dependency tree, and avoiding exactly that is part of why the
C# implementation exists.

Going straight to SVG rectangles produced a 6 MB file - one rectangle per cell, 76,000 of
them - which is not emailable. As quantized rasters the same figures come to 213 KB.

The artifact: the density layers are raster, so they do not follow the reader's light or
dark theme the way the vector layers do. The ramps run light-to-dark, which reads correctly
on either.

## What the report does not tell you

- **Whether the library was right.** A model trained against "theoretical" m/z values that
  carry someone else's calibration error will produce a confident-looking report and wrong
  corrections. See [spectral libraries](spectral-libraries.md).
- **Whether the file is well-formed.** That is `mars verify`.
- **Whether the correction generalizes.** The validation MAE in the summary is the only
  guard, and it is computed on rows from the same cohort.
- **How much of the error is removable**, in a `qc` report. Only fitting a model answers
  that, which is what `calibrate` does.
