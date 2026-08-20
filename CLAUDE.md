# AGENTS.md

This file provides context and instructions for AI agents working on the Mars repository.

## Repository Overview

Mars (Mass Accuracy Recalibration System) is a tool for calibrating DIA mass spectrometry data from the Thermo Stellar instrument. It uses XGBoost to learn m/z corrections from spectral library matches.

## Continuous Integration (CI/CD)

The repository uses GitHub Actions for CI/CD, defined in `.github/workflows/`.

### Workflows

1.  **Tests (`tests.yml`)**
    *   **Triggers:** Push to `main`, Pull Requests to `main`.
    *   **Actions:**
        *   Sets up Python 3.10, 3.11, 3.12.
        *   Installs dependencies with `pip install -e ".[dev]"`.
        *   Runs tests using `pytest tests/ -v --tb=short`.
        *   Runs linting with `ruff check mars/`.

2.  **Publish to PyPI (`publish.yml`)**
    *   **Triggers:** Release published.
    *   **Actions:**
        *   Builds the package (`python -m build`).
        *   Publishes to PyPI using Trusted Publishing (OIDC).
        *   Requires the tag (e.g., `v0.1.0`) to match the release.

## Common Development Tasks

*   **Install for dev:** `pip install -e ".[dev]"`
*   **Run tests:** `pytest tests/`
*   **Lint:** `ruff check .`
*   **Build package:** `python -m build`

## Release Notes

There are two independent release tracks, Python and C# (.NET), each with its own rolling
draft. `release-notes/README.md` is the authoritative process; the short version:

*   **Append to the `-next` draft for the track you changed:** `RELEASE_NOTES_next.md` for
    Python, `RELEASE_NOTES_dotnet-next.md` for C#. A change affecting both gets an entry in
    both, written for that track's users.
*   **Never edit a released notes file.** Versioned files record what shipped.
*   **Be specific:** what was fixed and why, with numbers where they exist.
*   **Group related changes:** New Features, Bug Fixes, Performance, Breaking Changes.
*   **Flag anything that changes written output.** Corrected mzML files may already be in
    downstream pipelines.

## Documentation

*   `docs/` holds the algorithm description, library guidance, the mzML passthrough
    contract, and the port specification. Update these when behavior changes, not just the
    code comments.
*   `README.md` documents the C# implementation; `README-python.md` documents the Python
    one. Keep the split - the top-level README is what a new user reads first.

## The two implementations

The Python implementation is **frozen**: bug fixes only, no new features, per the port
specification. The C# implementation under `dotnet/` is where new work goes. Do not port a
Python quirk into C# without checking `docs/dotnet-port-spec.md` section 10a first - four
of them are known defects that C# deliberately does not reproduce.

## Style Guidelines

*   **No Emojis:** Do not use emojis in any output, documentation, source code comments, or Jupyter notebooks. Keep all text professional and plain.
