"""Fragment matching between spectral library and DIA spectra.

Matches expected fragment m/z values from the library to observed peaks
in DIA MS2 spectra, calculating delta m/z for calibration training.
"""

from __future__ import annotations

import logging
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd
from tqdm import tqdm

from mars.library import LibraryEntry
from mars.mzml import DIASpectrum

logger = logging.getLogger(__name__)


@dataclass
class FragmentMatch:
    """A matched fragment with all calibration features."""

    # Library values (ground truth)
    expected_mz: float
    library_intensity: float  # For weighting

    # Measured values
    observed_mz: float
    observed_intensity: float
    delta_mz: float  # observed - expected

    # Features for calibration model
    precursor_mz: float  # Center of DIA isolation window
    fragment_mz: float  # Same as expected_mz (for convenience)
    fragment_charge: int
    absolute_time: float | None  # Absolute time in seconds (normalized to earliest file)
    log_tic: float  # Log10 of spectrum total ion current
    log_intensity: float  # Log10 of peak intensity
    injection_time: float | None  # Ion injection time in seconds (optional)
    tic_injection_time: (
        float | None
    )  # TIC * injection_time (optional, only if injection_time available)

    # Adjacent ion population features (intensity × injection_time for ions just above this m/z)
    ions_above_0_1: float | None  # Total ions in (X, X+1] Th
    ions_above_1_2: float | None  # Total ions in (X+1, X+2] Th
    ions_above_2_3: float | None  # Total ions in (X+2, X+3] Th

    # Metadata
    peptide_sequence: str
    ion_annotation: str  # e.g., "y7+1"
    scan_number: int


def find_most_intense_peak(
    target_mz: float,
    mz_array: np.ndarray,
    intensity_array: np.ndarray,
    tolerance: float,
    min_intensity: float = 0.0,
    tolerance_ppm: float | None = None,
) -> tuple[float, float] | None:
    """Find the most intense peak within tolerance of target m/z.

    Args:
        target_mz: Expected m/z value
        mz_array: Array of observed m/z values (sorted)
        intensity_array: Array of observed intensities
        tolerance: Maximum allowed delta m/z in Th (ignored if tolerance_ppm is set)
        min_intensity: Minimum intensity threshold
        tolerance_ppm: Maximum allowed delta m/z in ppm (overrides tolerance if set)

    Returns:
        Tuple of (observed_mz, intensity) or None if no match
    """
    if len(mz_array) == 0:
        return None

    # Calculate effective tolerance in Th
    if tolerance_ppm is not None:
        effective_tolerance = target_mz * tolerance_ppm / 1e6
    else:
        effective_tolerance = tolerance

    # Find range of indices within tolerance using binary search
    low_mz = target_mz - effective_tolerance
    high_mz = target_mz + effective_tolerance

    low_idx = np.searchsorted(mz_array, low_mz, side="left")
    high_idx = np.searchsorted(mz_array, high_mz, side="right")

    if low_idx >= high_idx:
        return None

    # Get peaks within tolerance
    candidate_intensities = intensity_array[low_idx:high_idx]
    candidate_mz = mz_array[low_idx:high_idx]

    # Apply minimum intensity filter
    if min_intensity > 0:
        mask = candidate_intensities >= min_intensity
        if not np.any(mask):
            return None
        candidate_intensities = candidate_intensities[mask]
        candidate_mz = candidate_mz[mask]

    if len(candidate_intensities) == 0:
        return None

    # Return the most intense peak
    best_idx = np.argmax(candidate_intensities)
    return float(candidate_mz[best_idx]), float(candidate_intensities[best_idx])


