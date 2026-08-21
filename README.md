# MARS: Mass Accuracy Recalibration System

[![Build](https://github.com/maccoss/mars/actions/workflows/dotnet.yml/badge.svg)](https://github.com/maccoss/mars/actions/workflows/dotnet.yml)
[![Release](https://img.shields.io/github/v/release/maccoss/mars?display_name=tag&sort=semver&label=release)](https://github.com/maccoss/mars/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/download)
[![License](https://img.shields.io/github/license/maccoss/mars)](https://github.com/maccoss/mars/blob/main/LICENSE)

Learns the systematic part of a mass spectrometer's m/z error from spectral library
matches, and subtracts it from every peak in the file.

On Thermo Stellar ion-trap DIA data this cuts the median absolute fragment mass error
roughly in half:

| Stellar HeLa GPF-DIA, 5 files | Uncorrected | Corrected |
|---|---|---|
| Median absolute deviation | 0.0800 Th | **0.0469 Th** |
| Standard deviation | 0.1180 Th | **0.0883 Th** |
| Median error | -0.0082 Th | **-0.0048 Th** |

Measured by rematching the library against the written output. On an already
well-calibrated Astral run the same pipeline moves the spread by under 2% - there is
little systematic error left to remove. **Run `mars qc` first** to see whether your data
has anything worth correcting.

## About the Python implementation

MARS began as a Python package (`mars-ms`, versions `0.1.x`). **The C# tool documented
here is MARS going forward.** The Python implementation is frozen to bug fixes, is no
longer published to PyPI, and will be archived once the C# one has been used in earnest.
Its documentation is preserved in [README-python.md](README-python.md).

The C# implementation is not a rewrite that hopes to behave the same. Fragment matching
and every model feature were verified against the Python implementation row by row: across
160,947 matched fragments from two Stellar runs, all 24 shared columns agree with a maximum
absolute difference of **zero**. See [docs/python-parity.md](docs/python-parity.md).

Where they deliberately differ, it is because the port found four defects in the Python
implementation - including an invalid SHA-1 checksum on every mzML it has ever written.
Those are listed in
[docs/dotnet-port-spec.md](docs/dotnet-port-spec.md#10a-defects-found-in-the-python-implementation).

Versions follow `YY.feature.patch` starting at `26.1.0`; the `0.1.x` line was the Python
package. See [release-notes/README.md](release-notes/README.md).

---

## Install

### Option 1: download a build (no .NET needed)

Grab an archive from the [Releases page](https://github.com/maccoss/mars/releases). Each is
self-contained - unpack it and run; nothing else to install.

| Platform | Archive |
|---|---|
| Windows x64 | `mars-{version}-win-x64.zip` |
| Windows on Arm | `mars-{version}-win-arm64.zip` |
| Linux x64 | `mars-{version}-linux-x64.tar.gz` |
| Linux arm64 | `mars-{version}-linux-arm64.tar.gz` |
| macOS Apple silicon | `mars-{version}-osx-arm64.tar.gz` |
| macOS Intel | `mars-{version}-osx-x64.tar.gz` |

```bash
tar xzf mars-26.1.0-linux-x64.tar.gz
./mars --version
```

`SHA256SUMS.txt` is published alongside. On macOS, Gatekeeper will quarantine an
unsigned download; clear it with `xattr -d com.apple.quarantine mars` or allow it once
under System Settings > Privacy & Security.

Builds of unreleased commits are available as workflow artifacts on any CI run, under the
Actions tab.

### Option 2: build a single binary yourself

Build once (see [Build from source](#build-from-source)) and copy the resulting file
anywhere; it carries its own runtime.

```bash
# from the dotnet/ directory, pick the target you want
dotnet publish MARS/MARS.csproj -c Release -f net8.0 \
    -r win-x64      --self-contained true -p:PublishSingleFile=true -o publish/win-x64
dotnet publish MARS/MARS.csproj -c Release -f net8.0 \
    -r linux-x64    --self-contained true -p:PublishSingleFile=true -o publish/linux-x64
dotnet publish MARS/MARS.csproj -c Release -f net8.0 \
    -r osx-arm64    --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

Produces a ~69 MB `mars` (`mars.exe` on Windows). Other runtime identifiers:
`linux-arm64`, `osx-x64`.

### Option 3: install the .NET runtime

A framework-dependent build is about 2 MB but needs the **.NET 8 runtime** (or newer - MARS
rolls forward). To *build* MARS you need the **.NET 8 SDK**, which includes the runtime.

**Windows**

```powershell
winget install Microsoft.DotNet.SDK.8
# runtime only:
winget install Microsoft.DotNet.Runtime.8
```

Or download from [dot.net/download](https://dotnet.microsoft.com/download/dotnet/8.0).

**Linux**

```bash
# Ubuntu 22.04+ / Debian 12+
sudo apt update && sudo apt install -y dotnet-sdk-8.0

# Fedora / RHEL
sudo dnf install -y dotnet-sdk-8.0

# any distro, no root required
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export PATH="$HOME/.dotnet:$PATH"
```

**macOS**

```bash
brew install --cask dotnet-sdk
```

Or download the `.pkg` from
[dot.net/download](https://dotnet.microsoft.com/download/dotnet/8.0). On Apple silicon
take the **Arm64** build.

Check it worked:

```bash
dotnet --list-sdks
```

## Build from source

```bash
git clone https://github.com/maccoss/mars.git
cd mars/dotnet
dotnet build -c Release
dotnet test
```

The CLI lands at `MARS/bin/Release/net8.0/mars` (`mars.exe` on Windows). Add that
directory to your `PATH`, or publish a single binary as above.

**Dependencies are handled by `dotnet build`.** There is exactly one NuGet package,
`Parquet.Net`, used to read DIA-NN libraries. It brings a small native compression library
(`nironcompress`) that ships alongside the binary in every release archive - nothing has to
be installed separately, but it does mean release artifacts are per-platform.

`.blib` files are read through a managed SQLite reader written for this purpose rather than
`Microsoft.Data.Sqlite`, so that path adds no native code. `MARS.Core` and `MARS.OspreyML`
have no package references at all and are pure managed; only `MARS.IO` pulls in
`Parquet.Net`.

Targets `net8.0` by default, which runs unchanged on .NET 9 and 10. With a .NET 10 SDK
installed you can build the full matrix:

```bash
dotnet build -c Release -p:MarsIncludeNet10=true
```

### Platform status

CI builds and tests on Windows, Linux and macOS, and packages all six runtime
identifiers on every push.

| Platform | Status |
|---|---|
| Windows x64 | Build, tests and packaging in CI; full pipeline run on 6 GB of real data |
| Linux x64 | Build, tests and packaging in CI; verified locally on real mzML |
| macOS arm64 / x64 | Build, tests and packaging in CI. **Not yet run on real data** by a human. |
| Windows on Arm | Cross-compiled and packaged; binary confirmed ARM64, **not** smoke tested - no hosted Windows Arm runner |
| Linux arm64 | Cross-compiled and packaged, **not** smoke tested - no hosted arm64 Linux runner |

> **On Windows on Arm and macOS Intel**, `Parquet.Net`'s native compression library is not
> published for the platform, so a DIA-NN library compressed with **LZ4 or LZO** cannot be
> read there and fails with a clear "no compression codec" message. Snappy (parquet's usual
> default, and what DIA-NN writes), Gzip, Brotli, Zstd and uncompressed all work, as do
> Skyline PRISM reports and `.blib` libraries. Everything else in MARS is unaffected.

## Usage

The command is identical on all three platforms; only the path to the binary differs.

```bash
# Linux / macOS
./mars calibrate --mzml-dir runs/ --prism-csv report.csv --output-dir corrected/

# Windows (PowerShell)
.\mars.exe calibrate --mzml-dir runs\ --prism-csv report.csv --output-dir corrected\
```

### Before you correct anything

```bash
mars qc --mzml-dir runs/ --prism-csv report.csv
```

Reports the mass error already present, in both Th and ppm, without training or writing
anything. If the error is already small, MARS has nothing to offer and you have learned
that cheaply.

### Correcting a set of runs

```bash
mars calibrate \
    --mzml-dir runs/ \
    --prism-csv skyline-report.csv \
    --output-dir corrected/
```

Writes `{input}-mars.mzML` for each input, plus `mars_model.json`,
`mars_qc_summary.txt` and `mars_qc_report.html`. All input files are fitted together as one
cohort, which is what lets the model learn drift across a run sequence rather than only
within a file.

`mars_qc_report.html` is the one to look at: the error distribution before and after, the
error across retention time and m/z, feature importance, and a panel per feature. It is a
single self-contained file with everything embedded, so it can be emailed as an attachment
and read by someone who has neither the data nor the tool. See
[docs/qc-report.md](docs/qc-report.md), or pass `--no-html-report` to skip it.

For high-resolution data use a relative tolerance:

```bash
mars calibrate --mzml-dir runs/ --prism-csv report.csv \
    --tolerance-ppm 10 --output-dir corrected/
```

### Reusing a model

```bash
mars apply --model corrected/mars_model.json --mzml-dir more-runs/ \
    --output-dir corrected/ --validate
```

### Checking the file format is handled correctly

```bash
mars verify runs/one.mzML
```

Round-trips the file applying a **null correction**, then checks the result decodes to
bit-identical m/z and intensity arrays with a valid index and checksum. Run this first if
a corrected file misbehaves downstream: it separates a file-format problem from a model
problem, and those have very different fixes.

### Commands

| Command | Purpose |
|---|---|
| `calibrate` | Learn a correction from library matches and write recalibrated mzML |
| `apply` | Apply an existing model to more files |
| `qc` | Report mass accuracy without training or writing |
| `verify` | Round-trip a file with a null correction and check it |
| `compare` | Compare two mzML files on decoded values |

Every command takes `--help`. Diagnostics go to stderr so stdout stays pipeable. Exit
codes: `0` success, `1` input error, `2` insufficient training data, `3` output validation
failure.

### Frequently used options

| Option | Default | Meaning |
|---|---|---|
| `--mzml`, `--mzml-dir` | - | Input files, a glob, or a directory |
| `--prism-csv` | - | Skyline PRISM report (recommended library source) |
| `--library` | - | `.blib`, DIA-NN `report-lib.parquet`, or a PRISM `.csv` |
| `--diann-report` | beside the library | DIA-NN `report.parquet`, for RT windows |
| `--tolerance` | 0.3 Th | Matching tolerance; use `--tolerance-ppm` for Orbitrap/Astral |
| `--min-intensity` | 500 | Minimum peak intensity usable as a training row |
| `--max-isolation-window` | - | Leave wider isolation windows uncorrected |
| `--temperature-dir` | - | RF temperature logs (`RFA2-*.csv`, `RFC2-*.csv`) |
| `--threads` | all cores | Worker threads |
| `--on-reorder` | `clamp` | What to do if a correction would unsort the m/z array |

## Which library do I need?

One with **theoretical** fragment m/z. A [Skyline PRISM
report](Skyline-PRISM-Report/Skyline-PRISM.skyr) is the best-supported source. A `.blib`
without peak annotations cannot be used and MARS will say so rather than produce a bad
model. See [docs/spectral-libraries.md](docs/spectral-libraries.md).

## A note on reproducibility

Identical input produces identical decoded m/z values, on any thread count and any
platform. Compressed **file bytes** are not portable across platforms, because runtimes
ship different zlib builds - the same input produced 1,176,380 bytes on Windows and
1,176,172 on Linux with every decoded value identical. Compare files with `mars compare`,
not `cmp`. See [docs/algorithm.md](docs/algorithm.md#determinism).

## Documentation

[docs/](docs/) is the full documentation. The pages most people want:

| | |
|---|---|
| [docs/algorithm.md](docs/algorithm.md) | How the recalibration works: matching, features, model, correction |
| [docs/cli-reference.md](docs/cli-reference.md) | Every command and option, and what the exit codes mean |
| [docs/spectral-libraries.md](docs/spectral-libraries.md) | Library sources and choosing a tolerance |
| [docs/qc-report.md](docs/qc-report.md) | How to read the QC figures |
| [docs/model.md](docs/model.md) | The gradient boosted trees, in depth |
| [docs/mzml-passthrough.md](docs/mzml-passthrough.md) | How output files are written |
| [docs/architecture.md](docs/architecture.md) | A map of the code, for anyone modifying it |
| [docs/python-parity.md](docs/python-parity.md) | How this is verified against the Python implementation |
| [docs/dotnet-port-spec.md](docs/dotnet-port-spec.md) | Port specification, acceptance gates, measured results |
| [dotnet/README.md](dotnet/README.md) | The C# source tree |
| [release-notes/](release-notes/) | Per-version release notes and the release process |

## License

MIT. See [LICENSE](LICENSE).
