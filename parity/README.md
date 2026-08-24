# Frozen parity reference

MARS is a port of a Python implementation, and every feature the model sees was verified
against it row by row. That verification is what [the parity doc](../docs/python-parity.md)
describes, and it depended on being able to run both implementations.

The Python implementation is being removed from this repository. It is not gone - `v26.1.0` and
every earlier tag carry it, and the harness that drove it - but it will not be on `main`, so
its output cannot be regenerated here. What is kept instead is enough to answer the question
the harness existed to answer: **does the matcher still produce what it produced when it was
verified?**

## What is in this directory

`golden/*.digest.json`, one per file of the five-file Stellar reference cohort. Each holds, for
every column of the match dump: a SHA-256 over the exact text, the number of finite values, and
their minimum, maximum and mean. About 8 KB per file, 44 KB for the cohort, against 296 MB of
CSV.

`SHA256SUMS.txt`, the checksums of the archived dumps described below.

## Checking against it

Produce a dump the same way the digest was produced, then compare:

```bash
mars calibrate --mzml example-data/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_600-700_16.mzML \
    --prism-csv example-data/Stellar-HeLa-GPF-PRISM.csv \
    --tolerance 0.3 --min-intensity 500 \
    --no-dedupe-library --no-recalibrate \
    --output-dir /tmp/parity --dump-matches /tmp/parity/cs.csv

python dotnet/scripts/parity_digest.py check \
    --csv /tmp/parity/cs.csv \
    --digest parity/golden/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_600-700_16.digest.json
```

It exits non-zero and names the column that moved. The options matter: `--no-dedupe-library`
because the reference was taken that way, and `--no-recalibrate` because the dump is of the
matched rows, not of a correction.

A digest that trips is not automatically a bug. It says the matcher's output changed, which is
exactly what a deliberate change to the matcher does. Read it as "justify this", not as "revert
this" - and if the change is intended, regenerate the digest and say why in the commit.

## Why the digest is of the C# output

The Python implementation is the reference, but the digest is taken from the C# dump. The two
never agreed textually: C# emits four columns Python does not - `retention_time`,
`entry_index`, `fragment_index`, `peptide_group` - and quotes the peptide field. What they agree
on is every shared value, which is what `compare_matches.py` checks and what the parity claim
has always rested on.

So each digest freezes the C# output **at the moment it was verified identical to Python**, and
records that verification in its `provenance` field: the cohort, the options, the MARS version
and commit, the Python row count, and the comparison result. The hashes cannot say for
themselves what they were once checked against.

Taken on 2026-08-24, against Python `mars` 0.1.5, over 352,349 matched fragments in five files.
Every shared column agreed to a maximum absolute difference of **0.000e+00**, on every row both
implementations found.

## The archived dumps

The digests detect a change and name the column. They cannot show the offending row, because
they do not contain one. For that, the full dumps are attached to the
[v26.1.0 release](https://github.com/maccoss/mars/releases/tag/v26.1.0):

| Asset | What it is |
|---|---|
| `mars-parity-python-reference-26.1.0.tar.gz` | The Python dumps and the comparison logs. Irreplaceable: nothing on `main` can produce these again. |
| `mars-parity-csharp-dumps-26.1.0.tar.gz` | The C# dumps the digests were computed from. Regenerable in principle, kept so a tripped digest can be differenced without first reproducing the run. |

Fetch and verify:

```bash
gh release download v26.1.0 --repo maccoss/mars     --pattern 'mars-parity-python-reference-*.tar.gz' --dir parity-archive
sha256sum -c parity/SHA256SUMS.txt          # run from the directory holding the archive
tar xzf parity-archive/mars-parity-python-reference-26.1.0.tar.gz
```

`--repo` is needed because `gh` otherwise infers the repository from the working directory, and
fails without explanation outside a clone.

Then difference the two sides with `compare_matches.py`, which compares shared columns
numerically and so is unbothered by the textual differences above:

```bash
python dotnet/scripts/compare_matches.py --csharp cs-<run>.csv --python py-<run>.csv
```

This path was walked end to end when the archives were published: downloaded, checksum
verified, extracted, and differenced against a freshly generated C# dump, which reported every
shared column agreeing to 0.000e+00.

## What this does not cover

The reference is five Stellar GPF-DIA files. It says nothing about Astral data, about vendor
formats, or about any library reader other than the PRISM CSV path - and it cannot be extended,
because extending it would mean running the Python implementation, which is what a tag is for.

It is a regression guard on the matcher and the features, not a proof of correctness.
