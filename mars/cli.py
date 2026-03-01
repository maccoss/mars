"""Command-line interface for Mars calibration tool.

Supports batch processing of multiple mzML files with wildcards/folders.
"""

from __future__ import annotations

import glob
import logging
import sys
from pathlib import Path

import click

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(name)s - %(levelname)s - %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("mars")


def setup_logging(verbose: bool = False):
    """Configure logging level."""
    level = logging.DEBUG if verbose else logging.INFO
    logging.getLogger("mars").setLevel(level)


def find_mzml_files(
    mzml: tuple[str, ...] = (),
    mzml_dir: str | None = None,
    input_files: tuple[str, ...] = (),
) -> list[Path]:
    """Find mzML files from arguments, options, or directory.

    Args:
        mzml: File paths or glob patterns from --mzml option (can be repeated)
        mzml_dir: Directory containing mzML files
        input_files: Positional file paths (e.g., from shell-expanded wildcards)

    Returns:
        List of mzML file paths
    """
    files = []

    # Process --mzml options and positional arguments together
    all_patterns = list(mzml) + list(input_files)
    for pattern in all_patterns:
        if "*" in pattern or "?" in pattern:
            matches = glob.glob(pattern, recursive=True)
            files.extend(Path(m) for m in matches if m.endswith(".mzML"))
        else:
            path = Path(pattern)
            if path.exists():
                files.append(path)
            else:
                logger.error(f"File not found: {pattern}")

    if mzml_dir:
        dir_path = Path(mzml_dir)
        if dir_path.is_dir():
            files.extend(dir_path.glob("*.mzML"))
            files.extend(dir_path.glob("*.mzml"))
        else:
            logger.error(f"Directory not found: {mzml_dir}")

    # Remove duplicates
    files = list(set(files))
    files.sort()

    return files


@click.group()
@click.version_option(version="0.1.5", prog_name="mars")
def main():
    """Mars: Mass Accuracy Recalibration System for Thermo Stellar DIA data."""
    pass