def sum_intensity_in_range(
    mz_array: np.ndarray,
    intensity_array: np.ndarray,
    low_mz: float,
    high_mz: float,
) -> float:
    """Sum intensities in an m/z range (exclusive low, inclusive high).

    Uses binary search for efficient range finding on sorted m/z arrays.

    Args:
        mz_array: Array of observed m/z values (must be sorted ascending)
        intensity_array: Array of observed intensities
        low_mz: Lower bound (exclusive) - peaks at exactly low_mz are NOT included
        high_mz: Upper bound (inclusive) - peaks at exactly high_mz ARE included

    Returns:
        Sum of intensities for peaks in the range (low_mz, high_mz]
    """
    if len(mz_array) == 0:
        return 0.0

    # Find indices: low exclusive (use 'right' to exclude low_mz),
    # high inclusive (use 'right' to include high_mz)
    low_idx = np.searchsorted(mz_array, low_mz, side="right")
    high_idx = np.searchsorted(mz_array, high_mz, side="right")

    if low_idx >= high_idx:
        return 0.0

    return float(np.sum(intensity_array[low_idx:high_idx]))


def load_rt_ranges_from_prism(
    prism_csv: Path | str,
    file_name_filter: str | None = None,
) -> dict[str, tuple[float, float]]:
    """Load RT start/end ranges from PRISM Skyline report CSV.

    Args:
        prism_csv: Path to PRISM CSV file with Start Time and End Time columns
        file_name_filter: Optional filter to match specific File Name or Replicate Name.
                         Matches against Replicate Name, File Name (without .raw),
                         or checks if the filter contains the Replicate Name.

    Returns:
        Dict mapping peptide key (modified_sequence + charge) to (rt_start, rt_end)
    """
    logger.info(f"Loading RT ranges from PRISM CSV: {prism_csv}")

    df = pd.read_csv(prism_csv)
    original_len = len(df)

    # Filter by file name if specified
    if file_name_filter:
        # Strip common suffixes from filter for flexible matching
        filter_clean = file_name_filter
        for suffix in ["_uncalibrated", "_calibrated", "-mars", ".mzML", ".mzml"]:
            filter_clean = filter_clean.replace(suffix, "")

        matched = False

        # Try Replicate Name column first (most reliable - unique portion of filename)
        if "Replicate Name" in df.columns:
            # Check if any Replicate Name is contained in the mzML filename
            mask = df["Replicate Name"].apply(
                lambda x: str(x) in file_name_filter or str(x) in filter_clean
            )
            if mask.any():
                df = df[mask]
                matched = True
                logger.info(
                    f"Matched {len(df)} rows via Replicate Name "
                    f"('{df['Replicate Name'].iloc[0]}' in '{file_name_filter}')"
                )

        # Fall back to File Name column
        if not matched and "File Name" in df.columns:
            # Extract base names without extensions
            df_filenames = df["File Name"].str.replace(".raw", "", regex=False)

            # Check for partial match (filter contains filename or vice versa)
            mask = df_filenames.apply(lambda x: filter_clean in str(x) or str(x) in filter_clean)
            if mask.any():
                df = df[mask]
                matched = True
                logger.info(f"Matched {len(df)} rows via File Name partial match")

        if not matched:
            logger.warning(
                f"No matches found for '{file_name_filter}' in PRISM CSV. "
                f"Using all {original_len} rows."
            )
            df = pd.read_csv(prism_csv)  # Reset to full data
        else:
            logger.info(f"Filtered from {original_len} to {len(df)} rows")

    # Required columns
    required = [
        "Peptide Modified Sequence Unimod Ids",
        "Precursor Charge",
        "Start Time",
        "End Time",
    ]
    missing = [c for c in required if c not in df.columns]
    if missing:
        raise ValueError(f"Missing required columns in PRISM CSV: {missing}")

    rt_ranges: dict[str, tuple[float, float]] = {}

    for _, row in df.iterrows():
        seq = row["Peptide Modified Sequence Unimod Ids"]
        charge = int(row["Precursor Charge"])
        start = row["Start Time"]
        end = row["End Time"]

        # Skip rows with missing RT values
        if pd.isna(start) or pd.isna(end):
            continue

        key = f"{seq}_{charge}"

        # If multiple entries, take the widest range
        if key in rt_ranges:
            existing_start, existing_end = rt_ranges[key]
            start = min(start, existing_start)
            end = max(end, existing_end)

        rt_ranges[key] = (float(start), float(end))

    logger.info(f"Loaded RT ranges for {len(rt_ranges)} peptides")
    return rt_ranges


