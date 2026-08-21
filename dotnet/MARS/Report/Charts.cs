// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// The four chart types the QC report uses.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace MARS.Report;

/// <summary>Chart rendering for the QC report. Every method returns a standalone SVG.</summary>
public static class Charts
{
    private const int Width = 760;
    private const int Height = 340;
    private const int Left = 66;
    private const int Right = 18;
    private const int Top = 34;
    private const int Bottom = 46;

    private const string Grid = "var(--grid)";
    private const string Axis0 = "var(--axis)";
    private const string Muted = "var(--muted)";
    private const string Before = "#d2695a";
    private const string After = "#3f7fbf";

    /// <summary>
    /// Histogram of the mass error before and after correction, overlaid.
    ///
    /// This is the headline figure: if the after distribution is not visibly narrower than
    /// the before one, nothing else in the report matters.
    /// </summary>
    public static string ErrorHistogram(
        ReadOnlySpan<double> before, ReadOnlySpan<double> after, string unit, int bins = 160)
    {
        var svg = new Svg(Width, Height);
        if (before.Length == 0) return Empty(svg, "No matched fragments.");

        // A few extreme rows should not squash the informative part of the axis into a
        // sliver, so bound the range at a high percentile of the uncorrected error.
        double limit = SymmetricLimit(before, 0.995);
        var x = new Axis(-limit, limit, Left, Width - Right);

        int[] beforeCounts = Bin(before, -limit, limit, bins);
        int[] afterCounts = after.Length > 0 ? Bin(after, -limit, limit, bins) : Array.Empty<int>();

        int peak = 0;
        foreach (int c in beforeCounts) peak = Math.Max(peak, c);
        foreach (int c in afterCounts) peak = Math.Max(peak, c);
        var y = new Axis(0, peak <= 0 ? 1 : peak, Top, Height - Bottom, invert: true);

        Frame(svg, x, y, $"mass error ({unit})", "fragments");

        DrawBars(svg, beforeCounts, x, y, -limit, limit, Before, 0.55);
        if (afterCounts.Length > 0) DrawBars(svg, afterCounts, x, y, -limit, limit, After, 0.55);

        // Zero is the whole point of the figure; make it unmissable.
        double zero = x.Map(0);
        svg.Line(zero, Top, zero, Height - Bottom, Axis0, 1, "3 3");

        Legend(svg, after.Length > 0);
        return svg.ToString();
    }

    /// <summary>
    /// Median mass error over retention time and fragment m/z. Structure here is what
    /// tells you the error is systematic rather than random, and therefore correctable.
    /// </summary>
    public static string ErrorHeatmap(
        ReadOnlySpan<double> retentionTime, ReadOnlySpan<double> fragmentMz,
        ReadOnlySpan<double> error, string unit, string title, int xBins = 60, int yBins = 44)
    {
        var svg = new Svg(Width, Height);
        if (error.Length == 0) return Empty(svg, "No matched fragments.");

        (double rtMin, double rtMax) = Range(retentionTime);
        (double mzMin, double mzMax) = Range(fragmentMz);
        var x = new Axis(rtMin, rtMax, Left, Width - Right);
        var y = new Axis(mzMin, mzMax, Top, Height - Bottom, invert: true);

        // Median per cell, not mean: a handful of mismatched peaks in a cell would drag a
        // mean far enough to invent structure that is not there.
        var cells = new List<double>[xBins * yBins];
        for (int i = 0; i < error.Length; i++)
        {
            int cx = Bucket(retentionTime[i], rtMin, rtMax, xBins);
            int cy = Bucket(fragmentMz[i], mzMin, mzMax, yBins);
            if (cx < 0 || cy < 0) continue;
            int index = (cy * xBins) + cx;
            (cells[index] ??= new List<double>()).Add(error[i]);
        }

        double scale = SymmetricLimit(error, 0.98);
        var pixels = new byte[xBins * yBins * 3];
        FillBackground(pixels);
        for (int i = 0; i < cells.Length; i++)
        {
            List<double>? values = cells[i];
            if (values is null || values.Count == 0) continue;
            values.Sort();
            // The buffer is top-down; the grid counts up from the axis.
            int cy = i / xBins, cx = i % xBins;
            SetPixel(pixels, xBins, cx, yBins - 1 - cy, Diverging(values[values.Count / 2], scale));
        }

        Raster(svg, pixels, xBins, yBins);

        Frame(svg, x, y, "retention time (min)", "fragment m/z", drawGrid: false);
        svg.Text(Left, 20, title, size: 12, bold: true);
        ColorBar(svg, scale, unit);
        return svg.ToString();
    }

