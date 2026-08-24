---
description: How to build, test and run the mars project
---

# Mars Development Workflow

MARS is the C# implementation under `dotnet/`. The Python implementation it was ported from was
removed after `v26.1.0`; the pytest and ruff steps that used to be here went with it.

## Running Tests
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars/dotnet
dotnet test -c Release
```

## Building
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars/dotnet
dotnet build -c Release -warnaserror
```

## Building With Vendor Support

Needs a full pwiz-sharp working tree at the commit pinned in `dotnet/pwiz-sharp.json`. Without
it MARS reads and writes mzML and nothing else, which is what CI builds by default.

```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars/dotnet
dotnet build -c Release -p:PwizSharpDir=<path>/pwiz/pwiz-sharp -p:IAgreeToVendorLicenses=true
```

## Running Calibration on Example Data
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
mars calibrate \
  --mzml "example-data/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_*.mzML" \
  --prism-report example-data/Stellar-HeLa-GPF-PRISM.csv \
  --tolerance 0.3 \
  --min-intensity 500 \
  --output-dir example-data/output/
```

## Checking a Matcher Change Against the Frozen Reference

Every model feature was verified against the Python implementation row by row before it was
removed, and that verification is frozen as a digest per file of the reference cohort. Re-run it
after changing the matcher or a feature.

```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
mars calibrate --mzml example-data/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_600-700_16.mzML \
  --prism-report example-data/Stellar-HeLa-GPF-PRISM.csv \
  --tolerance 0.3 --min-intensity 500 \
  --no-dedupe-library --no-recalibrate \
  --output-dir /tmp/parity --dump-matches /tmp/parity/cs.csv

python dotnet/scripts/parity_digest.py check \
  --csv /tmp/parity/cs.csv \
  --digest parity/golden/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_600-700_16.digest.json
```

A digest that trips means the matcher's output moved. Justify the change and regenerate the
digest; see `parity/README.md`.