@main.command()
@click.option(
    "--mzml",
    "--mzML",
    "mzml",
    multiple=True,
    type=str,
    help="Path to mzML file(s) or glob pattern (repeatable, e.g., --mzml a.mzML --mzml b.mzML)",
)
@click.option(
    "--mzml-dir",
    type=click.Path(exists=True, file_okay=False, dir_okay=True),
    help="Directory containing mzML files",
)
@click.option(
    "--library",
    type=click.Path(exists=True, dir_okay=False),
    required=False,
    help="Path to spectral library: blib file or DIA-NN report-lib.parquet",
)
@click.option(
    "--output-dir",
    type=click.Path(file_okay=False),
    default=".",
    help="Output directory for calibrated files and QC plots",
)
@click.option(
    "--tolerance",
    type=float,
    default=0.7,
    show_default=True,
    help="m/z tolerance for fragment matching in Th (ignored if --tolerance-ppm is set)",
)
@click.option(
    "--tolerance-ppm",
    type=float,
    default=None,
    help="m/z tolerance for fragment matching in ppm (e.g., 10 for Astral data). Overrides --tolerance.",
)
@click.option(
    "--min-intensity",
    type=float,
    default=500.0,
    show_default=True,
    help="Minimum observed peak intensity to use for matching",
)
@click.option(
    "--max-isolation-window",
    type=float,
    default=None,
    help="Maximum isolation window width (m/z) to process. Skips wider windows (e.g., 5.0 to ignore 20-30 m/z wide bins)",
)
@click.option(
    "--temperature-dir",
    type=click.Path(exists=True, file_okay=False, dir_okay=True),
    default=None,
    help="Directory containing RF temperature CSV files (RFA2-*.csv, RFC2-*.csv)",
)
@click.option(
    "--prism-csv",
    type=click.Path(exists=True, dir_okay=False),
    help="PRISM Skyline report CSV with Start Time/End Time for RT ranges",
)
@click.option(
    "--diann-report",
    type=click.Path(exists=True, dir_okay=False),
    help="DIA-NN report.parquet file (auto-detected if in same folder as library)",
)
@click.option(
    "--rt-window",
    type=float,
    default=2.0,
    show_default=True,
    help="RT window around library RT for matching (minutes, used if no PRISM CSV)",
)
@click.option(
    "--model-path",
    type=click.Path(dir_okay=False),
    help="Path to save/load calibration model (default: output-dir/mars_model.pkl)",
)
@click.option(
    "--no-recalibrate",
    is_flag=True,
    help="Only train model and generate QC, don't write calibrated mzML",
)
@click.option(
    "-v",
    "--verbose",
    is_flag=True,
    help="Verbose output",
)
@click.argument("input_files", nargs=-1, type=click.Path())
def calibrate(
    mzml: tuple[str, ...],
    mzml_dir: str | None,
    library: str,
    output_dir: str,
    tolerance: float,
    tolerance_ppm: float | None,
    min_intensity: float,
    max_isolation_window: float | None,
    temperature_dir: str | None,
    prism_csv: str | None,
    diann_report: str | None,
    rt_window: float,
    model_path: str | None,
    no_recalibrate: bool,
    verbose: bool,
    input_files: tuple[str, ...] = (),
):
    """Learn and apply m/z calibration from spectral library matches.

    Processes one or more mzML files, matching fragments to a spectral library
    to learn calibration corrections. Outputs recalibrated mzML files named
    {input}-mars.mzML.

    mzML files can also be passed as positional arguments (wildcards expand
    naturally without quotes).

    Examples:

        # Single file
        mars calibrate --mzml data.mzML --library lib.blib --output-dir output/

        # Multiple files with wildcard (no quotes needed)
        mars calibrate --mzml *.mzML --library lib.blib --output-dir output/

        # Positional arguments (also works)
        mars calibrate *.mzML --library lib.blib --output-dir output/

        # All files in directory
        mars calibrate --mzml-dir /data/raw/ --library lib.blib --output-dir output/
    """
    setup_logging(verbose)

    from mars.calibration import MzCalibrator
    from mars.library import load_blib
    from mars.matching import (
        apply_calibration_to_matches,
        match_library_to_spectra,
    )
    from mars.mzml import get_output_path, read_dia_spectra, write_calibrated_mzml
    from mars.visualization import generate_qc_report

    # Find input files
    mzml_files = find_mzml_files(mzml, mzml_dir, input_files)
    if not mzml_files:
        logger.error("No mzML files found. Use --mzml, --mzml-dir, or pass files as arguments.")
        sys.exit(1)

    logger.info(f"Found {len(mzml_files)} mzML files to process")

    # Setup output
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    if model_path is None:
        model_path = output_path / "mars_model.pkl"
    else:
        model_path = Path(model_path)

    # Load library - priority: PRISM CSV > DIA-NN parquet > blib
    file_filters = [f.stem for f in mzml_files]

    if prism_csv:
        from mars.library import load_prism_library

        logger.info(f"Loading library from PRISM CSV: {prism_csv}")
        library_entries = load_prism_library(prism_csv, mzml_filename=file_filters)
        logger.info(f"Loaded {len(library_entries)} library entries with theoretical m/z")
    elif library and library.endswith(".parquet"):
        # DIA-NN parquet library
        from mars.library import load_diann_library

        logger.info(f"Loading DIA-NN library from: {library}")
        library_entries = load_diann_library(
            library,
            report_parquet=diann_report,
            mzml_filename=file_filters,
        )
        logger.info(f"Loaded {len(library_entries)} library entries from DIA-NN parquet")
    elif library:
        # blib file
        from mars.library import load_blib

        logger.info(f"Loading spectral library: {library}")
        library_entries = load_blib(library, rt_window=rt_window)
        logger.info(f"Loaded {len(library_entries)} library entries from blib")
    else:
        logger.error("A library is required: --library (blib or parquet) or --prism-csv")
        sys.exit(1)

    # Load temperature data if provided
    temperature_data_by_file = {}
    if temperature_dir:
        from mars.temperature import find_temperature_files

        temp_dir_path = Path(temperature_dir)
        for mzml_file in mzml_files:
            temp_data = find_temperature_files(mzml_file, temp_dir_path)
            if temp_data:
                temperature_data_by_file[mzml_file.name] = temp_data

    # Collect matches from all files
    all_matches = []

    for mzml_file in mzml_files:
        logger.info(f"Processing: {mzml_file.name}")

        # Get temperature data for this file
        temperature_data = temperature_data_by_file.get(mzml_file.name)

        # Read spectra and match
        spectra = read_dia_spectra(mzml_file)
        matches = match_library_to_spectra(
            library_entries,
            spectra,
            mz_tolerance=tolerance,
            min_intensity=min_intensity,
            max_isolation_window_width=max_isolation_window,
            temperature_data=temperature_data,
            tolerance_ppm=tolerance_ppm,
        )

        if len(matches) > 0:
            matches["source_file"] = mzml_file.name
            all_matches.append(matches)
        else:
            logger.warning(f"No matches found in {mzml_file.name}")

    if not all_matches:
        logger.error("No fragment matches found in any file. Check library compatibility.")
        sys.exit(1)

    # Combine all matches
    import pandas as pd

    combined_matches = pd.concat(all_matches, ignore_index=True)
    logger.info(f"Total matches: {len(combined_matches):,}")

    # Normalize absolute_time across all files (subtract global minimum so first run starts at ~0)
    if (
        "absolute_time" in combined_matches.columns
        and combined_matches["absolute_time"].notna().any()
    ):
        min_absolute_time = combined_matches["absolute_time"].min()
        combined_matches["absolute_time"] = combined_matches["absolute_time"] - min_absolute_time
        max_time = combined_matches["absolute_time"].max()
        logger.info(
            f"Absolute time range: 0 to {max_time:.1f} seconds ({max_time / 60:.1f} minutes)"
        )

    # Train model
    logger.info("Training calibration model...")
    calibrator = MzCalibrator()
    calibrator.fit(combined_matches)

    # Save model
    calibrator.save(model_path)

    # Apply calibration to matches for QC
    matches_calibrated = apply_calibration_to_matches(combined_matches, calibrator)

    # Generate QC report
    logger.info("Generating QC report...")
    generate_qc_report(
        combined_matches,
        matches_calibrated,
        calibrator,
        output_path,
    )

    # Write calibrated mzML files
    if not no_recalibrate:
        calibration_func = calibrator.create_calibration_function()

        for mzml_file in mzml_files:
            output_file = get_output_path(mzml_file, output_path)
            logger.info(f"Writing: {output_file.name}")
            # Get temperature data for this file
            temp_data = temperature_data_by_file.get(mzml_file.name)
            write_calibrated_mzml(
                mzml_file,
                output_file,
                calibration_func,
                max_isolation_window_width=max_isolation_window,
                temperature_data=temp_data,
            )

    logger.info("Done!")
    logger.info(f"Output directory: {output_path}")


