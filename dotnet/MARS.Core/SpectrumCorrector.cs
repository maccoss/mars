// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Inference side of the calibration, ported from MzCalibrator.create_calibration_function
// in mars/calibration.py.

using System;

namespace MARS.Core;

/// <summary>What to do when a per-peak correction would reorder adjacent peaks.</summary>
public enum MonotonicityPolicy
{
    /// <summary>
    /// Nudge the offending peak up to the next representable double above its predecessor.
    /// Keeps the array strictly ascending with the smallest possible perturbation.
    /// </summary>
    ClampAscending,

    /// <summary>Leave the whole spectrum uncorrected and count it.</summary>
    RevertSpectrum,

    /// <summary>Write the corrected values as-is. Produces an unsorted m/z array.</summary>
    Allow,
}

public sealed class CorrectionOptions
{
    /// <summary>Do not correct spectra whose isolation window is wider than this, in Th.</summary>
    public double? MaxIsolationWindowWidth { get; set; }

    public MonotonicityPolicy Monotonicity { get; set; } = MonotonicityPolicy.ClampAscending;

    /// <summary>
    /// Reproduce two inconsistencies in the Python implementation, for A/B comparison
    /// against its output. Both change which side of a tree split an inference row lands on.
    /// <list type="bullet">
    /// <item>log_tic and tic_injection_time are computed from the mzML total ion current
    /// cvParam at correction time, but from the summed intensity array at training time.
    /// The two differ on Thermo centroided data.</item>
    /// <item>absolute_time is fed in as a raw Unix timestamp at correction time, but is
    /// re-based to the earliest acquisition before training, so every inference row sits
    /// far above the largest value the model ever saw.</item>
    /// </list>
    /// </summary>
    public bool PythonCompatibility { get; set; }
}

/// <summary>Per-thread scratch for correcting a spectrum. Reused across spectra.</summary>
public sealed class CorrectionWorkspace
{
    private double[] _features = Array.Empty<double>();
    private double[] _neighbors = Array.Empty<double>();
    private double[] _corrections = Array.Empty<double>();
    private double[] _row = Array.Empty<double>();

    public double[] Features => _features;

    public double[] Neighbors => _neighbors;

    public double[] Corrections => _corrections;

    public double[] Row => _row;

    public void EnsureCapacity(int peakCount, int featureCount)
    {
        int needed = peakCount * featureCount;
        if (_features.Length < needed) _features = new double[Math.Max(needed, 1024)];
        if (_neighbors.Length < peakCount * MarsFeatures.NeighborWindows.Length)
            _neighbors = new double[Math.Max(peakCount * MarsFeatures.NeighborWindows.Length, 1024)];
        if (_corrections.Length < peakCount) _corrections = new double[Math.Max(peakCount, 1024)];
        if (_row.Length < featureCount) _row = new double[featureCount];
    }
}

public sealed class SpectrumCorrectionResult
{
    public bool Corrected { get; init; }

    public int MonotonicityFixes { get; init; }

    public bool Reverted { get; init; }
}

/// <summary>Applies a trained <see cref="MzCalibrator"/> to the peaks of a spectrum.</summary>
public sealed class SpectrumCorrector
{
    private readonly MzCalibrator _calibrator;
    private readonly CorrectionOptions _options;
    private readonly MarsFeature[] _features;

    public SpectrumCorrector(MzCalibrator calibrator, CorrectionOptions options)
    {
        _calibrator = calibrator;
        _options = options;
        _features = calibrator.Features.Features;
    }

    public bool ShouldCorrect(SpectrumRecord spectrum)
    {
        if (spectrum.MsLevel != 2) return false;
        if (spectrum.PeakCount == 0) return false;
        if (_options.MaxIsolationWindowWidth is double maxWidth && spectrum.IsolationWindowWidth > maxWidth)
            return false;
        return true;
    }

