"""mzML file reading and writing for DIA data.

Uses pyteomics for parsing mzML files and extracting DIA MS2 spectra
with isolation window information.
"""

from __future__ import annotations

import logging
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from pyteomics import mzml

logger = logging.getLogger(__name__)

# Array name constants for encoding
MZ_ARRAY = "m/z array"
INTENSITY_ARRAY = "intensity array"
TIME_ARRAY = "time array"


@dataclass
class DIASpectrum:
    """Single MS2 spectrum from DIA acquisition."""

    scan_number: int
    rt: float  # Retention time in minutes
    precursor_mz_low: float  # Isolation window lower bound
    precursor_mz_high: float  # Isolation window upper bound
    precursor_mz_center: float  # Isolation window center
    tic: float  # Total ion current
    mz_array: np.ndarray  # Fragment m/z values
    intensity_array: np.ndarray  # Fragment intensities
    injection_time: float | None = None  # Ion injection time in seconds (optional)
    acquisition_start_time: float | None = None  # File acquisition start time (Unix timestamp)
    absolute_time: float | None = None  # Absolute time in seconds (normalized to earliest file)

    @property
    def n_peaks(self) -> int:
        """Number of peaks in spectrum."""
        return len(self.mz_array)


def _extract_isolation_window(spectrum: dict) -> tuple[float, float, float]:
    """Extract isolation window bounds from spectrum metadata.

    Args:
        spectrum: Pyteomics spectrum dict

    Returns:
        Tuple of (low, high, center) m/z values
    """
    try:
        precursor_list = spectrum.get("precursorList", {})
        precursors = precursor_list.get("precursor", [])

        if precursors:
            precursor = precursors[0]
            isolation = precursor.get("isolationWindow", {})

            target = isolation.get("isolation window target m/z", 0.0)
            lower_offset = isolation.get("isolation window lower offset", 0.0)
            upper_offset = isolation.get("isolation window upper offset", 0.0)

            low = target - lower_offset
            high = target + upper_offset

            return float(low), float(high), float(target)

    except Exception as e:
        logger.debug(f"Failed to extract isolation window: {e}")

    # Fallback: try selected ion m/z
    try:
        precursor_list = spectrum.get("precursorList", {})
        precursors = precursor_list.get("precursor", [])
        if precursors:
            selected_ions = precursors[0].get("selectedIonList", {}).get("selectedIon", [])
            if selected_ions:
                mz = selected_ions[0].get("selected ion m/z", 0.0)
                return float(mz) - 0.5, float(mz) + 0.5, float(mz)
    except Exception:
        pass

    return 0.0, 0.0, 0.0


def _extract_scan_number(spectrum: dict) -> int:
    """Extract scan number from spectrum ID.

    Args:
        spectrum: Pyteomics spectrum dict

    Returns:
        Scan number (0 if not found)
    """
    spec_id = spectrum.get("id", "")

    # Try "scan=NNNN" format
    if "scan=" in spec_id:
        try:
            return int(spec_id.split("scan=")[1].split()[0])
        except (ValueError, IndexError):
            pass

    # Try index from spectrum
    return spectrum.get("index", 0)


def _extract_injection_time(spectrum: dict) -> float | None:
    """Extract ion injection time from spectrum metadata.

    Args:
        spectrum: Pyteomics spectrum dict

    Returns:
        Injection time in seconds, or None if not found
    """
    try:
        precursor_list = spectrum.get("precursorList", {})
        precursors = precursor_list.get("precursor", [])

        if precursors:
            precursor = precursors[0]
            # Look for ion injection time in precursor
            injection_time_ms = precursor.get("ion injection time")
            if injection_time_ms is not None:
                # Convert from milliseconds to seconds
                return float(injection_time_ms) / 1000.0

        # Also check in scan level
        scan_list = spectrum.get("scanList", {})
        scans = scan_list.get("scan", [])
        if scans:
            injection_time_ms = scans[0].get("ion injection time")
            if injection_time_ms is not None:
                return float(injection_time_ms) / 1000.0

    except Exception as e:
        logger.debug(f"Failed to extract injection time: {e}")

    return None