def update_library_rt_ranges(
    library: list[LibraryEntry],
    rt_ranges: dict[str, tuple[float, float]],
) -> int:
    """Update library entries with RT ranges from PRISM CSV.

    Args:
        library: List of LibraryEntry objects
        rt_ranges: Dict from load_rt_ranges_from_prism

    Returns:
        Number of entries updated
    """
    n_updated = 0
    for entry in library:
        key = entry.make_key()
        if key in rt_ranges:
            entry.rt_start, entry.rt_end = rt_ranges[key]
            n_updated += 1

    logger.info(f"Updated RT ranges for {n_updated}/{len(library)} library entries")
    return n_updated


def match_library_to_spectra(
    library: list[LibraryEntry],
    spectra: Iterator[DIASpectrum],
    mz_tolerance: float = 0.3,
    min_intensity: float = 500.0,
    min_rt: float | None = None,
    max_rt: float | None = None,
    max_isolation_window_width: float | None = None,
    temperature_data: dict | None = None,
    show_progress: bool = True,
    tolerance_ppm: float | None = None,
) -> pd.DataFrame:
    """Match library fragments to measured spectra within RT and precursor windows.

    Algorithm:
    1. For each spectrum, find library peptides where:
       - RT is within library RT window (if available)
       - Precursor m/z window overlaps library precursor
    2. For each candidate peptide's fragments:
       - Find the MOST INTENSE peak within tolerance (not closest)
       - Filter by minimum intensity threshold
       - Record delta m/z and all calibration features

    Args:
        library: List of LibraryEntry objects with expected fragments
        spectra: Iterator of DIASpectrum objects from mzML
        mz_tolerance: Maximum delta m/z for matching in Th (default: ±0.3 Th)
                     Ignored if tolerance_ppm is specified.
        min_intensity: Minimum observed intensity to use (default: 500)
        min_rt: Minimum RT to process (minutes)
        max_rt: Maximum RT to process (minutes)
        max_isolation_window_width: Maximum isolation window width (m/z) to process.
                                    Spectra with wider windows are skipped (e.g., set to 5.0
                                    to ignore wide 20-30 m/z windows and only process narrow bins)
        temperature_data: Dict mapping source names (e.g., 'RFA2', 'RFC2') to TemperatureData objects
        show_progress: Show progress bar
        tolerance_ppm: Maximum delta m/z for matching in ppm (e.g., 10.0 for ±10 ppm).
                      If specified, overrides mz_tolerance (Th).

    Returns:
        DataFrame with one row per matched fragment
    """
    if tolerance_ppm is not None:
        logger.info(
            f"Matching library fragments with tolerance ±{tolerance_ppm} ppm, min intensity {min_intensity}"
        )
    else:
        logger.info(
            f"Matching library fragments with tolerance ±{mz_tolerance} Th, min intensity {min_intensity}"
        )

    # Build precursor index for efficient lookup
    # Group library entries by precursor m/z bins
    precursor_bins: dict[int, list[LibraryEntry]] = {}
    bin_width = 30  # Th bins for precursor lookup

    for entry in library:
        bin_idx = int(entry.precursor_mz / bin_width)
        if bin_idx not in precursor_bins:
            precursor_bins[bin_idx] = []
        precursor_bins[bin_idx].append(entry)

    logger.info(f"Indexed {len(library)} library entries into {len(precursor_bins)} precursor bins")

    matches: list[dict] = []
    n_spectra = 0
    n_matched_fragments = 0
    matched_peptide_keys: set[str] = set()  # Track unique peptides matched
    precursor_ranges_seen: set[tuple[int, int]] = set()  # Track DIA windows seen

    # Collect spectra into a list for processing
    spectra_list = list(spectra)

    # Process spectra
    spectra_iter = (
        tqdm(spectra_list, desc="Matching spectra", unit="spectra", delay=0.5)
        if show_progress
        else spectra_list
    )

    for spectrum in spectra_iter:
        n_spectra += 1

        # Isolation window width filter
        if max_isolation_window_width is not None:
            window_width = spectrum.precursor_mz_high - spectrum.precursor_mz_low
            if window_width > max_isolation_window_width:
                continue

        # RT filter
        if min_rt is not None and spectrum.rt < min_rt:
            continue
        if max_rt is not None and spectrum.rt > max_rt:
            continue

        # Find candidate library entries
        # Check bins that could overlap with isolation window
        low_bin = int(spectrum.precursor_mz_low / bin_width)
        high_bin = int(spectrum.precursor_mz_high / bin_width)

        # Track which DIA window we're in
        precursor_ranges_seen.add((int(spectrum.precursor_mz_low), int(spectrum.precursor_mz_high)))

        candidates: list[LibraryEntry] = []
        for bin_idx in range(low_bin, high_bin + 1):
            if bin_idx in precursor_bins:
                for entry in precursor_bins[bin_idx]:
                    # Check if precursor falls within isolation window
                    if (
                        spectrum.precursor_mz_low
                        <= entry.precursor_mz
                        <= spectrum.precursor_mz_high
                    ):
                        # Check RT window if available
                        if entry.rt_start is not None and entry.rt_end is not None:
                            if not (entry.rt_start <= spectrum.rt <= entry.rt_end):
                                continue
                        candidates.append(entry)

        if not candidates:
            continue

        # Match fragments from candidate peptides
        for entry in candidates:
            for fragment in entry.fragments:
                # Skip fragments with unknown m/z
                if fragment.mz <= 0:
                    continue

                # Find most intense peak within tolerance
                result = find_most_intense_peak(
                    fragment.mz,
                    spectrum.mz_array,
                    spectrum.intensity_array,
                    mz_tolerance,
                    min_intensity,
                    tolerance_ppm=tolerance_ppm,
                )

                if result is None:
                    continue

                observed_mz, observed_intensity = result
                delta_mz = observed_mz - fragment.mz
                # Calculate delta in ppm (parts per million)
                delta_ppm = (delta_mz / fragment.mz) * 1e6

                # Calculate log-transformed features
                log_tic = float(np.log10(np.clip(spectrum.tic, 1, None)))
                log_intensity = float(np.log10(np.clip(observed_intensity, 1, None)))

                # Calculate tic_injection_time if injection_time is available
                tic_injection_time = None
                if spectrum.injection_time is not None:
                    tic_injection_time = spectrum.tic * spectrum.injection_time

                # Calculate fragment_ions (observed_intensity × injection_time)
                # This gives total fragment ion count rather than ions/sec rate
                fragment_ions = None
                if spectrum.injection_time is not None:
                    fragment_ions = observed_intensity * spectrum.injection_time

                # Use raw absolute_time (Unix timestamp) - spans across multiple runs
                absolute_time = spectrum.absolute_time

                # Look up temperatures at this RT (if temperature data available)
                rfa2_temp = None
                rfc2_temp = None
                if temperature_data is not None:
                    if "RFA2" in temperature_data:
                        rfa2_temp = temperature_data["RFA2"].get_temperature_at_time(spectrum.rt)
                    if "RFC2" in temperature_data:
                        rfc2_temp = temperature_data["RFC2"].get_temperature_at_time(spectrum.rt)

                # Calculate adjacent ion population features
                # For monoisotopic m/z = X, sum intensity in ranges offset by 0.5 Th to center on isotope pattern
                ions_above_0_1 = None
                ions_above_1_2 = None
                ions_above_2_3 = None
                if spectrum.injection_time is not None:
                    # Use fragment.mz as X (the expected monoisotopic m/z)
                    x = fragment.mz
                    # (X+0.5, X+1.5] range - centered on first isotope region
                    ions_above_0_1 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x + 0.5, x + 1.5
                        )
                        * spectrum.injection_time
                    )
                    # (X+1.5, X+2.5] range - centered on second isotope region
                    ions_above_1_2 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x + 1.5, x + 2.5
                        )
                        * spectrum.injection_time
                    )
                    # (X+2.5, X+3.5] range - centered on third isotope region
                    ions_above_2_3 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x + 2.5, x + 3.5
                        )
                        * spectrum.injection_time
                    )

                # Calculate adjacent ion population features BELOW the fragment
                # For monoisotopic m/z = X, sum intensity in ranges offset by 0.5 Th
                ions_below_0_1 = None
                ions_below_1_2 = None
                ions_below_2_3 = None
                if spectrum.injection_time is not None:
                    # Use fragment.mz as X (the expected monoisotopic m/z)
                    x = fragment.mz
                    # (X-1.5, X-0.5] range - centered on first region below
                    ions_below_0_1 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x - 1.5, x - 0.5
                        )
                        * spectrum.injection_time
                    )
                    # (X-2.5, X-1.5] range - centered on second region below
                    ions_below_1_2 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x - 2.5, x - 1.5
                        )
                        * spectrum.injection_time
                    )
                    # (X-3.5, X-2.5] range - centered on third region below
                    ions_below_2_3 = (
                        sum_intensity_in_range(
                            spectrum.mz_array, spectrum.intensity_array, x - 3.5, x - 2.5
                        )
                        * spectrum.injection_time
                    )

                # Calculate ratio features (adjacent ions / fragment ions)
                # These capture relative adjacent ion population compared to the fragment signal
                adjacent_ratio_0_1 = None
                adjacent_ratio_1_2 = None
                adjacent_ratio_2_3 = None
                adjacent_ratio_below_0_1 = None
                adjacent_ratio_below_1_2 = None
                adjacent_ratio_below_2_3 = None
                if fragment_ions is not None and fragment_ions > 0:
                    if ions_above_0_1 is not None:
                        adjacent_ratio_0_1 = ions_above_0_1 / fragment_ions
                    if ions_above_1_2 is not None:
                        adjacent_ratio_1_2 = ions_above_1_2 / fragment_ions
                    if ions_above_2_3 is not None:
                        adjacent_ratio_2_3 = ions_above_2_3 / fragment_ions
                    if ions_below_0_1 is not None:
                        adjacent_ratio_below_0_1 = ions_below_0_1 / fragment_ions
                    if ions_below_1_2 is not None:
                        adjacent_ratio_below_1_2 = ions_below_1_2 / fragment_ions
                    if ions_below_2_3 is not None:
                        adjacent_ratio_below_2_3 = ions_below_2_3 / fragment_ions

                # Create match record
                ion_annotation = f"{fragment.ion_type}{fragment.ion_number}+{fragment.charge}"

                matches.append(
                    {
                        "expected_mz": fragment.mz,
                        "library_intensity": fragment.intensity,
                        "observed_mz": observed_mz,
                        "observed_intensity": observed_intensity,
                        "delta_mz": delta_mz,
                        "delta_ppm": delta_ppm,
                        "precursor_mz": spectrum.precursor_mz_center,
                        "fragment_mz": fragment.mz,
                        "fragment_charge": fragment.charge,
                        "absolute_time": absolute_time,
                        "log_tic": log_tic,
                        "log_intensity": log_intensity,
                        "injection_time": spectrum.injection_time,
                        "tic_injection_time": tic_injection_time,
                        "fragment_ions": fragment_ions,
                        "ions_above_0_1": ions_above_0_1,
                        "ions_above_1_2": ions_above_1_2,
                        "ions_above_2_3": ions_above_2_3,
                        "ions_below_0_1": ions_below_0_1,
                        "ions_below_1_2": ions_below_1_2,
                        "ions_below_2_3": ions_below_2_3,
                        "adjacent_ratio_0_1": adjacent_ratio_0_1,
                        "adjacent_ratio_1_2": adjacent_ratio_1_2,
                        "adjacent_ratio_2_3": adjacent_ratio_2_3,
                        "adjacent_ratio_below_0_1": adjacent_ratio_below_0_1,
                        "adjacent_ratio_below_1_2": adjacent_ratio_below_1_2,
                        "adjacent_ratio_below_2_3": adjacent_ratio_below_2_3,
                        "rfa2_temp": rfa2_temp,
                        "rfc2_temp": rfc2_temp,
                        "rt": spectrum.rt,
                        "peptide_sequence": entry.modified_sequence,
                        "ion_annotation": ion_annotation,
                        "scan_number": spectrum.scan_number,
                    }
                )

                n_matched_fragments += 1
                matched_peptide_keys.add(entry.make_key())

    # Detailed logging
    logger.info(f"Matched {n_matched_fragments:,} fragments from {n_spectra:,} spectra")
    logger.info(f"  Unique peptides matched: {len(matched_peptide_keys):,} / {len(library):,}")

    # Log precursor ranges seen
    if precursor_ranges_seen:
        ranges_str = ", ".join(f"{lo}-{hi}" for lo, hi in sorted(precursor_ranges_seen))
        logger.info(f"  DIA windows in file: {ranges_str}")

        # Count library entries that fall in these ranges
        entries_in_range = sum(
            1
            for e in library
            if any(lo <= e.precursor_mz <= hi for lo, hi in precursor_ranges_seen)
        )
        logger.info(f"  Library entries in precursor range: {entries_in_range:,}")

    df = pd.DataFrame(matches)

    if len(df) > 0:
        logger.info("Delta m/z statistics (Th):")
        logger.info(f"  Mean:   {df['delta_mz'].mean():.4f} Th")
        logger.info(f"  Median: {df['delta_mz'].median():.4f} Th")
        logger.info(f"  Std:    {df['delta_mz'].std():.4f} Th")

        logger.info("Delta m/z statistics (ppm):")
        logger.info(f"  Mean:   {df['delta_ppm'].mean():.2f} ppm")
        logger.info(f"  Median: {df['delta_ppm'].median():.2f} ppm")
        logger.info(f"  Std:    {df['delta_ppm'].std():.2f} ppm")

        # Intensity-weighted statistics
        weights = df["observed_intensity"].values
        weighted_mean_th = np.average(df["delta_mz"].values, weights=weights)
        weighted_mean_ppm = np.average(df["delta_ppm"].values, weights=weights)
        logger.info(
            f"  Intensity-weighted mean: {weighted_mean_th:.4f} Th ({weighted_mean_ppm:.2f} ppm)"
        )

    return df


