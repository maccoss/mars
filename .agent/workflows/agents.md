---
description: How to build, test and run the mars project
---

# Mars Development Workflow

MARS is the C# implementation under `dotnet/`. The Python implementation it was ported from was
removed after `v26.1.0`; the pytest and ruff steps that used to be here went with it.

Every command below runs from the repository root. They used to begin
`cd /home/maccoss/GitHub-Repo/maccoss/mars`, which is not where the repository is on any machine
but one - not even the one it was written on, where it sits on `D:`.

## Running Tests
// turbo
```bash
cd dotnet
dotnet test -c Release
```

## Building
// turbo
```bash
cd dotnet
dotnet build -c Release -warnaserror
```

## Building With Vendor Support

Needs a full pwiz-sharp working tree at the commit pinned in `dotnet/pwiz-sharp.json`. Without
it MARS reads and writes mzML and nothing else, which is what CI builds by default.

```bash
cd dotnet
dotnet build -c Release -p:PwizSharpDir=<path>/pwiz/pwiz-sharp -p:IAgreeToVendorLicenses=true
```

## Running Calibration on Example Data

`mars` is not on PATH: the console script came from `pip install -e .`, which went with the
Python implementation. Use the built binary, or `dotnet run --project dotnet/MARS/MARS.csproj --`.

```bash
./dotnet/MARS/bin/Release/net8.0/mars calibrate \
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

The commands are in [`parity/README.md`](../../parity/README.md), which is the one place they
are written down - they have already changed once, when `--prism-csv` became `--prism-report`,
and a second copy here would be the one that missed it. That page also explains why the check
cannot run without the reference cohort, which is not in this repository.
