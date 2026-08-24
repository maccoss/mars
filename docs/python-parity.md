# Checking the C# implementation against the Python one

The Python implementation is the reference. It had been used on real data for long enough
that "the C# version computes the same thing" is a stronger statement about correctness
than any test written from first principles, because a test can only check what its author
already believed.

> **The Python implementation was removed after `v26.1.0`.** It is not lost - that tag and
> every earlier one carry it, along with the harness described here - but nothing on `main`
> can run it, so the comparison below cannot be re-taken here.
>
> It was taken before the code went, over the five-file Stellar cohort, and frozen: 352,349
> matched fragments, every shared column agreeing to a maximum absolute difference of
> **0.000e+00**. What is kept is a digest per file in [`parity/`](../parity/README.md),
> which is what to check a matcher change against now.

This page describes how that comparison is made and what it does and does not cover.

## Why aggregate agreement is not enough

The two implementations agree on the numbers that appear in a QC report. On the five-file
Stellar cohort both find **352,349 matches**, and measured on the written files:

| | before | C# | Python |
|---|---|---|---|
| MAD | 0.0800 Th | 0.0464 | 0.0472 |
| std | 0.1180 Th | 0.0872 | 0.0882 |

Both columns come from one paired run, re-taken after the injection-time fix moved the C#
column. Python landed within 0.0001 Th of its previous run, which is what says the two
measurements are comparable. Both implementations use the same 20 features on this cohort.

That is reassuring and almost meaningless on its own. It is a summary over hundreds of
thousands of rows, and a feature can be wrong in a way that never moves it. The two
features the model weights most heavily - `ions_above_0_1` at 0.346 importance and
`adjacent_ratio_0_1` at 0.292 - are sums over neighbouring peaks. An off-by-one in the
window boundary changes every row a little, the model re-fits around it, and the corrected
spread barely moves.

So the comparison has to be per row and per feature.

## Running it

This is how it was run while both implementations were present. Repeating it needs a
checkout of `v26.1.0` or earlier, where `dump_python_matches.py` and the `mars` package
live. To check a change against the frozen result instead - which is what you want day to
day - see [`parity/README.md`](../parity/README.md).

Both sides write the same CSV schema: one row per matched fragment, identified by the scan
and the library fragment, carrying every feature the model will see.

```bash
# C#
mars calibrate --mzml run.mzML --prism-report report.csv \
    --no-dedupe-library --no-recalibrate --dump-matches cs.csv --output-dir out/

# Python, from a v26.1.0 checkout
python dotnet/scripts/dump_python_matches.py \
    --mzml run.mzML --prism-csv report.csv --out py.csv

# Difference them
python dotnet/scripts/compare_matches.py --csharp cs.csv --python py.csv
```

`--no-dedupe-library` is required. The C# reader collapses transitions that repeat across
replicates and the Python one does not, so without it the two produce different row sets
for a reason that has nothing to do with correctness.

`compare_matches.py` exits non-zero on any disagreement. It gated this comparison while both
implementations existed; it cannot gate a build now, because nothing on `main` can produce
the Python side. `parity_digest.py` is what a build or a review can gate on today.

### What the comparison does

- **Joins on (scan number, ion annotation, expected m/z).** Peptide sequence is
  deliberately excluded: each side carries whatever form its library reader produced, and a
  formatting difference there is not a calibration difference.
- **Pairs repeated keys by position** rather than discarding them. A few precursors appear
  in more than one block of a PRISM report, so the same fragment can be matched twice in
  one scan through two library entries. Both implementations produce those duplicates; each
  group is ordered identically on both sides and paired off. A difference in the multiset
  would surface as an unmatched row.
- **Treats NaN on both sides as agreement**, and NaN on one side as a failure. NaN is how an
  undefined ratio reaches row selection, so the two agreeing that a row is undefined is
  agreement about the row's fate.
- **Reads with `float_precision="round_trip"`.** Pandas' default float parser drops the last
  digit, which invents differences of about 1e-16 in every column and buries the real ones.

## Result

On `Ste-2024-12-02_HeLa_20msIIT_GPFDIA_400-500_14.mzML` with the Stellar PRISM report:

```
Row counts        C# 14,432    Python 14,432
Row agreement     matched by both 14,432    C# only 0    Python only 0

24 columns compared, max absolute difference 0.000e+00 on every one.
```

That is one file, and 24 columns because `compare_matches.py` compares only the columns both
sides emit - the C# dump carries four more (`retention_time`, `entry_index`, `fragment_index`,
`peptide_group`) that the Python one never had, which is also why the frozen digests describe 31
columns rather than 24.

The final run before the Python implementation was removed covered all five files of the cohort
on the same terms: **352,349 rows, every shared column at 0.000e+00**, file by file.

Every feature, every row, bit-identical - including all six space-charge features and the
six ratios derived from them, `absolute_time` after re-basing, `log_tic`, `log_intensity`,
`injection_time` and `tic_injection_time`.

The comparison was checked against a perturbed copy to confirm it can fail: a 1e-8 shift in
one `observed_mz`, a 1% shift in one `ions_above_0_1`, and one ratio forced to NaN were all
detected and reported.

## What this does not cover

Parity is the right standard for the parts that were transcribed. It is not the standard
for everything:

- **The four deliberate divergences.** MARS in C# does not reproduce four defects in the
  Python implementation; see [the port spec](dotnet-port-spec.md) section 10a. Two of them
  change training and can be reproduced with `--python-compat` for an A/B run.
- **The model itself.** The gradient boosted trees are a different implementation, so
  per-peak corrected m/z values differ. Agreement there is statistical, not exact.
- **Everything Python has no counterpart for**: `mars verify`, the byte-splicing writer,
  the `--on-reorder` policies, command-line parsing, and the cross-platform packaging.
- **The `.blib` and DIA-NN paths**, which had their own readers on both sides. Only the PRISM
  CSV path was ever run through the harness, and now none of them can be: the harness needs the
  Python implementation. The PRISM parquet reader added afterwards was never compared against
  Python at all, because Python had no such reader - its equivalence is asserted against the
  PRISM CSV reader instead, in the test suite.

For those, ordinary tests are the only option, and the gaps are recorded in the test
coverage notes rather than papered over.