def _parse_iso8601_timestamp(timestamp_str: str) -> float | None:
    """Parse ISO 8601 timestamp to Unix timestamp.

    Args:
        timestamp_str: ISO 8601 formatted timestamp string (e.g., "2023-01-15T10:30:45Z")

    Returns:
        Unix timestamp (float) or None if parsing fails
    """
    try:
        from datetime import datetime

        # Try parsing with timezone info
        try:
            dt = datetime.fromisoformat(timestamp_str.replace("Z", "+00:00"))
        except (ValueError, AttributeError):
            # Fallback: try without timezone
            dt = datetime.fromisoformat(timestamp_str)

        # Convert to Unix timestamp
        return float(dt.timestamp())
    except Exception as e:
        logger.debug(f"Failed to parse ISO 8601 timestamp '{timestamp_str}': {e}")
        return None


def read_dia_spectra(
    mzml_path: Path | str,
    ms_level: int = 2,
    min_absolute_time: float | None = None,
) -> Iterator[DIASpectrum]:
    """Stream DIA MS2 spectra from mzML file.

    Args:
        mzml_path: Path to mzML file
        ms_level: MS level to extract (default: 2 for MS2)
        min_absolute_time: Minimum absolute time to use for normalization (seconds).
                          If provided, all absolute_time values will be normalized to this reference.

    Yields:
        DIASpectrum objects for each matching spectrum
    """
    import re

    mzml_path = Path(mzml_path)
    logger.info(f"Reading DIA spectra from {mzml_path}")

    if not mzml_path.exists():
        raise FileNotFoundError(f"mzML file not found: {mzml_path}")

    n_spectra = 0
    n_yielded = 0
    acquisition_start_time = None

    # Extract startTimeStamp from the <run> element before processing spectra
    try:
        with open(mzml_path, encoding="utf-8") as f:
            for line_num, line in enumerate(f):
                if "startTimeStamp" in line:
                    match = re.search(r'startTimeStamp="([^"]+)"', line)
                    if match:
                        timestamp_str = match.group(1)
                        acquisition_start_time = _parse_iso8601_timestamp(timestamp_str)
                        if acquisition_start_time is not None:
                            logger.info(f"Found acquisition start time: {timestamp_str}")
                        break
                # Stop searching after a reasonable number of lines (run element should be near the top)
                if line_num > 200:
                    break
    except Exception as e:
        logger.debug(f"Failed to extract startTimeStamp: {e}")

    if acquisition_start_time is None:
        logger.info("No acquisition start time found in mzML, will use RT as absolute time")

    with mzml.MzML(str(mzml_path)) as reader:
        for spectrum in reader:
            n_spectra += 1

            # Filter by MS level
            spec_ms_level = spectrum.get("ms level", 1)
            if spec_ms_level != ms_level:
                continue

            # Extract arrays
            mz_array = spectrum.get("m/z array", np.array([]))
            intensity_array = spectrum.get("intensity array", np.array([]))

            if len(mz_array) == 0:
                continue

            # Extract metadata
            rt = spectrum.get("scanList", {}).get("scan", [{}])[0].get("scan start time", 0.0)
            # Convert to minutes if in seconds
            rt_unit = (
                spectrum.get("scanList", {})
                .get("scan", [{}])[0]
                .get("scan start time", {"unit_info": "minute"})
            )
            if isinstance(rt_unit, dict) and rt_unit.get("unit_info") == "second":
                rt = rt / 60.0

            # Sometimes RT is stored directly
            if rt == 0.0 and "scan start time" in spectrum:
                rt = spectrum["scan start time"]

            # Calculate TIC
            tic = float(np.sum(intensity_array))

            # Extract isolation window
            low, high, center = _extract_isolation_window(spectrum)

            # Get scan number
            scan_number = _extract_scan_number(spectrum)

            # Extract injection time
            injection_time = _extract_injection_time(spectrum)

            # Calculate absolute_time
            # If acquisition_start_time is available, use it; otherwise use RT in seconds
            if acquisition_start_time is not None:
                # Use acquisition start time + RT offset
                absolute_time = acquisition_start_time + (rt * 60.0)
            else:
                # Fallback: use RT converted to seconds
                absolute_time = rt * 60.0

            # Normalize to min_absolute_time if provided
            if min_absolute_time is not None:
                absolute_time = absolute_time - min_absolute_time

            n_yielded += 1
            yield DIASpectrum(
                scan_number=scan_number,
                rt=float(rt),
                precursor_mz_low=low,
                precursor_mz_high=high,
                precursor_mz_center=center,
                tic=tic,
                mz_array=np.asarray(mz_array, dtype=np.float64),
                intensity_array=np.asarray(intensity_array, dtype=np.float64),
                injection_time=injection_time,
                acquisition_start_time=acquisition_start_time,
                absolute_time=absolute_time,
            )

    logger.info(f"Read {n_yielded} MS2 spectra from {n_spectra} total spectra")


