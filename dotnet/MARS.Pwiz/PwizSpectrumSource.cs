// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MARS.Core;
using Pwiz.Data.Common;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.Common.Params;
using Pwiz.Data.MsData;
using Pwiz.Data.MsData.Instruments;
using Pwiz.Data.MsData.Readers;
using Pwiz.Data.MsData.Spectra;

namespace MARS.Pwiz;

/// <summary>
/// Reads spectra through pwiz, so MARS can open a Thermo <c>.raw</c> without converting it
/// first.
/// </summary>
/// <remarks>
/// <para>
/// Removing the conversion is the point. It is not faster to read - a 4.9 GB Astral run takes
/// about 72 s through the vendor SDK against 41 s for the equivalent mzML, and it does not
/// thread - but it removes an msconvert pass and the ~5 GB intermediate it leaves behind.
/// </para>
/// <para>
/// One <see cref="SpectrumRecord"/> is reused across the enumeration, matching what
/// <c>MzMLFile.ReadSpectra</c> does, so a consumer that keeps the arrays must copy them.
/// </para>
/// </remarks>
internal sealed class PwizSpectrumSource : ISpectrumSource
{
    private readonly MSData _msd = new();
    private readonly ISpectrumList _spectra;

    public PwizSpectrumSource(string path)
    {
        Path = path;
        Length = LengthOf(path);

        // Collapse the ion mobility dimension. pwiz otherwise presents an uncombined TIMS
        // frame as hundreds of spectra that share one retention time and one isolation m/z,
        // separated only by mobility - ProteoWizard's own diaPASEF.d is 4,631 spectra at five
        // distinct scan times. MARS has no mobility feature and does not want one: combining
        // sums each frame's mobility scans back into one spectrum per isolation window, which
        // is the shape every other instrument already produces and the shape the matcher and
        // the space-charge features assume.
        ReaderList.Default.Read(path, _msd, new ReaderConfig { CombineIonMobilitySpectra = true });
        _spectra = _msd.Run.SpectrumList
                   ?? throw new InvalidDataException($"No spectra in {path}.");

        AcquisitionStartTime = StartTimeOf(_msd);
        Analyzer = DetectAnalyzer(_msd, _spectra);
    }

    public string Path { get; }

    public long Length { get; }

    public double? AcquisitionStartTime { get; }

    public MassAnalyzerClass Analyzer { get; }

