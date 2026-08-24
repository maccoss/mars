"""Mars: Mass Accuracy Recalibration System for Thermo Stellar DIA data."""

from importlib.metadata import PackageNotFoundError
from importlib.metadata import version as _package_version

from mars.calibration import MzCalibrator
from mars.library import Fragment, LibraryEntry, load_blib, load_diann_library, load_prism_library
from mars.matching import FragmentMatch, match_library_to_spectra
from mars.mzml import DIASpectrum, read_dia_spectra, write_calibrated_mzml
from mars.visualization import plot_delta_mz_heatmap, plot_delta_mz_histogram

try:
    # Single source of truth: the version declared in pyproject.toml and recorded in the
    # installed distribution metadata. A literal here drifted to 0.1.4 while the package
    # shipped as 0.1.5, and nothing caught it.
    __version__ = _package_version("mars-ms")
except PackageNotFoundError:  # running from a source tree that was never installed
    __version__ = "0.0.0.dev0"

__all__ = [
    # Library
    "LibraryEntry",
    "Fragment",
    "load_blib",
    "load_diann_library",
    "load_prism_library",
    # mzML
    "DIASpectrum",
    "read_dia_spectra",
    "write_calibrated_mzml",
    # Matching
    "FragmentMatch",
    "match_library_to_spectra",
    # Calibration
    "MzCalibrator",
    # Visualization
    "plot_delta_mz_histogram",
    "plot_delta_mz_heatmap",
]