def write_calibrated_mzml(
    input_path: Path | str,
    output_path: Path | str,
    calibration_func,
    max_isolation_window_width: float | None = None,
    temperature_data: dict | None = None,
) -> None:
    """Write calibrated mzML file with corrected m/z values.

    Uses a passthrough approach that preserves all original file metadata and structure,
    only modifying the m/z binary data for MS2 spectra. This ensures compatibility with
    downstream tools like DIA-NN, SeeMS, and MSConvert.

    Args:
        input_path: Path to input mzML file
        output_path: Path for output mzML file
        calibration_func: Function that takes (spectrum_metadata, mz_array, intensity_array)
                         and returns calibrated mz_array
        max_isolation_window_width: Maximum isolation window width (m/z) to calibrate.
                                    MS2 spectra with wider windows are left unchanged.
        temperature_data: Dict mapping source names (e.g., 'RFA2', 'RFC2') to TemperatureData objects
    """
    import base64
    import hashlib
    import re
    import zlib

    from lxml import etree

    input_path = Path(input_path)
    output_path = Path(output_path)

    logger.info(f"Writing calibrated mzML to {output_path}")

    # Extract acquisition start time for absolute_time calculation
    acquisition_start_time = None
    try:
        with open(input_path, encoding="utf-8", errors="ignore") as f:
            for line in f:
                if "startTimeStamp" in line:
                    match = re.search(r'startTimeStamp="([^"]+)"', line)
                    if match:
                        acquisition_start_time = _parse_iso8601_timestamp(match.group(1))
                    break
                if "<spectrumList" in line:
                    break
    except Exception as e:
        logger.debug(f"Failed to extract startTimeStamp: {e}")

    # Read raw file content
    with open(input_path, "rb") as f:
        raw_content = f.read()

    content_str = raw_content.decode("utf-8")

    # Find index section (we'll regenerate it)
    index_match = re.search(r"\s*<indexList", content_str)
    if not index_match:
        raise ValueError("No indexList found - file may not be an indexed mzML")

    main_xml = content_str[: index_match.start()]

    # Find mzML element boundaries
    mzml_start = main_xml.find("<mzML")
    mzml_end = main_xml.rfind("</mzML>") + len("</mzML>")

    indexed_header = main_xml[:mzml_start]
    mzml_content = main_xml[mzml_start:mzml_end]
    indexed_footer = main_xml[mzml_end:]

    # Parse mzML with lxml
    root = etree.fromstring(mzml_content.encode("utf-8"))
    ns = {"ms": "http://psi.hupo.org/ms/mzml"}

    # Get spectrum metadata from pyteomics for calibration
    spectra_meta = {}
    with mzml.MzML(str(input_path)) as reader:
        for spec in reader:
            spec_id = spec.get("id", "")
            ms_level = spec.get("ms level", 1)
            if ms_level == 2:
                scan_list = spec.get("scanList", {})
                scans = scan_list.get("scan", [])
                scan_time = scans[0].get("scan start time", 0.0) if scans else 0.0

                # Get isolation window info
                center, low_off, high_off = 0, 0, 0
                precursor_list = spec.get("precursorList", {})
                precursors = precursor_list.get("precursor", [])
                if precursors:
                    iso = precursors[0].get("isolationWindow", {})
                    center = iso.get("isolation window target m/z", 0)
                    low_off = abs(iso.get("isolation window lower offset", 0))
                    high_off = abs(iso.get("isolation window upper offset", 0))

                spectra_meta[spec_id] = {
                    "scan_time": scan_time,
                    "isolation_center": center,
                    "window_width": low_off + high_off,
                    "intensity": spec.get("intensity array", np.array([])),
                    "tic": spec.get("total ion current", 0.0),
                    "injection_time": _extract_injection_time(spec),
                }

    logger.info(f"Found {len(spectra_meta)} MS2 spectra")

    # Helper functions for binary encoding
    def decode_binary(encoded_data: str, is_zlib: bool, dtype) -> np.ndarray:
        data = base64.b64decode("".join(encoded_data.split()))
        if is_zlib:
            data = zlib.decompress(data)
        return np.frombuffer(data, dtype=dtype)

    def encode_binary(arr: np.ndarray, is_zlib: bool, dtype) -> str:
        arr = np.asarray(arr, dtype=dtype)
        data = arr.tobytes()
        if is_zlib:
            data = zlib.compress(data)
        return base64.b64encode(data).decode("ascii")

    def is_mz_array(elem) -> bool:
        for cv in elem.findall("ms:cvParam", ns):
            if cv.get("name") == "m/z array":
                return True
        return False

    def get_encoding_info(elem) -> tuple[bool, type]:
        is_zlib, dtype = False, np.float64
        for cv in elem.findall("ms:cvParam", ns):
            name = cv.get("name", "")
            if "zlib" in name.lower():
                is_zlib = True
            if "32-bit" in name:
                dtype = np.float32
        return is_zlib, dtype

    # Process spectra in the XML tree
    n_calibrated = 0
    for spectrum in root.iter("{http://psi.hupo.org/ms/mzml}spectrum"):
        spec_id = spectrum.get("id")

        if spec_id not in spectra_meta:
            continue  # MS1 spectrum

        meta = spectra_meta[spec_id]

        # Check window width
        if max_isolation_window_width and meta["window_width"] > max_isolation_window_width:
            continue  # Too wide, leave unchanged

        # Find and modify m/z array
        for binary_array in spectrum.iter("{http://psi.hupo.org/ms/mzml}binaryDataArray"):
            if not is_mz_array(binary_array):
                continue

            is_zlib, dtype = get_encoding_info(binary_array)
            binary_elem = binary_array.find("{http://psi.hupo.org/ms/mzml}binary")

            if binary_elem is None or not binary_elem.text:
                continue

            # Decode m/z array
            mz_array = decode_binary(binary_elem.text, is_zlib, dtype)

            # Calculate absolute time
            absolute_time = 0.0
            if acquisition_start_time is not None:
                absolute_time = acquisition_start_time + meta["scan_time"] * 60.0

            # Look up temperatures
            rfa2_temp, rfc2_temp = 0.0, 0.0
            if temperature_data is not None:
                if "RFA2" in temperature_data:
                    rfa2_temp = temperature_data["RFA2"].get_temperature_at_time(meta["scan_time"])
                if "RFC2" in temperature_data:
                    rfc2_temp = temperature_data["RFC2"].get_temperature_at_time(meta["scan_time"])

            # Build calibration metadata
            cal_meta = {
                "rt": meta["scan_time"],
                "precursor_mz": meta["isolation_center"],
                "tic": meta["tic"],
                "injection_time": meta["injection_time"] if meta["injection_time"] else 0.0,
                "absolute_time": absolute_time,
                "rfa2_temp": rfa2_temp,
                "rfc2_temp": rfc2_temp,
            }

            # Apply calibration
            calibrated_mz = calibration_func(cal_meta, mz_array, meta["intensity"])

            # Re-encode with same settings
            new_binary = encode_binary(calibrated_mz, is_zlib, dtype)

            # Update XML
            binary_elem.text = new_binary
            binary_array.set("encodedLength", str(len(new_binary)))

            n_calibrated += 1

    logger.info(f"Calibrated {n_calibrated} MS2 spectra")

    # Serialize modified mzML back to string
    modified_mzml = etree.tostring(root, encoding="unicode")

    # Reconstruct full content (preserving original header/footer)
    modified_content = indexed_header + modified_mzml + indexed_footer
    modified_bytes = modified_content.encode("utf-8")

    # Regenerate index with correct byte offsets
    spectrum_offsets = []
    for match in re.finditer(rb'<spectrum[^>]+id="([^"]+)"', modified_bytes):
        spectrum_offsets.append((match.group(1).decode("utf-8"), match.start()))

    chrom_offsets = []
    for match in re.finditer(rb'<chromatogram[^>]+id="([^"]+)"', modified_bytes):
        chrom_offsets.append((match.group(1).decode("utf-8"), match.start()))

    # Build index XML
    index_lines = ["  <indexList count=\"2\">\n", "    <index name=\"spectrum\">\n"]
    for id_ref, offset in spectrum_offsets:
        index_lines.append(f'      <offset idRef="{id_ref}">{offset}</offset>\n')
    index_lines.append("    </index>\n")
    index_lines.append('    <index name="chromatogram">\n')
    for id_ref, offset in chrom_offsets:
        index_lines.append(f'      <offset idRef="{id_ref}">{offset}</offset>\n')
    index_lines.append("    </index>\n")
    index_lines.append("  </indexList>\n")
    index_xml = "".join(index_lines)

    # Calculate indexListOffset and checksum
    index_list_offset = len(modified_bytes)
    offset_line = f"  <indexListOffset>{index_list_offset}</indexListOffset>\n"

    checksum_content = modified_bytes + index_xml.encode("utf-8") + offset_line.encode("utf-8")
    sha1 = hashlib.sha1(checksum_content).hexdigest()

    # Write final output
    output_path.parent.mkdir(parents=True, exist_ok=True)
    final_content = (
        modified_content + index_xml + offset_line + f"  <fileChecksum>{sha1}</fileChecksum>\n</indexedmzML>"
    )

    # Write in binary mode: the index offsets above are byte positions into the
    # UTF-8/LF stream, so newline translation (CRLF on Windows) would shift every
    # offset by the number of preceding lines and corrupt the index.
    with open(output_path, "wb") as f:
        f.write(final_content.encode("utf-8"))

    logger.info(f"Wrote {len(spectrum_offsets)} spectra ({n_calibrated} MS2 calibrated)")


def get_output_path(input_path: Path | str, output_dir: Path | str | None = None) -> Path:
    """Generate output path for calibrated mzML file.

    Output file is named {input_stem}-mars.mzML

    Args:
        input_path: Input mzML file path
        output_dir: Output directory (uses input dir if None)

    Returns:
        Output file path
    """
    input_path = Path(input_path)
    output_dir = Path(output_dir) if output_dir else input_path.parent

    stem = input_path.stem
    if stem.endswith(".mzML"):
        stem = stem[:-5]

    return output_dir / f"{stem}-mars.mzML"
