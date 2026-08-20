# MARS vNEXT Release Notes

One-sentence summary of the release.

## New Features

## Bug Fixes

- Collapsed the package version to a single source. `mars/__init__.py` declared `0.1.4`
  while `pyproject.toml` and the CLI both said `0.1.5`, so `mars.__version__` reported a
  version that had never shipped. Both now read the installed distribution metadata.

## Performance

## Breaking Changes
