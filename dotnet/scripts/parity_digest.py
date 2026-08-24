"""Summarise a match dump so it can be compared against a frozen reference.

The Python implementation is the reference MARS was ported from, and once it is removed from
the repository its output cannot be regenerated - only fetched from a tagged release. A full
dump is too large to keep in git: the five-file Stellar cohort is about 340 MB of CSV, 63 MB
compressed, against a repository whose entire history is 2 MB.

So the repository keeps a digest instead. For every column: a SHA-256 over the exact text the
dump contained, the number of finite values, and their minimum, maximum and mean. Any changed
value changes that column's hash, and the summary says which column moved and roughly how far.
That is about 8 KB per file rather than 67 MB, and it is enough to answer the question the
parity harness exists to answer - "does the matcher still produce what it produced then?" -
without being enough to answer "what exactly changed", which is what the archived dump is for.

    python parity_digest.py compute --csv cs.csv --out cs.digest.json
    python parity_digest.py check   --csv cs.csv --digest golden/run.digest.json

check exits non-zero on any disagreement, so it can gate a build.

The digest is taken from the C# dump, not the Python one, even though the Python
implementation is the reference. The two do not agree textually and never did: C# emits four
columns Python does not, and quotes the peptide field. What they agree on is every shared
value, which is what compare_matches.py checks and what the parity claim rests on. So the
digest freezes the C# output *at the moment it was verified identical to Python*, and the
provenance field records that verification - the hashes cannot say for themselves what they
were once checked against.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import sys
from pathlib import Path

# The dumps have one row per matched fragment and tens of columns; the default limit is far
# too small for a field holding a long modified sequence.
csv.field_size_limit(1 << 24)


def compute(path: Path) -> dict:
    """Digest one match dump."""
    with path.open(newline="") as handle:
        reader = csv.reader(handle)
        try:
            columns = next(reader)
        except StopIteration:
            raise SystemExit(f"{path} is empty")

        hashes = [hashlib.sha256() for _ in columns]
        finite = [0] * len(columns)
        low = [math.inf] * len(columns)
        high = [-math.inf] * len(columns)
        total = [0.0] * len(columns)
        rows = 0

        for row in reader:
            if len(row) != len(columns):
                raise SystemExit(
                    f"{path} line {rows + 2}: {len(row)} fields, header has {len(columns)}"
                )
            rows += 1
            for i, value in enumerate(row):
                # Hashed as text rather than as a parsed number, so a change in how a value is
                # formatted counts too: the dump is the interchange format, and a consumer
                # reading "1.0" where it used to read "1" is a change whatever the maths says.
                hashes[i].update(value.encode())
                hashes[i].update(b"\n")
                try:
                    x = float(value)
                except ValueError:
                    continue
                if math.isnan(x) or math.isinf(x):
                    continue
                finite[i] += 1
                low[i] = min(low[i], x)
                high[i] = max(high[i], x)
                total[i] += x

    return {
        "rows": rows,
        "columns": [
            {
                "name": name,
                "sha256": hashes[i].hexdigest(),
                "finite": finite[i],
                "min": None if finite[i] == 0 else low[i],
                "max": None if finite[i] == 0 else high[i],
                "mean": None if finite[i] == 0 else total[i] / finite[i],
            }
            for i, name in enumerate(columns)
        ],
    }


def check(current: dict, golden: dict) -> list[str]:
    """Every way the two can disagree, worst first."""
    problems: list[str] = []

    if current["rows"] != golden["rows"]:
        problems.append(
            f"row count: {current['rows']:,} now, {golden['rows']:,} in the reference"
        )

    now = {c["name"]: c for c in current["columns"]}
    then = {c["name"]: c for c in golden["columns"]}

    for missing in sorted(set(then) - set(now)):
        problems.append(f"column '{missing}' is gone; the reference has it")
    for added in sorted(set(now) - set(then)):
        problems.append(f"column '{added}' is new; the reference does not have it")

    # In reference order, so the report reads like the file rather than like a set.
    for column in golden["columns"]:
        name = column["name"]
        if name not in now:
            continue
        mine = now[name]
        if mine["sha256"] == column["sha256"]:
            continue

        detail = [f"column '{name}' differs"]
        if mine["finite"] != column["finite"]:
            detail.append(f"finite {mine['finite']:,} vs {column['finite']:,}")
        for stat in ("min", "max", "mean"):
            a, b = mine[stat], column[stat]
            if a is None or b is None:
                if a is not b:
                    detail.append(f"{stat} {a} vs {b}")
                continue
            if a != b:
                # Relative where it means something: an absolute difference in a column of
                # intensities and one in a column of ppm errors are not comparable.
                scale = max(abs(a), abs(b))
                rel = abs(a - b) / scale if scale else 0.0
                detail.append(f"{stat} {a:.12g} vs {b:.12g} ({rel:.3e} relative)")
        if len(detail) == 1:
            detail.append("values reordered or reformatted; every summary is unchanged")
        problems.append(", ".join(detail))

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    c = sub.add_parser("compute", help="write a digest for a match dump")
    c.add_argument("--csv", required=True, type=Path)
    c.add_argument("--out", required=True, type=Path)
    c.add_argument(
        "--provenance",
        action="append",
        default=[],
        metavar="KEY=VALUE",
        help="recorded verbatim in the digest; repeatable. Where the dump came from and what "
        "it was checked against, which the hashes cannot say for themselves.",
    )

    k = sub.add_parser("check", help="compare a match dump against a stored digest")
    k.add_argument("--csv", required=True, type=Path)
    k.add_argument("--digest", required=True, type=Path)

    args = parser.parse_args()

    if args.command == "compute":
        digest = compute(args.csv)
        if args.provenance:
            provenance = {}
            for item in args.provenance:
                if "=" not in item:
                    raise SystemExit(f"--provenance expects KEY=VALUE, got '{item}'")
                key, value = item.split("=", 1)
                provenance[key] = value
            digest["provenance"] = provenance
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(digest, indent=2, sort_keys=True) + "\n")
        print(f"{args.out}: {digest['rows']:,} rows, {len(digest['columns'])} columns")
        return 0

    golden = json.loads(args.digest.read_text())
    problems = check(compute(args.csv), golden)
    if not problems:
        print(f"{args.csv} matches {args.digest}: {golden['rows']:,} rows, every column identical")
        return 0

    print(f"{args.csv} does NOT match {args.digest}:", file=sys.stderr)
    for problem in problems:
        print(f"  {problem}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
