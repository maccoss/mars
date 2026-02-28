# MARS: Mass Accuracy Recalibration System

[![PyPI version](https://img.shields.io/pypi/v/mars-ms.svg)](https://pypi.org/project/mars-ms/)
[![Python versions](https://img.shields.io/pypi/pyversions/mars-ms.svg)](https://pypi.org/project/mars-ms/)
[![License](https://img.shields.io/pypi/l/mars-ms.svg)](https://github.com/maccoss/mars/blob/main/LICENSE)

Mass recalibration tool for DIA mass spectrometry data from the ThermoFisher Stellar.

## Overview

Mars learns m/z calibration corrections from spectral library fragment matches. The XGBoost model accounts for:

- **Fragment m/z**: Mass-dependent calibration bias
- **Peak intensity**: Higher intensity peaks provide more reliable calibration
- **Absolute time**: Calibration drift over the acquisition run
- **Spectrum TIC**: Space charge effects from high ion current
- **Ion injection time**: Signal accumulation duration effects
- **Precursor m/z**: DIA isolation window-specific effects
- **RF temperatures**: Thermal effects from RF amplifier (RFA2) and electronics (RFC2)

## How It Works

1. **Fragment matching**: For each DIA MS2 spectrum, Mars finds library peptides where:
   - The precursor m/z falls within the DIA isolation window
   - The spectrum RT is within the peptide's elution window

2. **Peak selection**: For each expected fragment, Mars selects the **most intense** peak within the m/z tolerance (not the closest), filtering for minimum intensity

3. **Model training**: Each matched fragment becomes a training point with up to 16 features (see [Model Features](#model-features)) and target: `delta_mz`

4. **Calibration**: The trained model predicts m/z corrections for all peaks in the mzML

## Installation

### From PyPI (recommended)

```bash
pip install mars-ms
```

### From source

```bash
git clone https://github.com/maccoss/mars.git
cd mars
pip install -e .
```

**Requirements**: Python 3.10+, pyteomics, xgboost, numpy, pandas, matplotlib, seaborn, click

## Usage

### With PRISM CSV (Recommended)

Use a CSV file created using this [Skyline report](Skyline-PRISM-Report/Skyline-PRISM.skyr) for accurate RT windows:

```bash
mars calibrate \
  --mzML data.mzML \
  --prism-csv prism_report.csv \
  --tolerance 0.3 \
  --max-isolation-window 5.0 \
  --output-dir output/
```

> **Note:** Both `--mzml` and `--mzML` are accepted.

### With DIA-NN Parquet Output

Use DIA-NN parquet files directly as a spectral library:

```bash
mars calibrate \
  --mzml data.mzML \
  --library report-lib.parquet \
  --output-dir output/
```

Mars automatically looks for `report.parquet` in the same directory to get RT windows. If the report file is in a different location:

```bash
mars calibrate \
  --mzml data.mzML \
  --library report-lib.parquet \
  --diann-report /path/to/report.parquet \
  --output-dir output/
```

### Basic Usage (blib)

```bash
mars calibrate --mzml data.mzML --library library.blib --output-dir output/
```

### Batch Processing

```bash
# Multiple files with wildcard (no quotes needed)
mars calibrate --mzml *.mzML --library library.blib --output-dir output/

# Positional arguments also work (no --mzml flag needed)
mars calibrate *.mzML --library library.blib --output-dir output/

# Specify files individually
mars calibrate --mzml a.mzML --mzml b.mzML --library library.blib --output-dir output/

# All files in directory
mars calibrate --mzml-dir /path/to/data/ --library library.blib --output-dir output/
```

### Applying a Pre-Trained Model

If you've already trained a calibration model and want to apply it to new files without retraining:

```bash
# Apply existing model to new mzML files
mars apply --mzml new_data.mzML --model mars_model.pkl --output-dir output/

# Apply to multiple files (no quotes needed)
mars apply --mzml *.mzML --model mars_model.pkl --output-dir output/

# Or as positional arguments
mars apply *.mzML --model mars_model.pkl --output-dir output/

# Apply to all files in a directory
mars apply --mzml-dir /path/to/data/ --model mars_model.pkl --output-dir output/
```

This is useful when:

- You want to calibrate files from the same instrument/method without retraining
- You trained on a subset of files and want to apply to the rest
- You're reprocessing data with a validated model

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--mzml` / `--mzML` | - | Path to mzML file(s) or glob pattern (repeatable) |
| `--mzml-dir` | - | Directory containing mzML files |
| `--library` | - | Path to spectral library: blib file or DIA-NN `report-lib.parquet` |
| `--prism-csv` | - | PRISM Skyline CSV with Start/End Time columns |
| `--diann-report` | - | Path to DIA-NN `report.parquet` (auto-detected if in same dir as library) |
| `--tolerance` | 0.7 | m/z tolerance for matching (Th), ignored if `--tolerance-ppm` is set |
| `--tolerance-ppm` | - | m/z tolerance for matching in ppm (e.g., 10 for Astral), overrides `--tolerance` |
| `--min-intensity` | 500 | Minimum peak intensity for matching |
| `--max-isolation-window` | - | Maximum isolation window width (m/z) to include |
| `--temperature-dir` | - | Directory with RF temperature CSV files |
| `--output-dir` | `.` | Output directory |
| `--model-path` | - | Path to save/load calibration model |
| `--no-recalibrate` | - | Only train model, don't write mzML |

## RT Window Behavior

- **With `--prism-csv`**: Uses exact `Start Time` and `End Time` from Skyline
- **With DIA-NN parquet**: Uses `RT.Start` and `RT.Stop` from `report.parquet`
- **With blib only**: Uses +/-5 seconds around the blib library RT

## Isolation Window Filtering

Some DIA methods use wide isolation windows (e.g., 20-30 m/z) that may reduce calibration accuracy. Use `--max-isolation-window` to exclude these:

```bash
# Exclude windows wider than 5 m/z
mars calibrate --mzml data.mzML --prism-csv report.csv --max-isolation-window 5.0
```

This filters spectra during both model training and mzML recalibration. Typical narrow DIA windows (~1 m/z) are retained.

## Output Files

| File | Description |
|------|-------------|
| `{input}-mars.mzML` | Recalibrated mzML file |
| `mars_model.pkl` | Trained XGBoost calibration model |
| `mars_qc_histogram.png` | Delta m/z distribution (before/after) |
| `mars_qc_heatmap.png` | 2D heatmap (RT × m/z, color = delta) |
| `mars_qc_intensity_vs_error.png` | Intensity vs mass error hexbin |
| `mars_qc_rt_vs_error.png` | RT vs mass error hexbin |
| `mars_qc_mz_vs_error.png` | Fragment m/z vs mass error hexbin |
| `mars_qc_tic_vs_error.png` | TIC vs mass error hexbin |
| `mars_qc_injection_time_vs_error.png` | Injection time vs mass error hexbin |
| `mars_qc_tic_injection_time_vs_error.png` | TIC×injection time vs mass error hexbin |
| `mars_qc_fragment_ions_vs_error.png` | Fragment ions vs mass error hexbin |
| `mars_qc_rfa2_temperature_vs_error.png` | RFA2 temperature vs error (if available) |
| `mars_qc_rfc2_temperature_vs_error.png` | RFC2 temperature vs error (if available) |
| `mars_qc_feature_importance.png` | Model feature importance |
| `mars_qc_summary.txt` | Calibration statistics |


## Model Features

The XGBoost model uses up to 16 features to predict m/z corrections:

1. `precursor_mz` - DIA isolation window center
2. `fragment_mz` - Fragment m/z being calibrated  
3. `absolute_time` - Time relative to first acquisition (seconds)
4. `log_tic` - Log10 of spectrum total ion current
5. `log_intensity` - Log10 of peak intensity
6. `injection_time` - Ion injection time (seconds)
7. `tic_injection_time` - TIC × injection time product
8. `fragment_ions` - Fragment intensity × injection time (total ions, not rate)
9. `ions_above_0_1` - Total ions in (X+0.5, X+1.5] Th range above fragment m/z
10. `ions_above_1_2` - Total ions in (X+1.5, X+2.5] Th range above fragment m/z
11. `ions_above_2_3` - Total ions in (X+2.5, X+3.5] Th range above fragment m/z
12. `ions_below_0_1` - Total ions in (X-1.5, X-0.5] Th range below fragment m/z
13. `ions_below_1_2` - Total ions in (X-2.5, X-1.5] Th range below fragment m/z
14. `ions_below_2_3` - Total ions in (X-3.5, X-2.5] Th range below fragment m/z
15. `adjacent_ratio_0_1` - ions_above_0_1 / fragment_ions (relative adjacent density)
16. `adjacent_ratio_1_2` - ions_above_1_2 / fragment_ions
17. `adjacent_ratio_2_3` - ions_above_2_3 / fragment_ions
18. `adjacent_ratio_below_0_1` - ions_below_0_1 / fragment_ions
19. `adjacent_ratio_below_1_2` - ions_below_1_2 / fragment_ions
20. `adjacent_ratio_below_2_3` - ions_below_2_3 / fragment_ions
21. `rfa2_temp` - RF amplifier temperature (°C)
22. `rfc2_temp` - RF electronics temperature (°C)

**Note**: Features 6-20 are only included if injection time data is available in the mzML files. Features 21-22 are only included if temperature CSV files are provided. Features with universally missing data are automatically excluded.

## RF Temperature Data

Mars can incorporate RF temperature data to model thermal effects on mass accuracy. Temperature data is loaded from CSV files exported from Thermo chromatogram exports.

### Temperature File Format

Temperature CSV files should be in Thermo's chromatogram export format:
- 3 header lines (skipped)
- Columns: `Time(min)`, temperature value

Example naming convention:
```
RFA2-Sample_Name.csv  # RF amplifier temperature
RFC2-Sample_Name.csv  # RF electronics temperature  
```

### Usage with Temperature Data

```bash
mars calibrate \
  --mzml data.mzML \
  --prism-csv report.csv \
  --temperature-dir /path/to/temperature_csvs/ \
  --output-dir output/
```

Mars automatically finds temperature files matching each mzML filename and interpolates temperature values at each spectrum's retention time.

## Python API

```python
from mars import load_blib, read_dia_spectra, match_library_to_spectra, MzCalibrator

# Load library and match
library = load_blib("library.blib")
spectra = read_dia_spectra("data.mzML")
matches = match_library_to_spectra(library, spectra, mz_tolerance=0.2, min_intensity=1500)

# Train and save model
calibrator = MzCalibrator()
calibrator.fit(matches)
calibrator.save("model.pkl")
```

### Using DIA-NN Parquet

```python
from mars import load_diann_library, read_dia_spectra, match_library_to_spectra, MzCalibrator

# Load DIA-NN library (auto-finds report.parquet in same directory)
library = load_diann_library("report-lib.parquet")

# Or specify report.parquet explicitly
library = load_diann_library("report-lib.parquet", report_parquet="/path/to/report.parquet")

# Filter to specific mzML file(s)
library = load_diann_library("report-lib.parquet", mzml_filename=["sample1.mzML", "sample2.mzML"])

spectra = read_dia_spectra("data.mzML")
matches = match_library_to_spectra(library, spectra, mz_tolerance=0.2, min_intensity=1500)
```

## Requirements

- **Spectral library**: One of the following formats:
  - blib format from Skyline with fragment annotations
  - DIA-NN parquet output (`report-lib.parquet` + `report.parquet`)
- **mzML files**: DIA data from Thermo Stellar (or similar unit resolution instrument)
- **PRISM CSV** (optional): Skyline report with `Start Time`, `End Time`, `Replicate Name` columns

## License

MIT

