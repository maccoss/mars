// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/matching.py (match_library_to_spectra).

using System;
using System.Collections.Generic;

namespace MARS.Core;

public sealed class MatchOptions
{
    /// <summary>Absolute matching tolerance in Th. Ignored when <see cref="TolerancePpm"/> is positive.</summary>
    public double MzToleranceTh { get; set; } = 0.3;

    /// <summary>Relative matching tolerance in ppm. Overrides <see cref="MzToleranceTh"/> when positive.</summary>
    public double TolerancePpm { get; set; }

    /// <summary>Minimum observed peak intensity for a peak to be usable as a training row.</summary>
    public double MinIntensity { get; set; } = 500.0;

    /// <summary>Skip spectra whose isolation window is wider than this, in Th. Null disables the filter.</summary>
    public double? MaxIsolationWindowWidth { get; set; }

    /// <summary>Minimum retention time to process, in minutes.</summary>
    public double? MinRetentionTime { get; set; }

    /// <summary>Maximum retention time to process, in minutes.</summary>
    public double? MaxRetentionTime { get; set; }
}

public sealed class MatchStatistics
{
    public long SpectraSeen;

    public long SpectraMatched;

    public long FragmentsMatched;

    public long CandidateFragmentsConsidered;

    public readonly SortedSet<(int Low, int High)> IsolationWindows = new();

    public int UniqueEntriesMatched;
}

/// <summary>
/// Matches library fragments against the peaks of one DIA MS2 spectrum and appends one
/// training row per match.
/// </summary>
public sealed class FragmentMatcher
{
    private readonly SpectralLibrary _library;
    private readonly MatchOptions _options;
    private readonly int[] _order;
    private readonly double[] _sortedPrecursorMz;
    private readonly bool[] _entryMatched;

    public FragmentMatcher(SpectralLibrary library, MatchOptions options)
    {
        _library = library;
        _options = options;
        _order = library.OrderByPrecursorMz();
        _sortedPrecursorMz = new double[_order.Length];
        for (int i = 0; i < _order.Length; i++) _sortedPrecursorMz[i] = library.PrecursorMz[_order[i]];
        _entryMatched = new bool[library.EntryCount];
    }

    public MatchStatistics Statistics { get; } = new();

    /// <summary>
    /// The features a match table must collect for this matcher's output. Temperature
    /// features are only included when a temperature trace is available.
    /// </summary>
    public static MarsFeature[] CollectedFeatures(InjectionTimeUse injectionTime, bool rfa2, bool rfc2)
    {
        var features = new List<MarsFeature>(MarsFeatures.Count)
        {
            MarsFeature.PrecursorMz,
            MarsFeature.FragmentMz,
            MarsFeature.LogTic,
            MarsFeature.LogIntensity,
            MarsFeature.AbsoluteTime,
        };

        // Everything below needs the run to record an injection time, and nothing below needs
        // it to vary. Whether it varies is decided later, from the whole matched column rather
        // than from a sample of the head - see MzCalibrator.SelectFeatures. Collecting a
        // column costs one array; deciding too early costs the feature.
        if (injectionTime != InjectionTimeUse.Absent)
        {
            features.Add(MarsFeature.InjectionTime);
            features.Add(MarsFeature.TicInjectionTime);
            features.Add(MarsFeature.FragmentIons);
            features.AddRange(MarsFeatures.NeighborFeatures);
            features.AddRange(MarsFeatures.RatioFeatures);
        }

        if (rfa2) features.Add(MarsFeature.Rfa2Temp);
        if (rfc2) features.Add(MarsFeature.Rfc2Temp);
        return features.ToArray();
    }