@main.command()
@click.option(
    "--mzml",
    "--mzML",
    "mzml",
    multiple=True,
    type=str,
    help="Path to mzML file(s) or glob pattern (repeatable)",
)
@click.option(
    "--mzml-dir",
    type=click.Path(exists=True, file_okay=False, dir_okay=True),
    help="Directory containing mzML files",
)
@click.option(
    "--library",
    type=click.Path(exists=True, dir_okay=False),
    required=True,
    help="Path to blib spectral library",
)
@click.option(
    "--output",
    type=click.Path(file_okay=False),
    default="qc_report",
    help="Output directory for QC report",
)
@click.option(
    "--tolerance",
    type=float,
    default=0.7,
    show_default=True,
    help="m/z tolerance for fragment matching in Th (ignored if --tolerance-ppm is set)",
)
@click.option(
    "--tolerance-ppm",
    type=float,
    default=None,
    help="m/z tolerance for fragment matching in ppm (e.g., 10 for Astral data). Overrides --tolerance.",
)
@click.option(
    "--max-isolation-window",
    type=float,
    default=None,
    help="Maximum isolation window width (m/z) to process. Skips wider windows in output",
)
@click.option(
    "-v",
    "--verbose",
    is_flag=True,
    help="Verbose output",
)
@click.argument("input_files", nargs=-1, type=click.Path())
def qc(
    mzml: tuple[str, ...],
    mzml_dir: str | None,
    library: str,
    output: str,
    tolerance: float,
    tolerance_ppm: float | None,
    max_isolation_window: float | None,
    verbose: bool,
    input_files: tuple[str, ...] = (),
):
    """Generate QC report without recalibration.

    Matches fragments and generates histogram/heatmap plots showing
    the current mass accuracy, but does not train a model or write
    recalibrated mzML files.

    mzML files can also be passed as positional arguments.
    """
    setup_logging(verbose)

    import matplotlib.pyplot as plt

    from mars.library import load_blib
    from mars.matching import match_library_to_spectra
    from mars.mzml import read_dia_spectra
    from mars.visualization import plot_delta_mz_heatmap, plot_delta_mz_histogram

    # Find input files
    mzml_files = find_mzml_files(mzml, mzml_dir, input_files)
    if not mzml_files:
        logger.error("No mzML files found. Use --mzml, --mzml-dir, or pass files as arguments.")
        sys.exit(1)

    output_path = Path(output)
    output_path.mkdir(parents=True, exist_ok=True)

    # Load library
    library_entries = load_blib(library)

    # Collect matches
    all_matches = []
    for mzml_file in mzml_files:
        logger.info(f"Processing: {mzml_file.name}")
        spectra = read_dia_spectra(mzml_file)
        matches = match_library_to_spectra(
            library_entries,
            spectra,
            mz_tolerance=tolerance,
            max_isolation_window_width=max_isolation_window,
            tolerance_ppm=tolerance_ppm,
        )
        if len(matches) > 0:
            all_matches.append(matches)

    if not all_matches:
        logger.error("No matches found")
        sys.exit(1)

    import pandas as pd

    combined = pd.concat(all_matches, ignore_index=True)

    # Generate plots (before only, no calibration)
    plot_delta_mz_histogram(combined, output_path=output_path / "histogram.png")
    plt.close()

    plot_delta_mz_heatmap(combined, output_path=output_path / "heatmap.png")
    plt.close()

    # Write summary
    summary_path = output_path / "summary.txt"
    with open(summary_path, "w") as f:
        f.write("Mars QC Report (Pre-calibration)\n")
        f.write("=" * 40 + "\n\n")
        f.write(f"Files processed: {len(mzml_files)}\n")
        f.write(f"Total matches: {len(combined):,}\n\n")
        f.write("Delta m/z statistics:\n")
        f.write(f"  Mean:   {combined['delta_mz'].mean():.4f} Da\n")
        f.write(f"  Std:    {combined['delta_mz'].std():.4f} Da\n")
        f.write(f"  Median: {combined['delta_mz'].median():.4f} Da\n")

    logger.info(f"QC report saved to {output_path}")


