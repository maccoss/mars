# MARS for .NET

C# port of MARS (Mass Accuracy Recalibration System). This file covers the source tree; for
using the tool, start at the [top-level README](../README.md).

- [Algorithm](../docs/algorithm.md) - what MARS computes and why
- [mzML passthrough](../docs/mzml-passthrough.md) - how output files are written
- [Port specification](../docs/dotnet-port-spec.md) - design, acceptance gates, and what the
  port found in the Python implementation

## Building

```
cd dotnet
dotnet build -c Release
dotnet test
```

Targets `net10.0`, single target. A **.NET 10 SDK, 10.0.100 or newer, is required to build**;
`dotnet --list-sdks` has to show a 10.x entry.

The floor is pwiz-sharp's. MARS used to build `net8.0` with `net10.0` behind an opt-in
property, because pwiz-sharp - the ProteoWizard .NET port MARS reads vendor formats through -
was net8.0 and .NET reference compatibility is forward-only. pwiz-sharp retargeted to
`net10.0` in [ProteoWizard/pwiz PR #4619](https://github.com/ProteoWizard/pwiz/pull/4619), so
the constraint reversed: a `net8.0` MARS cannot reference it at all. `MarsIncludeNet10` and
`MarsTargetFrameworks` are gone with the multi-targeting they selected.

Nothing has to be installed to *run* a release artifact - those are self-contained - and a
framework-dependent build rolls forward past .NET 10. This is a build-machine requirement.

Two NuGet references, both on `MARS.IO`: `Parquet.Net`, for DIA-NN libraries, which carries
a native compression library (`nironcompress`) - the only native code in the tree - and
`Snappier`, referenced directly only to lift the vulnerable version `Parquet.Net` resolves
transitively. `MARS.Core`, `MARS.OspreyML`, `MARS.Pwiz` and `MARS` have no package references
and are pure managed; `MARS.Test` adds xunit and the test SDK, which do not ship.

BiblioSpec `.blib` files are read through a managed SQLite reader written for this purpose
rather than `Microsoft.Data.Sqlite`, so that path adds no native code of its own.

## Commands

```
mars calibrate   Learn a calibration from library matches and write corrected mzML
mars apply       Apply an existing model to more files
mars qc          Report mass accuracy without training or writing
mars verify      Round-trip a file with a null correction and check it
mars compare     Compare two mzML files on decoded values
```

Every command takes `--help`. Diagnostics go to stderr so stdout stays pipeable.
Exit codes: 0 success, 1 input error, 2 insufficient training data, 3 output validation
failure.

### Typical run

```
mars calibrate \
    --mzml-dir raw/ \
    --prism-report skyline-report.csv \
    --temperature-dir temperature_csvs/ \
    --output-dir corrected/
```

Writes `{input}-mars.mzML` per file, plus `mars_model.json` and `mars_qc_summary.txt`.

### Before trusting any output

```
mars verify raw/run.mzML
```

Round-trips the file through the writer applying a null correction, then checks that the
result decodes to bit-identical m/z and intensity arrays, that every index offset lands
on the element it names, and that the SHA-1 checksum validates. This isolates the
file-format work from the science; run it first when something looks wrong.

## Layout

| Project | Contents |
|---|---|
| `MARS.Core` | Domain types, fragment matching, feature extraction, the calibration model, correction |
| `MARS.IO` | mzML passthrough reader/writer, library readers, managed SQLite |
| `MARS.OspreyML` | Compiles the vendored Osprey.ML sources |
| `MARS` | CLI |
| `MARS.Test` | Unit and contract tests |

## Vendored Osprey.ML

`third_party/Osprey.ML/` holds a copy of the gradient boosted trees from
`pwiz_tools/Osprey/Osprey.ML`. **Osprey.ML owns that code.** MARS carries a copy only
because pwiz has no package feed to consume yet.

Do not edit the vendored files. Fix things upstream in pwiz, then:

```
pwsh -File ./scripts/sync-osprey-ml.ps1 -PwizPath D:\Dev\pwiz          # report drift
pwsh -File ./scripts/sync-osprey-ml.ps1 -PwizPath D:\Dev\pwiz -Apply   # pull it down
```

`UPSTREAM.json` records the source commit and a SHA-256 per file, and `MARS.Test` fails
when they stop matching, so an accidental local edit becomes a visible test failure
rather than a silent fork.

## Determinism

MARS writes m/z values into files that get reprocessed and compared, so identical input
must produce a bit-identical output, at any thread count, on any platform. The
guarantees:

- Histogram accumulation parallelizes across FEATURES only, so each histogram is summed
  in ascending row order by one thread.
- Subsampling draws from `XorShift64`, seeded, never `System.Random`.
- Split selection walks features and bins in ascending order and takes a new best only
  on a strict improvement, so ties resolve to the lowest (feature, bin).
- Row partitioning is stable.
- Inference carries no cross-row accumulation, so parallelizing it cannot change a value.

CI asserts this by writing the same file at 1 and 16 threads and comparing bytes.
