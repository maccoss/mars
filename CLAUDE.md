# AGENTS.md

This file provides context and instructions for AI agents working on the Mars repository.

## Repository Overview

MARS (Mass Accuracy Recalibration System) calibrates DIA mass spectrometry data, primarily
from the Thermo Stellar instrument. It learns the systematic part of the m/z error from
spectral library matches with gradient boosted trees, then subtracts it from every peak and
writes a corrected mzML.

MARS is the C# implementation under `dotnet/`. The Python implementation it was ported from
has been removed; see "The Python implementation is gone" below.

## Continuous Integration (CI/CD)

The repository uses GitHub Actions for CI/CD, defined in `.github/workflows/`.

### Workflows

1.  **.NET (`dotnet.yml`)** - the one that matters. Triggers on pushes and pull requests
    touching `dotnet/`.
    *   Builds and tests on ubuntu, windows and macos with `-warnaserror`.
    *   Packages all five self-contained artifacts (`win-x64`, `win-arm64`, `linux-x64`,
        `linux-arm64`, `osx-arm64`) on every run, so packaging breaks surface
        here rather than at tag time.
    *   Builds and tests against `net10.0` as well, reported rather than gated. Pass
        `-p:MarsIncludeNet10=true`, never a semicolon-separated framework list - the shell
        eats the quotes and MSBuild misreads it.
    *   Runs **determinism** and the **vendored Osprey.ML drift guard** as separate jobs so
        a failure in either is unmistakable.

2.  **.NET release (`dotnet-release.yml`)** - triggers on a `v*` tag. Preflights that the
    tag, `<Version>` and the release notes agree before building anything, then publishes
    the five artifacts and the GitHub Release. Also runs manually to build artifacts without
    releasing.

There is no Python workflow and no PyPI workflow. Both went with the Python implementation.

## Common Development Tasks

From `dotnet/`:

*   **Build:** `dotnet build -c Release -warnaserror`
*   **Test:** `dotnet test -c Release`
*   **Single-file binary:** `dotnet publish MARS/MARS.csproj -c Release -r <rid>
    --self-contained true -p:PublishSingleFile=true`
*   **Check against Python:** see `docs/python-parity.md`

### Building with vendor support

Reading Thermo, Bruker or Sciex data, and writing anything but mzML, needs
[pwiz-sharp](https://github.com/ProteoWizard/pwiz/pull/4178) - the .NET port of the
ProteoWizard core, still an unmerged draft with no package feed. The reference is optional:
without a checkout, `MARS_NO_PWIZ` drops that code and MARS reads and writes mzML exactly as
before, which is the configuration CI builds. Do not break that - a plain
`dotnet build`/`dotnet test` with no pwiz anywhere has to keep working.

```
dotnet build -c Release -p:PwizSharpDir=<path>/pwiz/pwiz-sharp -p:IAgreeToVendorLicenses=true
```

Three things that are not obvious:

*   It needs the **full** pwiz working tree, not a sparse checkout of `pwiz-sharp/`. Bruker
    reads its archives from `pwiz_aux` and pulls VC90 CRT files from `pwiz_tools/Shared/Lib`.
*   pwiz-sharp needs a `global.json` pinning SDK 8, which is absent from the branch. Without
    one, a nested `dotnet run` for its vendor pins generator fails to resolve an SDK.
*   `dotnet/Directory.Build.rsp` carries a `WarningsNotAsErrors` that a single-file publish
    needs. It repeats pwiz's own list because a global property replaces rather than extends
    what a project sets; keep it in step with `pwiz-sharp/Directory.Build.props`.

The scripts under `dotnet/scripts/` are Python and need only their own dependencies; there is
no package to install.

## Release Notes

One rolling draft, `release-notes/RELEASE_NOTES_next.md`. `release-notes/README.md` is the
authoritative process; the short version:

*   **Append to the draft as you land changes.** It is renamed to
    `RELEASE_NOTES_v{version}.md` at release time and published verbatim as the GitHub
    Release description, so write it for the people reading the Releases page.
*   **Never edit a released notes file.** Versioned files record what shipped.
*   **Be specific:** what was fixed and why, with numbers where they exist.
*   **Group related changes:** New Features, Bug Fixes, Performance, Breaking Changes.
*   **Flag anything that changes written output.** Corrected mzML files may already be in
    downstream pipelines.

## Documentation

*   `docs/` is the detailed documentation: the algorithm, the model and training, library
    guidance, the mzML passthrough contract, the parity harness, and the port
    specification. Update these when behaviour changes, not just the code comments.
*   `README.md` is what a new user reads first. `README-python.md` documents the Python
    implementation that was removed, and says where to find it; leave it as a signpost rather
    than extending it.

## The Python implementation is gone

The C# implementation under `dotnet/` **is** MARS. The Python one it was ported from was
removed after `v26.1.0`, so that nobody reaches for it by accident. It is not lost: `v26.1.0`
and every earlier tag carry it, along with the harness that drove it.

Do not port a Python quirk into C# without checking `docs/dotnet-port-spec.md` section 10a
first - four of them are known defects that C# deliberately does not reproduce.

**The parity check still exists, and still matters.** Fragment matching and every model feature
were verified against the Python implementation row by row before it went, and that verification
is frozen in `parity/golden/` as a digest per file of the reference cohort. If you change the
matcher or a feature, re-run it:

    python dotnet/scripts/parity_digest.py check --csv <dump> --digest parity/golden/<run>.digest.json

A digest that trips means the matcher's output moved. That is what a deliberate change to the
matcher does, so it is a prompt to justify the change and regenerate the digest - not to revert.
What it must not be is ignored. See `parity/README.md`, which also says what the reference does
not cover.

It cannot be extended. New reference data would need the Python implementation, which now means
checking out a tag.

## Versioning

`YY.feature.patch`, starting at `26.1.0`. The version lives in exactly one place,
`dotnet/Directory.Build.props` (`<Version>`), and changes only at release time. The
`0.1.x` line was the Python package. `release-notes/README.md` is the authoritative
process; pushing a `v{version}` tag builds every artifact and creates the GitHub Release.

## Style Guidelines

*   **No Emojis:** Do not use emojis in any output, documentation, source code comments, or Jupyter notebooks. Keep all text professional and plain.