@main.command()
@click.option(
    "--mzml",
    "--mzML",
    "mzml",
    multiple=True,
    type=str,
    help="Path to mzML file(s) or glob pattern (repeatable)",
)
@click.option(
    "--mzml-dir",
    type=click.Path(exists=True, file_okay=False, dir_okay=True),
    help="Directory containing mzML files",
)
@click.option(
    "--model",
    type=click.Path(exists=True, dir_okay=False),
    required=True,
    help="Path to trained calibration model (.pkl)",
)
@click.option(
    "--output-dir",
    type=click.Path(file_okay=False),
    default=".",
    help="Output directory for calibrated files",
)
@click.option(
    "--max-isolation-window",
    type=float,
    default=None,
    help="Maximum isolation window width (m/z) to process. Skips wider windows in output",
)
@click.option(
    "-v",
    "--verbose",
    is_flag=True,
    help="Verbose output",
)
@click.argument("input_files", nargs=-1, type=click.Path())
def apply(
    mzml: tuple[str, ...],
    mzml_dir: str | None,
    model: str,
    output_dir: str,
    max_isolation_window: float | None,
    verbose: bool,
    input_files: tuple[str, ...] = (),
):
    """Apply pre-trained calibration model to mzML files.

    Use this when you have already trained a model and want to apply
    it to new data without retraining.

    mzML files can also be passed as positional arguments.
    """
    setup_logging(verbose)

    from mars.calibration import MzCalibrator
    from mars.mzml import get_output_path, write_calibrated_mzml

    # Find input files
    mzml_files = find_mzml_files(mzml, mzml_dir, input_files)
    if not mzml_files:
        logger.error("No mzML files found. Use --mzml, --mzml-dir, or pass files as arguments.")
        sys.exit(1)

    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)

    # Load model
    calibrator = MzCalibrator.load(model)
    calibration_func = calibrator.create_calibration_function()

    # Apply to each file
    for mzml_file in mzml_files:
        output_file = get_output_path(mzml_file, output_path)
        logger.info(f"Calibrating: {mzml_file.name} -> {output_file.name}")
        write_calibrated_mzml(
            mzml_file,
            output_file,
            calibration_func,
            max_isolation_window_width=max_isolation_window,
        )

    logger.info("Done!")


if __name__ == "__main__":
    main()