    /// <summary>
    /// Mass error against one feature, as a binned density with the median error per
    /// column drawn over it. The line is the part worth reading: it is the trend the model
    /// has to capture.
    /// </summary>
    public static string FeatureVersusError(
        ReadOnlySpan<double> feature, ReadOnlySpan<double> before, ReadOnlySpan<double> after,
        string featureName, string unit, int xBins = 90, int yBins = 60)
    {
        var svg = new Svg(Width, Height);
        if (feature.Length == 0) return Empty(svg, "No matched fragments.");

        double xLow = Percentile(feature, 0.002);
        double xHigh = Percentile(feature, 0.998);
        double limit = SymmetricLimit(before, 0.99);
        var x = new Axis(xLow, xHigh, Left, Width - Right);
        var y = new Axis(-limit, limit, Top, Height - Bottom, invert: true);

        var counts = new int[xBins * yBins];
        int densest = 0;
        for (int i = 0; i < feature.Length; i++)
        {
            int cx = Bucket(feature[i], xLow, xHigh, xBins);
            int cy = Bucket(before[i], -limit, limit, yBins);
            if (cx < 0 || cy < 0) continue;
            int index = (cy * xBins) + cx;
            densest = Math.Max(densest, ++counts[index]);
        }

        var pixels = new byte[xBins * yBins * 3];
        FillBackground(pixels);
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            // Log scaling, because peak density in a scatter of this kind runs orders of
            // magnitude above the tails and a linear ramp would show one dark blob.
            int cy = i / xBins, cx = i % xBins;
            SetPixel(pixels, xBins, cx, yBins - 1 - cy, Density(Math.Log(1 + counts[i]) / Math.Log(1 + densest)));
        }

        Raster(svg, pixels, xBins, yBins);

        Frame(svg, x, y, featureName, $"mass error ({unit})", drawGrid: false);
        svg.Line(Left, y.Map(0), Width - Right, y.Map(0), Axis0, 1, "3 3");

        MedianTrend(svg, feature, before, x, y, xLow, xHigh, xBins, Before);
        if (after.Length == before.Length)
            MedianTrend(svg, feature, after, x, y, xLow, xHigh, xBins, After);

