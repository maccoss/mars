# Mars v0.1.5 Release Notes

**Release Date:** TBD

## Overview

This release includes bug fixes and improvements.

## New Features

(No new features yet)

## Bug Fixes

(No bug fixes yet)

## Changes

- Changed default `--tolerance` from 0.7 Th to 0.3 Th. The wider tolerance caused many isotope peaks from doubly charged fragments to be included in matching.

## Compatibility

- Fully backward compatible with v0.1.4
- Supported spectral library formats:
  - blib (BiblioSpec)
  - PRISM CSV
  - DIA-NN parquet (`report-lib.parquet` + `report.parquet`)
- Output mzML files are compatible with:
  - DIA-NN
  - SeeMS (ProteoWizard)
  - MSConvert
  - Skyline
  - Other standard mzML readers

## Upgrade Notes

```bash
pip install --upgrade mars-ms
```
