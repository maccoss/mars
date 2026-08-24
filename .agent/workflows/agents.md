---
description: How to run tests and linting for the mars project
---

# Mars Development Workflow

## Running Tests
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
pytest tests/ -v
```

## Running Linter (Ruff)
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
ruff check mars/
```

## Auto-fix Linter Issues
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
ruff check mars/ --fix
```

## Running Full CI Check
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
ruff check mars/ && pytest tests/ -v
```

## Running Calibration on Example Data
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
mars calibrate \
  --mzml "example-data/Ste-2024-12-02_HeLa_20msIIT_GPFDIA_*.mzML" \
  --prism-report example-data/Stellar-HeLa-GPF-PRISM.csv \
  --tolerance 0.3 \
  --min-intensity 500 \
  --output-dir example-data/output/
```

## Installing in Development Mode
// turbo
```bash
cd /home/maccoss/GitHub-Repo/maccoss/mars
pip install -e .
```