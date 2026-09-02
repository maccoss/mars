# MARS: Mass Accuracy Recalibration System

[![Build](https://github.com/maccoss/mars/actions/workflows/dotnet.yml/badge.svg)](https://github.com/maccoss/mars/actions/workflows/dotnet.yml)
[![Release](https://img.shields.io/github/v/release/maccoss/mars?display_name=tag&sort=semver&label=release)](https://github.com/maccoss/mars/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/github/license/maccoss/mars)](https://github.com/maccoss/mars/blob/main/LICENSE)

Learns the systematic part of a mass spectrometer's m/z error from spectral library
matches, and subtracts it from every peak in the file.

Reads Thermo, Bruker and Sciex data directly as well as mzML, and writes mzML, mzXML, mzMLb or
mgf - so a run can be calibrated straight off the instrument with no conversion step.

On Thermo Stellar ion-trap DIA data this cuts the median absolute fragment mass error
roughly in half:

| Stellar HeLa GPF-DIA, 5 files | Uncorrected | Corrected |
|---|---|---|
| Median absolute deviation | 0.0800 Th | **0.0464 Th** |
| Standard deviation | 0.1180 Th | **0.0872 Th** |
| Median error | -0.0082 Th | **-0.0025 Th** |

Measured by rematching the library against the written output. On an already
well-calibrated Astral run the same pipeline moves the spread by under 2% - there is
little systematic error left to remove. **Run `mars qc` first** to see whether your data
has anything worth correcting.

## About the Python implementation

MARS began as a Python package (`mars-ms`, versions `0.1.x`). **The C# tool documented here is
MARS.** The Python implementation was removed after `v26.1.0`, so that nobody reaches for it by
accident; it is still at that tag and every earlier one, and its documentation is preserved in
[README-python.md](README-python.md).

The C# implementation is not a rewrite that hopes to behave the same. Fragment matching and
every model feature were verified against the Python implementation row by row. That
verification was re-taken over the full five-file reference cohort immediately before the Python
code was removed: **352,349 matched fragments, every shared column agreeing to a maximum
absolute difference of zero**. It is frozen in [parity/](parity/README.md) as a digest per file,
which is what a change to the matcher is checked against now.
See [docs/python-parity.md](docs/python-parity.md) for how the comparison was made.

Where they deliberately differ, it is because the port found four defects in the Python
implementation - including an invalid SHA-1 checksum on every mzML it has ever written.
Those are listed in
[docs/dotnet-port-spec.md](docs/dotnet-port-spec.md#10a-defects-found-in-the-python-implementation).

Versions follow `YY.feature.patch` starting at `26.1.0`; the `0.1.x` line was the Python
package. See [release-notes/README.md](release-notes/README.md).

---

## Install

**MARS requires .NET 10.** Whether you have to install it depends on how you get MARS:

| | Needs .NET installed? |
|---|---|
| **Option 1** - download a release archive | **No.** Each archive is self-contained. |
| **Option 2** - build your own single binary | Yes, the **.NET 10 SDK** - to build it. What comes out needs nothing. |
| **Option 3** - framework-dependent build | Yes. The **SDK** to build it, and the **.NET 10 runtime** on every machine that runs it. |

If you are not sure, take Option 1. If you already have .NET installed, check the version
before assuming it is enough: **.NET 8 or 9 will not build MARS.** It fails with `NETSDK1045`,
repeated once per project, saying "The current .NET SDK does not support targeting .NET 10.0".

```bash
dotnet --list-sdks
```

At least one line has to start with `10.`.

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

**Needs the .NET 10 SDK** - see [Option 3](#option-3-install-net-10) below if you do not have
it. Build once (see [Build from source](#build-from-source)) and copy the resulting file
anywhere; it carries its own runtime, so the machine you copy it *to* needs nothing.

```bash
# from the dotnet/ directory, pick the target you want
dotnet publish MARS/MARS.csproj -c Release -f net10.0 \
    -r win-x64      --self-contained true -p:PublishSingleFile=true -o publish/win-x64
dotnet publish MARS/MARS.csproj -c Release -f net10.0 \
    -r linux-x64    --self-contained true -p:PublishSingleFile=true -o publish/linux-x64
dotnet publish MARS/MARS.csproj -c Release -f net10.0 \
    -r osx-arm64    --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

Produces a `mars` of about 72 MiB / 75 MB (`mars.exe` on Windows; 75,138,382 bytes
measured for win-x64 at 26.3.0). Other runtime identifiers:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`. An Intel Mac has to build from source:
there is no `osx-x64` release, and an `osx-arm64` binary will not run there.

### Option 3: install .NET 10

**Install the SDK, not the runtime**, unless you are certain you only ever want to run a
framework-dependent build someone else produced. The SDK includes the runtime and is what
`dotnet build` needs; a framework-dependent MARS is about 2 MB but has to find a .NET 10
runtime on every machine it runs on.

Installing .NET 10 alongside an existing .NET 8 or 9 is fine and expected - they coexist, and
nothing else on the machine switches to the new one.

**Windows** - the SDK:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Or, if you only need to *run* a framework-dependent build:

```powershell
winget install Microsoft.DotNet.Runtime.10
```

Either can also be downloaded from
[dot.net/download](https://dotnet.microsoft.com/download/dotnet/10.0).

**Linux**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

That is the route that works on any distribution and needs no root. The distribution packages
are fine where they exist, but a release that predates .NET 10 will not carry them - check
what you actually got before moving on:

```bash
sudo apt update && sudo apt install -y dotnet-sdk-10.0   # Ubuntu / Debian
```

```bash
sudo dnf install -y dotnet-sdk-10.0                      # Fedora / RHEL
```

**macOS**

```bash
brew install --cask dotnet-sdk
```

The `dotnet-sdk` cask tracks the current release, so confirm it gave you 10.x rather than
assuming. Otherwise download the `.pkg` from
[dot.net/download](https://dotnet.microsoft.com/download/dotnet/10.0) - on Apple silicon take
the **Arm64** build.

**Confirm it worked, on any platform.** This is the step worth not skipping:

```bash
dotnet --list-sdks
```

At least one line has to start with `10.` - for example `10.0.400 [C:\Program Files\dotnet\sdk]`.
Older versions listed alongside it do not matter: MARS ships no `global.json`, so `dotnet`
uses the newest SDK it finds. If the only lines say `8.0.x` or `9.0.x`, the install did not
land, and `dotnet build` will fail with `NETSDK1045` - "The current .NET SDK does not support
targeting .NET 10.0".

## Build from source

**Prerequisite: the .NET 10 SDK, 10.0.100 or newer.** Nothing else - no Python, no vendored
libraries to fetch. See [Option 3](#option-3-install-net-10) to install it, and
`dotnet --list-sdks` to confirm a `10.` line is there before you start.

```bash
git clone https://github.com/maccoss/mars.git
cd mars/dotnet
dotnet build -c Release
dotnet test
```

The CLI lands at `MARS/bin/Release/net10.0/mars` (`mars.exe` on Windows). Add that
directory to your `PATH`, or publish a single binary as above.

**Dependencies are handled by `dotnet build`.** Two NuGet packages, both on `MARS.IO`:

- `Parquet.Net`, used to read DIA-NN libraries. It brings a small native compression library
  (`nironcompress`) that ships alongside the binary in every release archive - nothing has to
  be installed separately, but it does mean release artifacts are per-platform.
- `Snappier`, referenced directly only to lift the version `Parquet.Net` would otherwise
  resolve transitively, which carried a high-severity advisory. Snappy is the codec DIA-NN
  writes, so this is on the path MARS actually uses. It goes away once `Parquet.Net` resolves
  a safe version on its own.

Both are confined to `MARS.IO`. `MARS.Core`, `MARS.OspreyML`, `MARS.Pwiz` and the `MARS`
executable itself have no package references at all and are pure managed. `MARS.Test` adds
xunit and the test SDK, which do not ship.

`.blib` files are read through a managed SQLite reader written for this purpose rather than
`Microsoft.Data.Sqlite`, so that path adds no native code.

**Why .NET 10 and not something older.** MARS targets `net10.0` single-target. The floor
comes from pwiz-sharp, the ProteoWizard .NET port MARS reads vendor formats through: it
retargeted to `net10.0`, and .NET reference compatibility is forward-only, so a `net8.0` MARS
could not reference it at all. MARS built `net8.0` by default until then. Nothing has to be
installed to *run* a release binary - those are self-contained.

### Platform status

CI builds and tests on Windows, Linux and macOS, and packages all five runtime
identifiers on every push.

| Platform | Status |
|---|---|
| Windows x64 | Build, tests and packaging in CI; full pipeline run on 6 GB of real data |
| Linux x64 | Build, tests and packaging in CI; verified locally on real mzML |
| macOS arm64 / x64 | Build, tests and packaging in CI. **Not yet run on real data** by a human. |
| Windows on Arm | Cross-compiled and packaged; binary confirmed ARM64, **not** smoke tested - no hosted Windows Arm runner |
| Linux arm64 | Cross-compiled and packaged, **not** smoke tested - no hosted arm64 Linux runner |

Vendor reading and the non-mzML outputs are a separate axis, because they need a build made
against pwiz-sharp. All six runtime identifiers publish and run with them; the gaps are that
**Sciex is Windows-only**, because its SDK is, and **mzMLb is x64-only**, because the HDF5
library it needs is published for x64 alone. Thermo and Bruker reading, and mzML and mzXML
writing, work on every target. None of it is exercised in CI yet - pwiz-sharp has no package
feed, so CI builds the mzML-only configuration.

> **On Windows on Arm and macOS Intel**, `Parquet.Net`'s native compression library is not
> published for the platform, so a DIA-NN library compressed with **LZ4 or LZO** cannot be
> read there and fails with a clear "no compression codec" message. Snappy (parquet's usual
> default, and what DIA-NN writes), Gzip, Brotli, Zstd and uncompressed all work, as do
> Skyline PRISM reports and `.blib` libraries. Everything else in MARS is unaffected.

## Usage

The command is identical on all three platforms; only the path to the binary differs.

```bash
# Linux / macOS
./mars calibrate --mzml-dir runs/ --prism-report report.csv --output-dir corrected/

# Windows (PowerShell)
.\mars.exe calibrate --mzml-dir runs\ --prism-report report.csv --output-dir corrected\
```

### Before you correct anything

```bash
mars qc --mzml-dir runs/ --prism-report report.csv
```

Reports the mass error already present, in both Th and ppm, without training or writing
anything. If the error is already small, MARS has nothing to offer and you have learned
that cheaply.

### Correcting a set of runs

```bash
mars calibrate \
    --mzml-dir runs/ \
    --prism-report skyline-report.csv \
    --output-dir corrected/
```

The inputs do not have to be mzML: a directory of Thermo `.raw`, or a Bruker `.d`, works the
same way, and `--output-format mzXML|mzMLb|mgf` picks what comes out.

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
mars calibrate --mzml-dir runs/ --prism-report report.csv \
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

### Input and output formats

| Read | Vendor | Platforms |
|---|---|---|
| `.mzML` | - | everywhere, by MARS itself |
| `.raw` | Thermo | Windows, Linux, macOS |
| `.d`, `.tdf`, `.tsf`, `.baf` | Bruker | Windows, Linux |
| `.wiff`, `.wiff2` | Sciex | Windows |

| Write | Notes |
|---|---|
| `mzML` (default) | The input byte for byte, except the m/z arrays that changed |
| `mzXML` | Cannot express ion mobility or some isolation-window terms |
| `mzMLb` | mzML in HDF5; roughly half the size. x64 only |
| `mgf` | MS2 peak lists only - no MS1, no chromatograms, no scan metadata |

Vendor formats and the non-mzML outputs come from
[pwiz-sharp](https://github.com/ProteoWizard/pwiz/pull/4178), the .NET port of the ProteoWizard
core. Released binaries carry the vendor SDKs, so a download opens a `.raw` with nothing else
installed; `mars --version` reports what the binary in front of you actually has. A MARS built
without pwiz reads and writes mzML exactly as before, and says so when asked for anything else
- see [the CLI reference](docs/cli-reference.md#input-formats).

Once pwiz-sharp merges upstream, MARS will stop shipping its own copies on Windows and use the
SDKs an installed Skyline-daily, Skyline or msconvert already provides - in that order, and
only after checking the version it finds. See
[open-questions.md](docs/open-questions.md#where-the-vendor-sdks-come-from).

MARS reads the mass analyzer from the file and configures itself: 0.3 Th on a trap, 10 ppm on
an orbitrap, TOF or Astral, with the QC report drawn in matching units. `--resolution` and
`--tolerance` override it. On a timsTOF the ion mobility dimension is collapsed - each frame's
mobility scans are combined into one spectrum per isolation window - because MARS models m/z
error, not mobility.

### Commands

| Command | Purpose |
|---|---|
| `calibrate` | Learn a correction from library matches and write recalibrated output |
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
| `--prism-report` | - | Skyline PRISM report, `.csv` or `.parquet` (recommended library source). `--prism-csv` still works |
| `--library` | - | `.blib`, DIA-NN `report-lib.parquet`, or a Skyline PRISM report |
| `--diann-report` | beside the library | DIA-NN `report.parquet`, for RT windows |
| `--tolerance` | 0.3 Th | Matching tolerance; use `--tolerance-ppm` for Orbitrap/Astral |
| `--min-intensity` | 500 | Minimum peak intensity usable as a training row |
| `--max-isolation-window` | - | Leave wider isolation windows uncorrected |
| `--temperature-dir` | - | RF temperature logs (`RFA2-*.csv`, `RFC2-*.csv`) |
| `--threads` | `auto` | Worker threads; `auto` is one per logical processor, and the run reports which it used |
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

**mzMLb is the exception**, and not because of anything MARS does: two mzMLb writes of identical
data differ byte-wise, because the HDF5 container records things that vary between writes. The
spectra are the same. Use mzML or mzXML where a checksum has to match.

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
