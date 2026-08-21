// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Linear axis scaling and tick selection for the QC charts.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MARS.Report;

/// <summary>
/// Maps a data range onto a pixel range and chooses readable tick positions.
/// </summary>
public readonly struct Axis
{
    public Axis(double min, double max, double pixelLow, double pixelHigh, bool invert = false)
    {
        // A degenerate range would divide by zero and put every point in one place. Widen it
        // around the value so a constant column still renders something sensible.
        if (!(max > min))
        {
            double pad = Math.Abs(min) > 0 ? Math.Abs(min) * 0.05 : 0.5;
            min -= pad;
            max += pad;
        }

        Min = min;
        Max = max;
        PixelLow = pixelLow;
        PixelHigh = pixelHigh;
        Invert = invert;
    }

    public double Min { get; }

    public double Max { get; }

    public double PixelLow { get; }

    public double PixelHigh { get; }

    /// <summary>True for the y axis, where larger values are drawn higher up the page.</summary>
    public bool Invert { get; }

    public double Map(double value)
    {
        double fraction = (value - Min) / (Max - Min);
        return Invert
            ? PixelHigh - (fraction * (PixelHigh - PixelLow))
            : PixelLow + (fraction * (PixelHigh - PixelLow));
    }

    /// <summary>
    /// Tick positions at 1, 2 or 5 times a power of ten, which is what makes an axis
    /// readable at a glance rather than a row of arbitrary decimals.
    /// </summary>
    public IReadOnlyList<double> Ticks(int target = 5)
    {
        var ticks = new List<double>();
        double span = Max - Min;
        if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span)) return ticks;

        double rough = span / Math.Max(1, target);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalized = rough / magnitude;
        double step = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        } * magnitude;

        double first = Math.Ceiling(Min / step) * step;
        for (double t = first; t <= Max + (step * 1e-9); t += step)
        {
            // Snap values that are a hair off a round number by accumulated error.
            double snapped = Math.Abs(t) < step * 1e-9 ? 0 : t;
            ticks.Add(snapped);
            if (ticks.Count > 40) break;
        }

        return ticks;
    }

    /// <summary>Formats a tick so the label carries the precision the step needs, and no more.</summary>
    public string Format(double value)
    {
        double span = Max - Min;
        if (span == 0) return value.ToString("0.###", CultureInfo.InvariantCulture);

        double magnitude = Math.Max(Math.Abs(Min), Math.Abs(Max));
        if (magnitude >= 100000 || (magnitude > 0 && magnitude < 0.001))
            return value.ToString("0.##e+0", CultureInfo.InvariantCulture);

        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(span / 5)) + 1);
        decimals = Math.Min(decimals, 6);
        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
