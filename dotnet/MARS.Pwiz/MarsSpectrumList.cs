// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using MARS.Core;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.MsData.Processing;
using Pwiz.Data.MsData.Spectra;

namespace MARS.Pwiz;

/// <summary>
/// A pwiz spectrum list that applies MARS's correction as spectra are pulled through it.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the integration. MARS's science is untouched and reached through the
/// same <see cref="SpectrumCorrector"/> the byte-splice writer uses, so whichever format is
/// being written gets exactly the values mzML would have got - verified by writing both and
/// diffing them with <c>mars compare</c>, which found no difference across 82,349,582 peaks.
/// </para>
/// <para>
/// Derived from <c>SpectrumListBase</c> in <c>Pwiz.Data.MsData</c> rather than
/// <c>SpectrumListWrapper</c> in <c>Pwiz.Analysis</c>, at the cost of three delegating
/// members. Analysis references the Waters reader, which stages a native Windows-only
/// MassLynxRaw.dll into the output, and MARS ships four non-Windows artifacts. Nothing here
/// needs Analysis.
/// </para>
/// <para>
/// Not thread-safe: one workspace and one record are reused across calls to avoid allocating
/// per spectrum. pwiz's writers pull spectra sequentially, which is the only caller.
/// </para>
/// </remarks>
internal sealed class MarsSpectrumList : SpectrumListBase
{
    private readonly ISpectrumList _inner;
    private readonly SpectrumCorrector? _corrector;
    private readonly TemperatureSet? _temperatures;
    private readonly CorrectionWorkspace _workspace = new();
    private readonly SpectrumRecord _record = new();
    private readonly double? _acquisitionStart;
    private double[] _corrected = Array.Empty<double>();

    public MarsSpectrumList(
        ISpectrumList inner, MzCalibrator? calibrator, CorrectionOptions options,
        double? acquisitionStart, TemperatureSet? temperatures)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _corrector = calibrator is null ? null : new SpectrumCorrector(calibrator, options);
        _acquisitionStart = acquisitionStart;
        _temperatures = temperatures;
    }

    public long SpectraSeen { get; private set; }

    public long SpectraCorrected { get; private set; }

    public long MonotonicityFixes { get; private set; }

    public long SpectraReverted { get; private set; }

    public override int Count => _inner.Count;

    public override SpectrumIdentity SpectrumIdentity(int index) => _inner.SpectrumIdentity(index);

    public override DataProcessing? DataProcessing => _inner.DataProcessing;

    public override Spectrum GetSpectrum(int index, bool getBinaryData = false)
    {
        Spectrum spectrum = _inner.GetSpectrum(index, getBinaryData);
        if (_corrector is null || !getBinaryData) return spectrum;

        if (spectrum.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0) != 2) return spectrum;

        BinaryDataArray? mz = spectrum.GetMZArray();
        BinaryDataArray? intensity = spectrum.GetIntensityArray();
        if (mz is null || intensity is null || mz.Data.Count == 0) return spectrum;

        SpectraSeen++;
        Fill(spectrum, mz, intensity);

        int peaks = mz.Data.Count;
        if (_corrected.Length < peaks) _corrected = new double[peaks];
        Span<double> corrected = _corrected.AsSpan(0, peaks);

        // Correcting in place would be wrong: the corrector reverts a whole spectrum when the
        // correction would reorder peaks, and it cannot revert what it has already overwritten.
        SpectrumCorrectionResult result = _corrector.Correct(_record, _temperatures, _workspace, corrected);

        MonotonicityFixes += result.MonotonicityFixes;
        if (result.Reverted) SpectraReverted++;
        if (!result.Corrected) return spectrum;

        SpectraCorrected++;
        for (int i = 0; i < peaks; i++) mz.Data[i] = corrected[i];
        return spectrum;
    }

    protected override void DisposeCore() => _inner.Dispose();

    /// <summary>Copies what MARS's features are computed from out of a pwiz spectrum.</summary>
    private void Fill(Spectrum spectrum, BinaryDataArray mz, BinaryDataArray intensity)
    {
        Scan? scan = spectrum.ScanList.Scans.Count > 0 ? spectrum.ScanList.Scans[0] : null;

        _record.Id = spectrum.Id;
        _record.Index = spectrum.Index;
        _record.ScanNumber = ScanNumberOf(spectrum.Id, spectrum.Index);
        _record.MsLevel = 2;
        _record.InstrumentConfigurationRef = null;
        _record.FilterString = scan?.CvParam(CVID.MS_filter_string).Value;

        // pwiz normalizes scan start time to minutes, which is the unit MARS stores.
        _record.RetentionTime = scan?.CvParamValueOrDefault(CVID.MS_scan_start_time, 0.0) ?? 0.0;

        // MARS holds injection time in seconds; the cvParam is in milliseconds.
        double injectionMs = scan?.CvParamValueOrDefault(CVID.MS_ion_injection_time, 0.0) ?? 0.0;
        _record.InjectionTime = injectionMs > 0 ? injectionMs / 1000.0 : null;

        _record.PrecursorMzCenter = 0;
        _record.PrecursorMzLow = 0;
        _record.PrecursorMzHigh = 0;
        if (spectrum.Precursors.Count > 0)
        {
            IsolationWindow window = spectrum.Precursors[0].IsolationWindow;
            double target = window.CvParamValueOrDefault(CVID.MS_isolation_window_target_m_z, 0.0);
            double lower = window.CvParamValueOrDefault(CVID.MS_isolation_window_lower_offset, 0.0);
            double upper = window.CvParamValueOrDefault(CVID.MS_isolation_window_upper_offset, 0.0);
            _record.PrecursorMzCenter = target;
            _record.PrecursorMzLow = target - lower;
            _record.PrecursorMzHigh = target + upper;
        }

        _record.ReportedTic = spectrum.Params.CvParamValueOrDefault(CVID.MS_total_ion_current, 0.0);

        int peaks = mz.Data.Count;
        if (_record.MzArray.Length < peaks) _record.MzArray = new double[peaks];
        if (_record.IntensityArray.Length < peaks) _record.IntensityArray = new double[peaks];

        // Summed here rather than taken from the TIC cvParam, because that is what the Python
        // matcher did and the log_tic and tic_injection_time features are defined on it.
        double summed = 0;
        for (int i = 0; i < peaks; i++)
        {
            _record.MzArray[i] = mz.Data[i];
            double value = intensity.Data[i];
            _record.IntensityArray[i] = value;
            summed += value;
        }

        _record.PeakCount = peaks;
        _record.SummedIntensity = summed;
        _record.AcquisitionStartTime = _acquisitionStart;
        _record.AbsoluteTime = (_acquisitionStart ?? 0) + (_record.RetentionTime * 60.0);
    }

    /// <summary>
    /// Pulls the scan number out of a nativeID, falling back to the index. MARS uses this only
    /// for reporting, but a wrong number in a warning sends someone to the wrong spectrum.
    /// </summary>
    private static int ScanNumberOf(string id, int fallback)
    {
        const string marker = "scan=";
        int at = id.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return fallback;

        int start = at + marker.Length;
        int end = start;
        while (end < id.Length && char.IsDigit(id[end])) end++;

        return end > start &&
               int.TryParse(id.AsSpan(start, end - start), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out int scan)
            ? scan
            : fallback;
    }
}
