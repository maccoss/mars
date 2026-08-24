// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/matching.py (find_most_intense_peak, sum_intensity_in_range) and
// the vectorized range-sum helper in mars/calibration.py.

using System;

namespace MARS.Core;

public static class PeakSearch
{
    /// <summary>
    /// numpy searchsorted(side="left"): first index whose value is greater than or equal
    /// to <paramref name="value"/>.
    /// </summary>
    public static int LowerBound(ReadOnlySpan<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// numpy searchsorted(side="right"): first index whose value is strictly greater than
    /// <paramref name="value"/>.
    /// </summary>
    public static int UpperBound(ReadOnlySpan<double> sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (sorted[mid] <= value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Finds the most intense peak within tolerance of <paramref name="targetMz"/>.
    /// Ties go to the lowest m/z, matching numpy argmax.
    /// </summary>
    /// <param name="toleranceTh">Absolute tolerance in Th; ignored when tolerancePpm is set.</param>
    /// <param name="tolerancePpm">Relative tolerance in ppm; overrides toleranceTh when positive.</param>
    /// <returns>True when a peak was found, with its m/z and intensity.</returns>
    public static bool TryFindMostIntensePeak(
        double targetMz,
        ReadOnlySpan<double> mz,
        ReadOnlySpan<double> intensity,
        double toleranceTh,
        double minIntensity,
        double tolerancePpm,
        out double observedMz,
        out double observedIntensity)
    {
        observedMz = 0;
        observedIntensity = 0;
        if (mz.Length == 0) return false;

        double tolerance = tolerancePpm > 0 ? targetMz * tolerancePpm / 1e6 : toleranceTh;
        int low = LowerBound(mz, targetMz - tolerance);
        int high = UpperBound(mz, targetMz + tolerance);
        if (low >= high) return false;

        int best = -1;
        double bestIntensity = double.NegativeInfinity;
        for (int i = low; i < high; i++)
        {
            double value = intensity[i];
            if (minIntensity > 0 && value < minIntensity) continue;
            if (value > bestIntensity)
            {
                bestIntensity = value;
                best = i;
            }
        }

        if (best < 0) return false;

        observedMz = mz[best];
        observedIntensity = intensity[best];
        return true;
    }

    /// <summary>
    /// Sums intensities of peaks in the half-open interval (lowMz, highMz]: peaks exactly at
    /// lowMz are excluded, peaks exactly at highMz are included. Matches the Python
    /// searchsorted(side="right") on both bounds.
    /// </summary>
    public static double SumIntensityInRange(
        ReadOnlySpan<double> mz,
        ReadOnlySpan<double> intensity,
        double lowMz,
        double highMz)
    {
        if (mz.Length == 0) return 0.0;

        int low = UpperBound(mz, lowMz);
        int high = UpperBound(mz, highMz);
        if (low >= high) return 0.0;

        double sum = 0.0;
        for (int i = low; i < high; i++) sum += intensity[i];
        return sum;
    }

    /// <summary>
    /// Computes, for every peak i, the summed intensity in (mz[i] + low, mz[i] + high].
    /// <para>
    /// Both interval ends are monotone in i because mz is ascending, so the two bounds
    /// advance without ever moving backwards and the whole sweep is linear in the number
    /// of peaks plus the total window occupancy. The per-window slice is summed directly
    /// rather than differenced out of a prefix sum, so the result is bit-identical to the
    /// training path's <see cref="SumIntensityInRange"/>.
    /// </para>
    /// </summary>
    public static void ComputeNeighborWindow(
        ReadOnlySpan<double> mz,
        ReadOnlySpan<double> intensity,
        double low,
        double high,
        Span<double> destination)
    {
        int n = mz.Length;
        int lowIdx = 0, highIdx = 0;
        for (int i = 0; i < n; i++)
        {
            double lowBound = mz[i] + low;
            double highBound = mz[i] + high;

            while (lowIdx < n && mz[lowIdx] <= lowBound) lowIdx++;
            if (highIdx < lowIdx) highIdx = lowIdx;
            while (highIdx < n && mz[highIdx] <= highBound) highIdx++;

            double sum = 0.0;
            for (int j = lowIdx; j < highIdx; j++) sum += intensity[j];
            destination[i] = sum;
        }
    }
}