    /// <summary>
    /// Writes corrected m/z values for every peak into <paramref name="destination"/>.
    /// </summary>
    public SpectrumCorrectionResult Correct(
        SpectrumRecord spectrum,
        TemperatureSet? temperatures,
        CorrectionWorkspace workspace,
        Span<double> destination)
    {
        int n = spectrum.PeakCount;
        ReadOnlySpan<double> mz = spectrum.Mz;
        ReadOnlySpan<double> intensity = spectrum.Intensity;

        if (!ShouldCorrect(spectrum))
        {
            mz.CopyTo(destination);
            return new SpectrumCorrectionResult { Corrected = false };
        }

        int nFeat = _features.Length;
        workspace.EnsureCapacity(n, nFeat);
        double[] features = workspace.Features;
        double[] neighbors = workspace.Neighbors;

        double injectionTime = spectrum.InjectionTime ?? 0.0;

        // Training computes the TIC features from the summed intensity array; do the same
        // here so inference rows land on the scale the model was fitted on.
        double tic = _options.PythonCompatibility ? spectrum.ReportedTic : spectrum.SummedIntensity;
        double logTic = Math.Log10(Math.Max(tic, 1.0));
        double ticInjectionTime = tic * injectionTime;

        double absoluteTime = _options.PythonCompatibility
            ? spectrum.AbsoluteTime
            : spectrum.AbsoluteTime - _calibrator.AbsoluteTimeOffset;

        double rfa2 = temperatures?.Rfa2 is { } a ? a.TemperatureAt(spectrum.RetentionTime) : 0.0;
        double rfc2 = temperatures?.Rfc2 is { } c ? c.TemperatureAt(spectrum.RetentionTime) : 0.0;
        if (double.IsNaN(rfa2)) rfa2 = 0.0;
        if (double.IsNaN(rfc2)) rfc2 = 0.0;

        bool needNeighbors = _calibrator.Features.NeedsNeighborDensity && injectionTime > 0;
        if (needNeighbors)
        {
            for (int w = 0; w < MarsFeatures.NeighborWindows.Length; w++)
            {
                (double low, double high) = MarsFeatures.NeighborWindows[w];
                PeakSearch.ComputeNeighborWindow(mz, intensity, low, high, neighbors.AsSpan(w * n, n));
            }
        }

        for (int i = 0; i < n; i++)
        {
            double peakMz = mz[i];
            double peakIntensity = intensity[i];
            double fragmentIons = peakIntensity * injectionTime;
            int rowStart = i * nFeat;

            for (int j = 0; j < nFeat; j++)
            {
                features[rowStart + j] = FeatureValue(
                    _features[j], peakMz, peakIntensity, fragmentIons, injectionTime,
                    logTic, ticInjectionTime, absoluteTime, spectrum.PrecursorMzCenter,
                    rfa2, rfc2, neighbors, needNeighbors, i, n);
            }
        }

        double[] corrections = workspace.Corrections;
        double[] row = workspace.Row;
        for (int i = 0; i < n; i++)
        {
            Array.Copy(features, i * nFeat, row, 0, nFeat);
            corrections[i] = _calibrator.PredictDelta(row);
        }

        int fixes = 0;
        double previous = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            double corrected = mz[i] - corrections[i];
            if (corrected <= previous)
            {
                fixes++;
                if (_options.Monotonicity == MonotonicityPolicy.RevertSpectrum)
                {
                    mz.CopyTo(destination);
                    return new SpectrumCorrectionResult
                    {
                        Corrected = false,
                        Reverted = true,
                        MonotonicityFixes = fixes,
                    };
                }

                if (_options.Monotonicity == MonotonicityPolicy.ClampAscending)
                    corrected = Math.BitIncrement(previous);
            }

            destination[i] = corrected;
            previous = corrected;
        }

        return new SpectrumCorrectionResult { Corrected = true, MonotonicityFixes = fixes };
    }

    private static double FeatureValue(
        MarsFeature feature,
        double peakMz,
        double peakIntensity,
        double fragmentIons,
        double injectionTime,
        double logTic,
        double ticInjectionTime,
        double absoluteTime,
        double precursorMz,
        double rfa2,
        double rfc2,
        double[] neighbors,
        bool haveNeighbors,
        int peakIndex,
        int peakCount)
    {
        switch (feature)
        {
            case MarsFeature.PrecursorMz:
                return precursorMz;
            case MarsFeature.FragmentMz:
                // Training uses the theoretical library m/z here; correction has only the
                // observed value. They differ by at most the matching tolerance.
                return peakMz;
            case MarsFeature.LogTic:
                return logTic;
            case MarsFeature.LogIntensity:
                return Math.Log10(Math.Max(peakIntensity, 1.0));
            case MarsFeature.AbsoluteTime:
                return absoluteTime;
            case MarsFeature.InjectionTime:
                return injectionTime;
            case MarsFeature.TicInjectionTime:
                return ticInjectionTime;
            case MarsFeature.FragmentIons:
                return fragmentIons;
            default:
                break;
        }

        if (feature == MarsFeature.Rfa2Temp) return rfa2;
        if (feature == MarsFeature.Rfc2Temp) return rfc2;

        for (int w = 0; w < MarsFeatures.NeighborFeatures.Length; w++)
        {
            if (MarsFeatures.NeighborFeatures[w] == feature)
                return haveNeighbors ? neighbors[(w * peakCount) + peakIndex] * injectionTime : 0.0;

            if (MarsFeatures.RatioFeatures[w] == feature)
            {
                if (!haveNeighbors || fragmentIons <= 0) return 0.0;
                return neighbors[(w * peakCount) + peakIndex] * injectionTime / fragmentIons;
            }
        }

        return 0.0;
    }
}