        Legend(svg, after.Length == before.Length, "median before", "median after");
        return svg.ToString();
    }

    /// <summary>Permutation importance per feature, largest first.</summary>
    public static string FeatureImportance(IReadOnlyList<string> names, IReadOnlyList<double> importance)
    {
        int rows = Math.Min(names.Count, importance.Count);
        int height = Math.Max(180, 40 + (rows * 22));
        var svg = new Svg(Width, height);
        if (rows == 0) return Empty(svg, "Importance was not computed.");

        var order = new int[rows];
        for (int i = 0; i < rows; i++) order[i] = i;
        Array.Sort(order, (a, b) => importance[b].CompareTo(importance[a]));

        double max = 0;
        foreach (double value in importance) max = Math.Max(max, value);
        if (max <= 0) max = 1;

        const int labelWidth = 190;
        double barLeft = labelWidth + 10;
        double barSpan = Width - barLeft - 60;

        for (int i = 0; i < rows; i++)
        {
            int index = order[i];
            double y = 26 + (i * 22);
            svg.Text(labelWidth, y + 11, names[index], anchor: "end", size: 11);
            double barWidth = barSpan * (importance[index] / max);
            svg.Rect(barLeft, y + 2, barWidth, 14, After);
            svg.Text(
                barLeft + barWidth + 6, y + 13,
                importance[index].ToString("0.000", CultureInfo.InvariantCulture),
                size: 10, fill: Muted);
        }

        return svg.ToString();
    }

    // ---- helpers ---------------------------------------------------------------------

    private static string Empty(Svg svg, string message)
    {
        svg.Text(svg.Width / 2.0, svg.Height / 2.0, message, anchor: "middle", fill: Muted);
        return svg.ToString();
    }

    private static void Frame(Svg svg, Axis x, Axis y, string xLabel, string yLabel, bool drawGrid = true)
    {
        foreach (double tick in y.Ticks(6))
        {
            double py = y.Map(tick);
            if (drawGrid) svg.Line(Left, py, Width - Right, py, Grid, 1);
            svg.Text(Left - 8, py + 3.5, y.Format(tick), anchor: "end", size: 10, fill: Muted);
        }

        foreach (double tick in x.Ticks(7))
        {
            double px = x.Map(tick);
            if (drawGrid) svg.Line(px, Top, px, Height - Bottom, Grid, 1);
            svg.Text(px, Height - Bottom + 15, x.Format(tick), anchor: "middle", size: 10, fill: Muted);
        }

        svg.Line(Left, Height - Bottom, Width - Right, Height - Bottom, Axis0, 1);
        svg.Line(Left, Top, Left, Height - Bottom, Axis0, 1);
        svg.Text(Left + ((Width - Right - Left) / 2.0), Height - 8, xLabel, anchor: "middle", size: 11);
        svg.Text(14, Top + ((Height - Bottom - Top) / 2.0), yLabel, anchor: "middle", size: 11, rotate: -90);
    }

    private static void DrawBars(
        Svg svg, int[] counts, Axis x, Axis y, double low, double high, string fill, double opacity)
    {
        double step = (high - low) / counts.Length;
        double baseline = y.Map(0);
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            double x0 = x.Map(low + (i * step));
            double x1 = x.Map(low + ((i + 1) * step));
            double top = y.Map(counts[i]);
            svg.Rect(x0, top, Math.Max(0.6, x1 - x0), baseline - top, fill,
                $"fill-opacity=\"{opacity.ToString(CultureInfo.InvariantCulture)}\"");
        }
    }

    private static void MedianTrend(
        Svg svg, ReadOnlySpan<double> feature, ReadOnlySpan<double> error,
        Axis x, Axis y, double low, double high, int bins, string stroke)
    {
        var buckets = new List<double>[bins];
        for (int i = 0; i < feature.Length; i++)
        {
            int b = Bucket(feature[i], low, high, bins);
            if (b < 0) continue;
            (buckets[b] ??= new List<double>()).Add(error[i]);
        }

        var points = new List<(double X, double Y)>();
        double step = (high - low) / bins;
        for (int b = 0; b < bins; b++)
        {
            List<double>? values = buckets[b];
            // A bucket with a handful of rows is noise, and a trend line drawn through noise
            // reads as signal. Require enough to make the median mean something.
            if (values is null || values.Count < 20) continue;
            values.Sort();
            points.Add((x.Map(low + ((b + 0.5) * step)), y.Map(values[values.Count / 2])));
        }

        svg.Polyline(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points), stroke, 2);
    }

    private static void Legend(Svg svg, bool hasAfter, string beforeLabel = "before", string afterLabel = "after")
    {
        double x = Left + 4;
        svg.Rect(x, 14, 10, 10, Before);
        svg.Text(x + 15, 23, beforeLabel, size: 11);
        if (!hasAfter) return;
        svg.Rect(x + 90, 14, 10, 10, After);
        svg.Text(x + 105, 23, afterLabel, size: 11);
    }

    private static void ColorBar(Svg svg, double scale, string unit)
    {
        const int steps = 40;
        double barWidth = 150.0;
        double x0 = Width - Right - barWidth;
        for (int i = 0; i < steps; i++)
        {
            double t = ((i / (double)(steps - 1)) * 2) - 1;
            (byte r, byte g, byte b) = Diverging(t * scale, scale);
            svg.Rect(x0 + (i * (barWidth / steps)), 10, (barWidth / steps) + 0.5, 9, $"rgb({r},{g},{b})");
        }

        svg.Text(x0 - 6, 18, $"-{scale.ToString("0.###", CultureInfo.InvariantCulture)}", anchor: "end", size: 9, fill: Muted);
        svg.Text(Width - Right + 2, 18, $"+{scale.ToString("0.###", CultureInfo.InvariantCulture)} {unit}", anchor: "start", size: 9, fill: Muted);
    }

    /// <summary>Places a density buffer in the plot area, scaled to fill it.</summary>
    private static void Raster(Svg svg, byte[] pixels, int xBins, int yBins) =>
        svg.Image(Left, Top, Width - Right - Left, Height - Bottom - Top, Png.DataUri(pixels, xBins, yBins));

    /// <summary>
    /// Fills the buffer with the panel background, so an empty cell is not black.
    /// </summary>
    /// <remarks>
    /// A near-white constant rather than the theme's background variable: this is a raster,
    /// so it cannot follow the reader's colour scheme the way the vector layers do. The
    /// density ramps run light-to-dark, which reads correctly on either theme.
    /// </remarks>
    private static void FillBackground(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 252;
            pixels[i + 1] = 252;
            pixels[i + 2] = 253;
        }
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, (byte R, byte G, byte B) color)
    {
        int offset = ((y * width) + x) * 3;
        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
    }

    /// <summary>Blue-white-red, so the sign of the error is readable and zero is blank.</summary>
    private static (byte R, byte G, byte B) Diverging(double value, double scale)
    {
        // Quantized to 32 steps, finer than the eye resolves on a diverging ramp, and a
        // limited palette is what lets deflate compress the panel down to a few kilobytes.
        double t = Math.Round(Math.Clamp(value / scale, -1, 1) * 32) / 32;
        if (t < 0)
        {
            double k = -t;
            return ((byte)(255 - (k * 190)), (byte)(255 - (k * 130)), 255);
        }

        return (255, (byte)(255 - (t * 150)), (byte)(255 - (t * 165)));
    }

    private static (byte R, byte G, byte B) Density(double intensity)
    {
        double t = Math.Round(Math.Clamp(intensity, 0, 1) * 24) / 24;
        return ((byte)(238 - (t * 200)), (byte)(242 - (t * 150)), (byte)(248 - (t * 60)));
    }

    private static int Bucket(double value, double low, double high, int bins)
    {
        if (double.IsNaN(value) || value < low || value > high) return -1;
        int b = (int)((value - low) / (high - low) * bins);
        return Math.Clamp(b, 0, bins - 1);
    }

    private static int[] Bin(ReadOnlySpan<double> values, double low, double high, int bins)
    {
        var counts = new int[bins];
        foreach (double value in values)
        {
            int b = Bucket(value, low, high, bins);
            if (b >= 0) counts[b]++;
        }

        return counts;
    }

    private static (double Min, double Max) Range(ReadOnlySpan<double> values)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (double value in values)
        {
            if (double.IsNaN(value)) continue;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        return double.IsInfinity(min) ? (0, 1) : (min, max);
    }

    private static double SymmetricLimit(ReadOnlySpan<double> values, double quantile)
    {
        double limit = Math.Max(
            Math.Abs(Percentile(values, 1 - quantile)),
            Math.Abs(Percentile(values, quantile)));
        return limit > 0 ? limit : 1;
    }

    private static double Percentile(ReadOnlySpan<double> values, double quantile)
    {
        if (values.Length == 0) return 0;

        // Sampled rather than sorting nine million rows to place an axis limit. The stride
        // is deterministic, so the same input always yields the same figure.
        const int cap = 20000;
        int stride = Math.Max(1, values.Length / cap);
        var sample = new List<double>(Math.Min(cap + 1, values.Length));
        for (int i = 0; i < values.Length; i += stride)
        {
            if (!double.IsNaN(values[i])) sample.Add(values[i]);
        }

        if (sample.Count == 0) return 0;
        sample.Sort();
        int index = Math.Clamp((int)(quantile * (sample.Count - 1)), 0, sample.Count - 1);
        return sample[index];
    }
}
