# Release Notes

This directory contains per-version release notes for MARS.

## Versioning Scheme

MARS uses a `YY.feature.patch` versioning convention, the same scheme as
[Skyline-PRISM](https://github.com/maccoss/skyline-prism):

- **YY**: two-digit year (e.g. `26` for 2026)
- **feature**: incremented for each release containing new features
- **patch**: incremented for bug-fix-only releases within the same feature version

Examples: `26.1.0` (first feature release of 2026), `26.1.1` (patch), `26.2.0` (second
feature release).

The version lives in exactly one place, `dotnet/Directory.Build.props` (`<Version>`), and
is updated only at release time, not during development. `mars --version` reads it back
out of the assembly, so there is nothing to keep in lockstep.

> **The `0.1.x` line was the Python package.** MARS is now the C# tool, and its first
> release is `26.1.0`. The jump is deliberate: it is a switch of versioning scheme, not a
> hundred-and-some feature releases. The Python package is frozen to bug fixes, is no
> longer published to PyPI, and will be archived once the C# implementation has been used
> in earnest. Its notes (`RELEASE_NOTES_v0.1.*.md`) stay here as history.

## File Format

Each release gets one file, `RELEASE_NOTES_v{version}.md`. During development the
unreleased draft lives in `RELEASE_NOTES_next.md` and is renamed at release time.

```text
release-notes/
  README.md                      # this file
  RELEASE_NOTES_next.md          # working draft for the next release
  RELEASE_NOTES_v26.1.0.md
  RELEASE_NOTES_v0.1.5.md        # Python package history
```

## Writing Release Notes

### During development

Maintain `RELEASE_NOTES_next.md` as a working draft for the next planned version. Append
entries as features and fixes land. The file stays unversioned until the release is
finalized so the target version can change: a planned patch release becomes a feature
release the moment new functionality lands.

### Content structure

```markdown
# MARS v{version} Release Notes

One-sentence summary of the release.

## New Features

- Grouped by area. What changed from the user's point of view, not how it was implemented.

## Bug Fixes

- What was wrong, what it affected, and what was fixed.

## Performance

- With context and numbers: "6.9 s for a 1.2 GB file", not "faster mzML handling".

## Breaking Changes

- Anything that requires the user to do something. Omit the section if there is nothing.
```

Sections can be omitted when empty. For a large release, subsections within a category are
fine; for a patch release a flat list is enough.

> [!IMPORTANT]
> **Delete the empty headings when you rename the draft.** `RELEASE_NOTES_next.md` is
> seeded with all four headings so entries have somewhere to go during development, which
> means a renamed draft *always* arrives carrying the ones nobody filled in. Removing them
> is a step of the release, not something the draft gets right on its own. It matters here
> because this file is published verbatim as the GitHub Release description, where empty
> headings are visible to everyone reading the Releases page.

### Style

- Past tense: "Added", "Fixed", "Removed".
- Lead with user impact.
- Include specific numbers wherever they exist.
- Reference options by their CLI flag.
- **Flag anything that changes written output.** Corrected mzML files may already be in
  downstream pipelines, and a change in what MARS writes is the one thing a reader cannot
  afford to miss.

## Release Process

1. Finalize `RELEASE_NOTES_next.md` on the development branch.
2. Rename it:
   `git mv release-notes/RELEASE_NOTES_next.md release-notes/RELEASE_NOTES_v{version}.md`
3. Update the title heading inside the file to match the version, and **delete every
   section heading with no entries under it**.
4. Create a fresh `RELEASE_NOTES_next.md` seeded with the four headings.
5. Bump `<Version>` in `dotnet/Directory.Build.props` to `{version}`.
6. Commit and merge to `main`.
7. Tag: `git tag v{version}`
8. Push the tag: `git push origin v{version}`

**Pushing the tag both builds the artifacts and creates the GitHub Release**, via
`.github/workflows/dotnet-release.yml`. Do not hand-create the Release.

The workflow runs a preflight before building anything, so an inconsistent release fails
in seconds rather than after twenty minutes of artifacts:

- the version in `Directory.Build.props` must equal the tag,
- `release-notes/RELEASE_NOTES_v{version}.md` must exist,
- and it must have no section heading with nothing under it.

Step 2's rename therefore has to happen **before** tagging; the workflow resolves the path
from the tag.

To fix the notes on a Release that already exists:

```bash
gh release edit v{version} --notes-file release-notes/RELEASE_NOTES_v{version}.md
```

### Building artifacts without releasing

`dotnet-release.yml` also accepts a manual `workflow_dispatch` with a version, which
builds all six platform artifacts and creates no Release. Useful for checking that
packaging works before committing to a tag.
