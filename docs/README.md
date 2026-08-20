# MARS documentation

| Document | What it covers |
|---|---|
| [algorithm.md](algorithm.md) | The recalibration algorithm: fragment matching, the 22 features, the model, and how the correction is applied. Start here. |
| [spectral-libraries.md](spectral-libraries.md) | The four library sources, what makes a usable one, and how to pick a tolerance. |
| [mzml-passthrough.md](mzml-passthrough.md) | How MARS writes mzML without disturbing anything it did not mean to change. |
| [python-parity.md](python-parity.md) | How the C# implementation is checked against the Python one row by row, what agrees, and what parity cannot cover. |
| [dotnet-port-spec.md](dotnet-port-spec.md) | The specification governing the Python-to-C# port: decisions, acceptance gates, measured results, and four defects the port found in the Python implementation. |

For installing and running MARS, see the [top-level README](../README.md). For the C#
source tree, see [dotnet/README.md](../dotnet/README.md).

## Quick answers

**What does MARS actually correct?** m/z values of MS2 peaks. Intensities, MS1 spectra,
chromatograms and metadata are untouched. See [algorithm.md](algorithm.md#step-4-correction).

**Will it help my data?** Run `mars qc` before correcting anything. On Stellar ion-trap data
it cuts the median absolute mass error roughly in half; on an already well-calibrated Astral
run it moves the spread by under 2%. See
[algorithm.md](algorithm.md#results).

**What library do I need?** One with *theoretical* fragment m/z. A Skyline PRISM report is
the best-supported option. A `.blib` without peak annotations cannot be used and MARS will
say so. See [spectral-libraries.md](spectral-libraries.md).

**Is the output identical run to run?** The decoded m/z values are, on any thread count and
any platform. The compressed file bytes are not portable across platforms, because runtimes
ship different zlib builds. See
[algorithm.md](algorithm.md#determinism).

**Something is wrong with a corrected file.** Run `mars verify <input>` first. It round-trips
the file with a null correction and checks the index, the checksum and the decoded arrays,
which separates a file-format problem from a model problem. See
[mzml-passthrough.md](mzml-passthrough.md#verifying-output).
