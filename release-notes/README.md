# Release Notes

This directory contains per-version release notes for MARS.

## Two release tracks: Python and C# (.NET)

MARS ships two implementations side by side, released independently:

- **Python** - the `mars-ms` PyPI package. Notes: `RELEASE_NOTES_v{version}.md`
  (draft `RELEASE_NOTES_next.md`). Version source: `pyproject.toml` alone -
  `mars.__version__` reads it back out of the installed distribution metadata.
  Tag: `v{version}`.
- **C# (.NET)** - the `mars` CLI, published to GitHub Releases. Notes:
  `RELEASE_NOTES_dotnet-v{version}.md` (draft `RELEASE_NOTES_dotnet-next.md`). Version
  source: `dotnet/Directory.Build.props` (`<Version>`) alone - `MarsInfo.Version` reads it
  back off the assembly at run time, so there is nothing to keep in lockstep.
  Tag: `dotnet-v{version}`.

The two tracks keep **distinct tag namespaces and notes filenames** so their counters never
collide.

## Versioning

### C# track: CalVer

The .NET track uses a `YY.feature.patch` convention:

- **YY**: two-digit year (`26` for 2026)
- **feature**: incremented for each release containing new features
- **patch**: incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second
feature release).

### Python track: 0.1.x

The Python package is on PyPI at `0.1.5` and continues that line for now. Migrating it to
the same CalVer scheme is a decision for the next feature release; a jump from `0.1.x` to
`26.x.y` is legal but visible to anyone pinning the package.

The version is updated only at release time, not during development.

> [!NOTE]
> `mars.__version__` reports the version of the **installed distribution**, which in an
> editable checkout is whatever was current when `pip install -e .` last ran, not what
> `pyproject.toml` says now. That is the honest answer for an installed package and exactly
> right for a released wheel; re-run `pip install -e .` after a version bump if a source
> checkout needs to report the new number. The previous arrangement kept the version in
> three places, and one of them had already drifted (`mars/__init__.py` said `0.1.4` while
> the package shipped as `0.1.5`).

## File format

Each release gets one file. During development the unreleased draft lives in a `-next` file
and is renamed at release time.

```text
release-notes/
  README.md                          # this file
  RELEASE_NOTES_next.md              # working draft, Python track
  RELEASE_NOTES_dotnet-next.md       # working draft, C# track
  RELEASE_NOTES_v0.1.5.md
  RELEASE_NOTES_dotnet-v26.1.0.md
```

## Writing release notes

### During development

Maintain the `-next` draft for the track you are changing. Append entries as features and
fixes land. The file stays unversioned until the release is finalized, so the target version
can change - a planned patch release becomes a feature release once new functionality lands.

A change that affects both implementations gets an entry in both drafts, written for that
track's users.

### Content structure

```markdown
# MARS {track} v{version} Release Notes

One-sentence summary of the release.

## New Features

- Grouped by area (matching, model, mzML, CLI)
- What changed from the user's perspective, not implementation details

## Bug Fixes

- The bug, its impact, and what was fixed

## Performance

- With context: "Reduced pass 2 from 90 s to 30 s on a 1.2 GB file"

## Breaking Changes

- Anything requiring user action: renamed options, changed defaults, model format bumps
- Omit this section if there are none
```

Sections can be omitted if empty.

> [!IMPORTANT]
> **Delete the empty headings when you rename the draft.** Both drafts are seeded with all
> four headings so entries have somewhere to go during development, which means a renamed
> draft *always* arrives carrying the ones nobody filled in. Removing them is a step of the
> release, not something the draft gets right on its own. It matters most on the C# track,
> where the file is published verbatim as the GitHub Release description.

### Style

- Past tense: "Added", "Fixed", "Removed"
- Lead with user impact, not implementation
- Include specific numbers: MAD reduction, file sizes, timings, row counts
- Reference options by their CLI flag (`--tolerance-ppm`, not "the ppm setting")
- Note when a change alters written output, since corrected mzML files may already be in
  downstream pipelines

## Python release process

1. Finalize `RELEASE_NOTES_next.md` on the development branch
2. Rename it: `git mv release-notes/RELEASE_NOTES_next.md release-notes/RELEASE_NOTES_v{version}.md`
3. Update the title heading inside the file, and **delete every section heading with no
   entries under it**
4. Create a fresh empty `RELEASE_NOTES_next.md` for the following release
5. Update `version` in `pyproject.toml`. That is the only place it appears;
   `mars.__version__` and `mars --version` read it back out of the installed distribution
   metadata
6. Commit the version bump and renames
7. Merge to `main`
8. Tag: `git tag v{version}`
9. Push: `git push origin main --tags`, then publish a GitHub Release, which triggers the
   PyPI upload

## C# (.NET) release process

1. Finalize `RELEASE_NOTES_dotnet-next.md`; rename it to
   `RELEASE_NOTES_dotnet-v{version}.md`, update its heading, and **delete every section
   heading with no entries under it** (this file is published verbatim as the Release body);
   create a fresh empty `RELEASE_NOTES_dotnet-next.md`
2. Bump `<Version>` in `dotnet/Directory.Build.props` to `{version}`. That is the only place
   it appears; `mars --version` reads it back off the assembly
3. Commit and merge to `main`
4. Tag: `git tag dotnet-v{version}`
5. Push the tag: `git push origin dotnet-v{version}`

**Pushing the tag does the rest.** `.github/workflows/dotnet-release.yml` builds every
platform artifact and creates the GitHub Release. Do not hand-create the Release.

The workflow refuses to start building if the release is inconsistent, so the failure
arrives in seconds rather than after twenty minutes of artifact builds:

- `<Version>` in `dotnet/Directory.Build.props` must equal the version in the tag
- `release-notes/RELEASE_NOTES_dotnet-v{version}.md` must exist
- that file must have no section heading with nothing under it

Artifacts published for each release, all self-contained (no .NET install required):

| Runtime identifier | Archive | Built on |
|---|---|---|
| `win-x64` | `.zip` | Windows runner, smoke tested |
| `win-arm64` | `.zip` | cross-compiled, **not** smoke tested |
| `linux-x64` | `.tar.gz` | Linux runner, smoke tested |
| `linux-arm64` | `.tar.gz` | cross-compiled, **not** smoke tested |
| `osx-arm64` | `.tar.gz` | macOS arm64 runner, smoke tested |
| `osx-x64` | `.tar.gz` | macOS x64 runner, smoke tested |

Plus `SHA256SUMS.txt`. To rebuild artifacts without cutting a release, run the workflow
manually with `workflow_dispatch` and a version; it uploads them as workflow artifacts and
creates no Release.

> [!IMPORTANT]
> **A .NET release is only meaningful if the passthrough still holds.** Before tagging, run
> `mars verify` on at least one real file per instrument type in the release. It round-trips
> the file with a null correction and checks that the decoded arrays, index and checksum all
> survive. A regression there corrupts every file the release touches, and no unit test on
> synthetic data will catch a real-world formatting quirk. CI cannot do this for you - the
> reference files are too large to keep in the repository.

> [!NOTE]
> To fix an already-published Release:
> `gh release edit <tag> --notes-file release-notes/RELEASE_NOTES_<tag>.md`