    /// <summary>
    /// Matches one spectrum. Returns the number of rows appended to <paramref name="table"/>.
    /// </summary>
    public int MatchSpectrum(SpectrumRecord spectrum, TemperatureSet? temperatures, MatchTable table)
    {
        Statistics.SpectraSeen++;

        if (_options.MaxIsolationWindowWidth is double maxWidth &&
            spectrum.IsolationWindowWidth > maxWidth)
        {
            return 0;
        }

        if (_options.MinRetentionTime is double minRt && spectrum.RetentionTime < minRt) return 0;
        if (_options.MaxRetentionTime is double maxRt && spectrum.RetentionTime > maxRt) return 0;

        Statistics.IsolationWindows.Add(((int)spectrum.PrecursorMzLow, (int)spectrum.PrecursorMzHigh));

        int first = PeakSearch.LowerBound(_sortedPrecursorMz, spectrum.PrecursorMzLow);
        int last = PeakSearch.UpperBound(_sortedPrecursorMz, spectrum.PrecursorMzHigh);
        if (first >= last) return 0;

        ReadOnlySpan<double> mz = spectrum.Mz;
        ReadOnlySpan<double> intensity = spectrum.Intensity;
        if (mz.Length == 0) return 0;

        double injectionTime = spectrum.InjectionTime ?? double.NaN;
        bool hasInjectionTime = spectrum.InjectionTime.HasValue;

        double logTic = Math.Log10(Math.Max(spectrum.SummedIntensity, 1.0));
        double ticInjectionTime = hasInjectionTime ? spectrum.SummedIntensity * injectionTime : double.NaN;

        double rfa2 = temperatures?.Rfa2 is { } a ? a.TemperatureAt(spectrum.RetentionTime) : double.NaN;
        double rfc2 = temperatures?.Rfc2 is { } c ? c.TemperatureAt(spectrum.RetentionTime) : double.NaN;

        double lowestUsableMz = mz[0] - MaxToleranceAt(mz[0]);
        double highestUsableMz = mz[mz.Length - 1] + MaxToleranceAt(mz[mz.Length - 1]);

        int rowsAdded = 0;
        Span<double> neighbors = stackalloc double[MarsFeatures.NeighborWindows.Length];

        for (int k = first; k < last; k++)
        {
            int entry = _order[k];

            double rtStart = _library.RtStart[entry];
            double rtEnd = _library.RtEnd[entry];
            if (!double.IsNaN(rtStart) && !double.IsNaN(rtEnd))
            {
                if (spectrum.RetentionTime < rtStart || spectrum.RetentionTime > rtEnd) continue;
            }

            int fragmentStart = _library.FragmentStart[entry];
            int fragmentEnd = _library.FragmentStart[entry + 1];

            for (int f = fragmentStart; f < fragmentEnd; f++)
            {
                double expectedMz = _library.FragmentMz[f];
                if (expectedMz <= 0) continue;
                if (expectedMz < lowestUsableMz || expectedMz > highestUsableMz) continue;

                Statistics.CandidateFragmentsConsidered++;

                if (!PeakSearch.TryFindMostIntensePeak(
                        expectedMz, mz, intensity,
                        _options.MzToleranceTh, _options.MinIntensity, _options.TolerancePpm,
                        out double observedMz, out double observedIntensity))
                {
                    continue;
                }

                table.Set(MarsFeature.PrecursorMz, spectrum.PrecursorMzCenter);
                table.Set(MarsFeature.FragmentMz, expectedMz);
                table.Set(MarsFeature.LogTic, logTic);
                table.Set(MarsFeature.LogIntensity, Math.Log10(Math.Max(observedIntensity, 1.0)));
                table.Set(MarsFeature.AbsoluteTime, spectrum.AbsoluteTime);

                if (table.Has(MarsFeature.InjectionTime))
                {
                    table.Set(MarsFeature.InjectionTime, injectionTime);
                    table.Set(MarsFeature.TicInjectionTime, ticInjectionTime);
                }

                if (table.Has(MarsFeature.FragmentIons))
                {
                    double fragmentIons = hasInjectionTime ? observedIntensity * injectionTime : double.NaN;
                    table.Set(MarsFeature.FragmentIons, fragmentIons);

                    for (int w = 0; w < neighbors.Length; w++)
                    {
                        if (!hasInjectionTime)
                        {
                            neighbors[w] = double.NaN;
                            continue;
                        }

                        (double low, double high) = MarsFeatures.NeighborWindows[w];
                        neighbors[w] = PeakSearch.SumIntensityInRange(
                            mz, intensity, expectedMz + low, expectedMz + high) * injectionTime;
                    }

                    for (int w = 0; w < neighbors.Length; w++)
                        table.Set(MarsFeatures.NeighborFeatures[w], neighbors[w]);

                    // The Python implementation leaves the ratios undefined (and therefore
                    // drops the row) when the fragment ion count is not strictly positive.
                    bool ratiosDefined = fragmentIons > 0;
                    for (int w = 0; w < neighbors.Length; w++)
                    {
                        table.Set(
                            MarsFeatures.RatioFeatures[w],
                            ratiosDefined ? neighbors[w] / fragmentIons : double.NaN);
                    }
                }

                if (table.Has(MarsFeature.Rfa2Temp)) table.Set(MarsFeature.Rfa2Temp, rfa2);
                if (table.Has(MarsFeature.Rfc2Temp)) table.Set(MarsFeature.Rfc2Temp, rfc2);

                table.DeltaMz.Add(observedMz - expectedMz);
                table.ObservedIntensity.Add(observedIntensity);
                table.PeptideGroup.Add(_library.PeptideGroup[entry]);

                if (table.KeepDetail)
                {
                    table.ScanNumber!.Add(spectrum.ScanNumber);
                    table.LibraryEntryIndex!.Add(entry);
                    table.FragmentIndex!.Add(f);
                    table.ObservedMz!.Add(observedMz);
                    table.RetentionTime!.Add(spectrum.RetentionTime);
                }

                table.CommitRow();
                rowsAdded++;

                Statistics.FragmentsMatched++;
                if (!_entryMatched[entry])
                {
                    _entryMatched[entry] = true;
                    Statistics.UniqueEntriesMatched++;
                }
            }
        }

        if (rowsAdded > 0) Statistics.SpectraMatched++;
        return rowsAdded;
    }

    private double MaxToleranceAt(double mz) =>
        _options.TolerancePpm > 0 ? mz * _options.TolerancePpm / 1e6 : _options.MzToleranceTh;
}