    public IEnumerable<SpectrumRecord> ReadSpectra(int? msLevel = 2)
    {
        var record = new SpectrumRecord();

        for (int i = 0; i < _spectra.Count; i++)
        {
            // Metadata first: deciding whether this spectrum is wanted before decoding its
            // arrays is most of the saving when only MS2 is being read.
            Spectrum probe = _spectra.GetSpectrum(i, DetailLevel.FullMetadata);
            int level = probe.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0);
            if (msLevel.HasValue && level != msLevel.Value) continue;

            Spectrum spectrum = _spectra.GetSpectrum(i, getBinaryData: true);
            if (Fill(record, spectrum, level)) yield return record;
        }
    }

    public void Dispose()
    {
        _spectra.Dispose();
        _msd.Dispose();
    }

    /// <summary>Copies one pwiz spectrum into the record MARS's matcher consumes.</summary>
    private bool Fill(SpectrumRecord record, Spectrum spectrum, int msLevel)
    {
        BinaryDataArray? mz = spectrum.GetMZArray();
        BinaryDataArray? intensity = spectrum.GetIntensityArray();
        if (mz is null || intensity is null) return false;

        Scan? scan = spectrum.ScanList.Scans.Count > 0 ? spectrum.ScanList.Scans[0] : null;

        record.Id = spectrum.Id;
        record.Index = spectrum.Index;
        record.ScanNumber = ScanNumberOf(spectrum.Id, spectrum.Index);
        record.MsLevel = msLevel;
        record.InstrumentConfigurationRef = null;
        record.FilterString = scan?.CvParam(CVID.MS_filter_string).Value;

        record.RetentionTime = Minutes(scan?.CvParam(CVID.MS_scan_start_time));

        // MARS holds injection time in seconds; the cvParam is milliseconds.
        double injectionMs = scan?.CvParamValueOrDefault(CVID.MS_ion_injection_time, 0.0) ?? 0.0;
        record.InjectionTime = injectionMs > 0 ? injectionMs / 1000.0 : null;

        record.PrecursorMzCenter = 0;
        record.PrecursorMzLow = 0;
        record.PrecursorMzHigh = 0;
        if (spectrum.Precursors.Count > 0)
        {
            IsolationWindow window = spectrum.Precursors[0].IsolationWindow;
            double target = window.CvParamValueOrDefault(CVID.MS_isolation_window_target_m_z, 0.0);
            double lower = window.CvParamValueOrDefault(CVID.MS_isolation_window_lower_offset, 0.0);
            double upper = window.CvParamValueOrDefault(CVID.MS_isolation_window_upper_offset, 0.0);
            record.PrecursorMzCenter = target;
            record.PrecursorMzLow = target - lower;
            record.PrecursorMzHigh = target + upper;
        }

        record.ReportedTic = spectrum.Params.CvParamValueOrDefault(CVID.MS_total_ion_current, 0.0);

        int peaks = mz.Data.Count;
        if (record.MzArray.Length < peaks) record.MzArray = new double[peaks];
        if (record.IntensityArray.Length < peaks) record.IntensityArray = new double[peaks];

        // Summed rather than taken from the TIC cvParam, because that is what the Python
        // matcher did and log_tic and tic_injection_time are defined on it.
        double summed = 0;
        for (int i = 0; i < peaks; i++)
        {
            record.MzArray[i] = mz.Data[i];
            double value = intensity.Data[i];
            record.IntensityArray[i] = value;
            summed += value;
        }

        record.PeakCount = peaks;
        record.SummedIntensity = summed;
        record.AcquisitionStartTime = AcquisitionStartTime;
        record.AbsoluteTime = (AcquisitionStartTime ?? 0) + (record.RetentionTime * 60.0);
        return true;
    }

    /// <summary>
    /// A time cvParam in minutes, honouring the unit it declares.
    /// </summary>
    /// <remarks>
    /// Vendors differ: Thermo records scan start time in minutes, Bruker in seconds. Reading
    /// the value and assuming minutes made a 64-minute diaPASEF run look like 64 hours, which
    /// would have gone into the absolute_time feature and out again as noise. An absent or
    /// unrecognized unit is treated as minutes, which is mzML's default.
    /// </remarks>
    private static double Minutes(CVParam? param)
    {
        if (param is null) return 0.0;

        double value = param;
        return param.Units switch
        {
            CVID.UO_second => value / 60.0,
            CVID.UO_millisecond => value / 60_000.0,
            _ => value,
        };
    }

    /// <summary>
    /// Works out which analyzer recorded the MS2 spectra.
    /// </summary>
    /// <remarks>
    /// Read from the first MS2 spectrum's own configuration rather than the run default,
    /// because on a hybrid instrument those differ: an Orbitrap Astral file names the
    /// orbitrap as the run default, since that takes the MS1 survey, and points only its MS2
    /// spectra at the Astral analyzer. MS2 is what MARS calibrates, so that is what decides.
    /// </remarks>
    private static MassAnalyzerClass DetectAnalyzer(MSData msd, ISpectrumList spectra)
    {
        for (int i = 0; i < spectra.Count; i++)
        {
            Spectrum spectrum = spectra.GetSpectrum(i, DetailLevel.FullMetadata);
            if (spectrum.Params.CvParamValueOrDefault(CVID.MS_ms_level, 0) != 2) continue;

            Scan? scan = spectrum.ScanList.Scans.Count > 0 ? spectrum.ScanList.Scans[0] : null;
            InstrumentConfiguration? configuration =
                scan?.InstrumentConfiguration ?? msd.Run.DefaultInstrumentConfiguration;

            MassAnalyzerClass analyzer = Classify(configuration);
            if (analyzer != MassAnalyzerClass.Unknown) return analyzer;

            // The configuration did not settle it. Thermo's filter string does: ITMS, FTMS
            // and ASTMS name the analyzer at the front of every filter.
            return MassAnalyzers.ClassifyFilterString(scan?.CvParam(CVID.MS_filter_string).Value);
        }

        // No MS2 at all. Fall back to the run default so that a QC pass over an MS1-only file
        // still reports on the right scale.
        return Classify(msd.Run.DefaultInstrumentConfiguration);
    }

    /// <summary>
    /// Classifies one instrument configuration by its measuring analyzer - the highest-order
    /// component that is not the isolating quadrupole.
    /// </summary>
    private static MassAnalyzerClass Classify(InstrumentConfiguration? configuration)
    {
        if (configuration is null) return MassAnalyzerClass.Unknown;

        var analyzers = new List<(int Order, string Accession)>();
        foreach (Component component in configuration.ComponentList)
        {
            if (component.Type != ComponentType.Analyzer) continue;
            foreach (CVParam param in component.CVParams)
            {
                // pwiz identifies terms by enum; MARS matches on the accession string, which
                // is what the mzML actually carries.
                string accession = CvLookup.CvTermInfo(param.Cvid).Id;
                analyzers.Add((component.Order, accession));
            }
        }

        return MassAnalyzers.Classify(MassAnalyzers.MeasuringAnalyzer(analyzers));
    }

    /// <summary>Run start as a Unix timestamp, for the absolute_time feature.</summary>
    private static double? StartTimeOf(MSData msd)
    {
        string? stamp = msd.Run.StartTimeStamp;
        if (string.IsNullOrEmpty(stamp)) return null;

        return DateTimeOffset.TryParse(
            stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed.ToUnixTimeMilliseconds() / 1000.0
            : null;
    }

    /// <summary>A Thermo .raw is a file; other vendors use a directory.</summary>
    private static long LengthOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;

            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
            return total;
        }
        catch (IOException)
        {
            return 0;
        }
    }

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
