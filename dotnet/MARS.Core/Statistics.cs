// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;

namespace MARS.Core;

/// <summary>Summary of a mass-error distribution, in Th.</summary>
public readonly struct ErrorSummary
{
    public required int Count { get; init; }

    public required double Mean { get; init; }

    public required double Median { get; init; }

    /// <summary>Sample standard deviation, matching pandas Series.std (ddof = 1).</summary>
    public required double StdDev { get; init; }

    /// <summary>Mean absolute error.</summary>
    public required double Mae { get; init; }

    /// <summary>Root mean square.</summary>
    public required double Rms { get; init; }

    /// <summary>Median absolute deviation about the median.</summary>
    public required double Mad { get; init; }
}

public static class MarsStatistics
{
    public static double Mean(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return sum / values.Length;
    }

    /// <summary>Sample standard deviation with ddof = 1, matching pandas.</summary>
    public static double StdDev(ReadOnlySpan<double> values)
    {
        if (values.Length < 2) return double.NaN;
        double mean = Mean(values);
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / (values.Length - 1));
    }

    public static double Rms(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i] * values[i];
        return Math.Sqrt(sum / values.Length);
    }

    public static double MeanAbsolute(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += Math.Abs(values[i]);
        return sum / values.Length;
    }

    /// <summary>
    /// Linearly interpolated median, matching numpy. Sorts a copy, so the caller's array
    /// is left alone.
    /// </summary>
    public static double Median(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        return MedianOfSorted(copy);
    }

    public static double MedianOfSorted(double[] sorted)
    {
        int n = sorted.Length;
        if (n == 0) return double.NaN;
        int mid = n / 2;
        return (n & 1) == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    /// <summary>Median absolute deviation about the median.</summary>
    public static double MedianAbsoluteDeviation(ReadOnlySpan<double> values)
    {
        if (values.Length == 0) return double.NaN;
        var copy = values.ToArray();
        Array.Sort(copy);
        double median = MedianOfSorted(copy);
        for (int i = 0; i < copy.Length; i++) copy[i] = Math.Abs(copy[i] - median);
        Array.Sort(copy);
        return MedianOfSorted(copy);
    }

    public static ErrorSummary Summarize(ReadOnlySpan<double> values) => new()
    {
        Count = values.Length,
        Mean = Mean(values),
        Median = Median(values),
        StdDev = StdDev(values),
        Mae = MeanAbsolute(values),
        Rms = Rms(values),
        Mad = MedianAbsoluteDeviation(values),
    };
}