def filter_matches(
    matches: pd.DataFrame,
    max_delta_mz: float | None = None,
    min_intensity: float | None = None,
    min_library_intensity: float | None = None,
) -> pd.DataFrame:
    """Filter fragment matches by quality criteria.

    Args:
        matches: DataFrame from match_library_to_spectra
        max_delta_mz: Maximum absolute delta m/z to keep
        min_intensity: Minimum observed intensity
        min_library_intensity: Minimum library intensity

    Returns:
        Filtered DataFrame
    """
    df = matches.copy()
    n_orig = len(df)

    if max_delta_mz is not None:
        df = df[df["delta_mz"].abs() <= max_delta_mz]

    if min_intensity is not None:
        df = df[df["observed_intensity"] >= min_intensity]

    if min_library_intensity is not None:
        df = df[df["library_intensity"] >= min_library_intensity]

    n_filtered = len(df)
    logger.info(
        f"Filtered matches: {n_orig} -> {n_filtered} ({n_filtered / n_orig * 100:.1f}% kept)"
    )

    return df


def apply_calibration_to_matches(
    matches: pd.DataFrame,
    calibrator,
) -> pd.DataFrame:
    """Apply calibration model to recalculate delta m/z.

    Used to assess calibration quality with before/after comparison.

    Args:
        matches: DataFrame with fragment matches
        calibrator: MzCalibrator with trained model

    Returns:
        DataFrame with updated delta_mz_calibrated column
    """
    df = matches.copy()

    # Get predictions using DataFrame interface
    corrections = calibrator.predict(matches=df)

    # Apply corrections
    df["delta_mz_calibrated"] = df["delta_mz"] - corrections
    df["mz_correction"] = corrections
    # Calculate calibrated delta in ppm
    df["delta_ppm_calibrated"] = (df["delta_mz_calibrated"] / df["fragment_mz"]) * 1e6

    return df
