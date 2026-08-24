// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the MARS Python implementation (mars/mzml.py).

using System;

namespace MARS.Core;

/// <summary>
/// One spectrum handed to MARS by the reader. The arrays are owned by the reader and
/// are only valid for the duration of the callback that receives them.
/// </summary>
public sealed class SpectrumRecord
{
    /// <summary>Scan number parsed out of the nativeID, or the spectrum index as a fallback.</summary>
    public int ScanNumber;

    /// <summary>Zero-based position in the spectrum list.</summary>
    public int Index;

    /// <summary>mzML spectrum id, e.g. "controllerType=0 controllerNumber=1 scan=42".</summary>
    public string Id = string.Empty;

    public int MsLevel;

    /// <summary>
    /// The spectrum's instrumentConfigurationRef, or null when it inherits the run default.
    /// On a hybrid instrument this is what says which analyzer recorded the scan.
    /// </summary>
    public string? InstrumentConfigurationRef;

    /// <summary>
    /// Thermo's scan filter (MS:1000512), when present. A fallback for identifying the
    /// analyzer on files whose instrument configuration does not settle it.
    /// </summary>
    public string? FilterString;

    /// <summary>Scan start time, in minutes.</summary>
    public double RetentionTime;

    /// <summary>Isolation window lower bound (target - lower offset), in Th.</summary>
    public double PrecursorMzLow;

    /// <summary>Isolation window upper bound (target + upper offset), in Th.</summary>
    public double PrecursorMzHigh;

    /// <summary>Isolation window target m/z, in Th. This is the precursor_mz feature.</summary>
    public double PrecursorMzCenter;

    /// <summary>
    /// Sum of the decoded intensity array. This is what the Python matcher calls "tic"
    /// and what the log_tic and tic_injection_time features are computed from.
    /// </summary>
    public double SummedIntensity;

    /// <summary>Value of the MS:1000285 total ion current cvParam, or 0 when absent.</summary>
    public double ReportedTic;

    /// <summary>Ion injection time in SECONDS (the mzML cvParam is in milliseconds), or null.</summary>
    public double? InjectionTime;

    /// <summary>Run start time as a Unix timestamp in seconds, or null when unparseable.</summary>
    public double? AcquisitionStartTime;

    /// <summary>Acquisition start + RT, in seconds. Normalization happens later.</summary>
    public double AbsoluteTime;

    public double[] MzArray = Array.Empty<double>();

    public double[] IntensityArray = Array.Empty<double>();

    public int PeakCount;

    public double IsolationWindowWidth => PrecursorMzHigh - PrecursorMzLow;

    public ReadOnlySpan<double> Mz => MzArray.AsSpan(0, PeakCount);

    public ReadOnlySpan<double> Intensity => IntensityArray.AsSpan(0, PeakCount);
}
