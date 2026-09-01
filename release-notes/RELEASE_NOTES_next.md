# MARS vNEXT Release Notes

One-sentence summary of the release, written when there is something to summarise.

## New Features

## Bug Fixes

- **Two decisions in the .NET port specification were invisible on GitHub.** Section 2 of
  `docs/dotnet-port-spec.md` records the decisions the port was built on as a table. Two of its
  six rows - the mzML passthrough strategy, and that the Python implementation would not be
  removed until the acceptance gates passed - sat below a later subsection, separated from the
  table header by a heading, three paragraphs and a code block. Markdown ends a table at the
  first line that is not part of it, so GitHub rendered those two rows as literal
  pipe-delimited text at the foot of an unrelated section, and anyone reading the table saw
  four decisions where the spec records six. Both rows are now in the table, wording unchanged.
  They had been misplaced since the document was added, so `v26.1.0` and `v26.2.0` shipped with
  the table short; every other table in the file was checked and none is split.

## Performance

## Breaking Changes

- **MARS now targets .NET 10, and building it requires a .NET 10 SDK (10.0.100 or newer).**
  Previously MARS built `net8.0` with `net10.0` available behind `-p:MarsIncludeNet10=true`.
  It is now a single `net10.0` target; `MarsIncludeNet10` and `MarsTargetFrameworks` no longer
  exist, and `-f net8.0` on a publish command will fail.

  The floor comes from pwiz-sharp, the ProteoWizard .NET port that MARS reads Thermo, Bruker
  and Sciex data through and writes mzXML, mzMLb and mgf with. It retargeted from .NET 8 to
  .NET 10 in [ProteoWizard/pwiz PR #4619](https://github.com/ProteoWizard/pwiz/pull/4619), and
  .NET reference compatibility is forward-only: a `net8.0` MARS cannot reference a `net10.0`
  pwiz-sharp at all.

  **Nothing changes for anyone running a release binary.** The published artifacts are
  self-contained - they carry their own runtime - so a downloaded `mars` needs no .NET
  installed, on any of the five platforms. A framework-dependent build needs the .NET 10
  runtime instead of .NET 8, and still rolls forward past it. Corrected mzML output is
  unaffected: this is a build-time change, and both gates were re-run against the new target.
  The frozen parity guard matches on all five files of the Stellar reference cohort - 352,349
  matched fragments, every column identical to the digests taken when the matcher was verified
  against the Python implementation - and the determinism tests still produce bit-identical
  output at any thread count.

  If you build from source, install a .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`,
  or `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0`).
  `dotnet --list-sdks` has to show a 10.x entry.

- **The pinned pwiz-sharp commit moved to the PR #4619 branch.**
  `dotnet/pwiz-sharp.json` now pins `Skyline/work/20260612_net8_port` at
  `52acb7fd79baa0bd046899f48ca422ca9ab6e87d`, replacing the `chambem2/pwiz-sharp` pin that
  PR #4178 was opened from. #4619 continues the same work from a shared integration branch;
  the old branch has diverged and is still .NET 8. A local vendor build needs a fresh checkout
  at the new commit - the branch name still says `net8`, the tree does not.

  Builds against pwiz-sharp no longer need a `global.json` written for them, and writing one
  breaks the build: the pwiz repository root now carries one asking for SDK 10.0.100, and an
  SDK-8 pin dropped under `pwiz-sharp/` shadows it. CI used to write exactly that; it no
  longer does.
