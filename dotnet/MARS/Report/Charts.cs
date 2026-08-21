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
    /// Median mass error over retention time and fragment m/z, before and after correction,
    /// side by side on a shared color scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Side by side sharing one scale is the point: the question is whether the structure in
    /// the left panel is gone from the right, and two panels scaled independently cannot
    /// answer it - a faint residue would be stretched to look like the original.
    /// </para>
    /// <para>
    /// The scale comes from the cell medians actually drawn, not from the raw per-row error.
    /// A cell median over hundreds of rows is far tighter than the rows it averages, so
    /// scaling it against the raw spread washes the field out to near-white, which is
    /// exactly what an earlier version of this chart did.
    /// </para>
    /// </remarks>
    public static string ErrorHeatmapPair(
        ReadOnlySpan<double> retentionTime, ReadOnlySpan<double> fragmentMz,
        ReadOnlySpan<double> before, ReadOnlySpan<double> after, string unit,
        int xBins = 44, int yBins = 36, int minimumPerCell = 8)
    {
        const int height = 400;
        var svg = new Svg(Width, height);
        if (before.Length == 0) return Empty(svg, "No matched fragments.");

        bool paired = after.Length == before.Length;
        (double rtMin, double rtMax) = Range(retentionTime);
        (double mzMin, double mzMax) = Range(fragmentMz);

        double?[] beforeCells = CellMedians(
            retentionTime, fragmentMz, before, rtMin, rtMax, mzMin, mzMax, xBins, yBins, minimumPerCell);
        double?[]? afterCells = paired
            ? CellMedians(retentionTime, fragmentMz, after, rtMin, rtMax, mzMin, mzMax, xBins, yBins, minimumPerCell)
            : null;

        // One scale for both panels, taken from the uncorrected field so the corrected one is
        // measured against it rather than against itself.
        double scale = CellScale(beforeCells);

        const int top = 46;
        const int bottom = 64;
        const int gap = 24;
        const int barWidth = 52;
        int panelWidth = paired
            ? (Width - Left - Right - gap - barWidth) / 2
            : Width - Left - Right - barWidth;
        int panelHeight = height - top - bottom;

        DrawPanel(svg, beforeCells, xBins, yBins, scale, Left, top, panelWidth, panelHeight,
            rtMin, rtMax, mzMin, mzMax, paired ? "Before correction" : "As measured", showY: true);

        if (paired)
        {
            DrawPanel(svg, afterCells!, xBins, yBins, scale, Left + panelWidth + gap, top,
                panelWidth, panelHeight, rtMin, rtMax, mzMin, mzMax, "After correction", showY: false);
        }

        VerticalColorBar(svg, Width - Right - barWidth + 10, top, panelHeight, scale, unit);
        svg.Text(13, top + (panelHeight / 2.0), "fragment m/z", anchor: "middle", size: 15, rotate: -90);
        return svg.ToString();
    }

    /// <summary>Median of each cell, or null where too few rows landed in it.</summary>
    /// <remarks>
    /// A cell holding one or two rows has a median that is just those rows, so coloring it
    /// scatters saturated noise across the field and hides the structure. Requiring a
    /// handful leaves thin regions blank instead, which is honest.
    /// </remarks>
    private static double?[] CellMedians(
        ReadOnlySpan<double> xValues, ReadOnlySpan<double> yValues, ReadOnlySpan<double> value,
        double xLow, double xHigh, double yLow, double yHigh, int xBins, int yBins, int minimum)
    {
        var cells = new List<double>[xBins * yBins];
        for (int i = 0; i < value.Length; i++)
        {
            int cx = Bucket(xValues[i], xLow, xHigh, xBins);
            int cy = Bucket(yValues[i], yLow, yHigh, yBins);
            if (cx < 0 || cy < 0) continue;
            int index = (cy * xBins) + cx;
            (cells[index] ??= new List<double>()).Add(value[i]);
        }

        var medians = new double?[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            List<double>? rows = cells[i];
            if (rows is null || rows.Count < minimum) continue;
            rows.Sort();
            medians[i] = rows[rows.Count / 2];
        }

        return medians;
    }

    private static double CellScale(double?[] cells)
    {
        var magnitudes = new List<double>();
        foreach (double? cell in cells)
        {
            if (cell is double v) magnitudes.Add(Math.Abs(v));
        }

        if (magnitudes.Count == 0) return 1;
        magnitudes.Sort();

        // The 97th percentile of what is drawn. Lower than this and the ramp saturates on
        // ordinary cells, so the cell-to-cell noise in a median reads as structure; much
        // higher and the field washes out again.
        double limit = magnitudes[Math.Min(magnitudes.Count - 1, (int)(magnitudes.Count * 0.97))];
        return limit > 0 ? limit : 1;
    }

    private static void DrawPanel(
        Svg svg, double?[] cells, int xBins, int yBins, double scale,
        double left, double top, double width, double height,
        double rtMin, double rtMax, double mzMin, double mzMax, string title, bool showY)
    {
        var pixels = new byte[xBins * yBins * 3];
        FillBackground(pixels);
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not double median) continue;
            int cy = i / xBins, cx = i % xBins;
            SetPixel(pixels, xBins, cx, yBins - 1 - cy, Diverging(median, scale));
        }

        svg.Image(left, top, width, height, Png.DataUri(pixels, xBins, yBins));
        svg.Rect(left, top, width, 1, Axis0);
        svg.Rect(left, top + height, width, 1, Axis0);
        svg.Rect(left, top, 1, height, Axis0);
        svg.Rect(left + width, top, 1, height, Axis0);
        svg.Text(left + (width / 2), top - 12, title, anchor: "middle", size: 16, bold: true);

        var x = new Axis(rtMin, rtMax, left, left + width);
        foreach (double tick in x.Ticks(5))
        {
            double px = x.Map(tick);
            svg.Line(px, top + height, px, top + height + 4, Axis0, 1);
            svg.Text(px, top + height + 16, x.Format(tick), anchor: "middle", size: 13, fill: Muted);
        }

        svg.Text(left + (width / 2), top + height + 34, "retention time (min)", anchor: "middle", size: 15);

        if (!showY) return;
        var y = new Axis(mzMin, mzMax, top, top + height, invert: true);
        foreach (double tick in y.Ticks(5))
        {
            double py = y.Map(tick);
            svg.Line(left - 4, py, left, py, Axis0, 1);
            svg.Text(left - 7, py + 3.5, y.Format(tick), anchor: "end", size: 13, fill: Muted);
        }
    }

    private static void VerticalColorBar(
        Svg svg, double x, double top, double height, double scale, string unit)
    {
        const int steps = 48;
        double band = height / steps;
        for (int i = 0; i < steps; i++)
        {
            double t = 1 - (2.0 * i / (steps - 1));
            svg.Rect(x, top + (i * band), 13, band + 0.6, ColorOf(Diverging(t * scale, scale)));
        }

        svg.Rect(x, top, 13, 1, Axis0);
        svg.Rect(x, top + height, 13, 1, Axis0);

        string limit = scale.ToString(scale < 0.01 ? "0.####" : "0.###", CultureInfo.InvariantCulture);
        svg.Text(x + 16, top + 8, "+" + limit, size: 10, fill: Muted);
        svg.Text(x + 16, top + (height / 2) + 3, "0", size: 10, fill: Muted);
        svg.Text(x + 16, top + height, "-" + limit, size: 10, fill: Muted);
        svg.Text(x + 16, top + height + 15, unit, size: 10, fill: Muted);
    }

    private static string ColorOf((byte R, byte G, byte B) c) =>
        "rgb(" + c.R.ToString(CultureInfo.InvariantCulture) + "," +
        c.G.ToString(CultureInfo.InvariantCulture) + "," +
        c.B.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// Mass error against one feature as a density, before and after correction, side by
    /// side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Colored by fragment count on a viridis ramp, which is what carries the information
    /// here: a monochrome ramp has one usable dimension and spends most of it on pale
    /// values, so the dense core and the sparse tail look much the same. Dark purple through
    /// green to yellow separates two orders of magnitude legibly, and stays legible printed
    /// in grayscale because it also rises monotonically in lightness.
    /// </para>
    /// <para>
    /// Each panel is normalized to its own peak rather than to a shared one. Correcting
    /// concentrates the distribution, so the after panel's peak is several times the before
    /// panel's; on a shared scale the before panel would flatten to near-empty and the
    /// structure that motivated the correction would disappear from the figure.
    /// </para>
    /// </remarks>
    public static string FeatureVersusErrorPair(
        ReadOnlySpan<double> feature, ReadOnlySpan<double> before, ReadOnlySpan<double> after,
        string featureName, string unit, int xBins = 72, int yBins = 56)
    {
        const int height = 400;
        var svg = new Svg(Width, height);
        if (feature.Length == 0) return Empty(svg, "No matched fragments.");

        bool paired = after.Length == before.Length;
        double xLow = Percentile(feature, 0.002);
        double xHigh = Percentile(feature, 0.998);

        // One vertical range for both panels: the after panel being visibly tighter is the
        // result, and rescaling it away would hide exactly that.
        double limit = SymmetricLimit(before, 0.99);

        const int top = 46;
        const int bottom = 64;
        const int gap = 24;
        const int barWidth = 58;
        int panelWidth = paired
            ? (Width - Left - Right - gap - barWidth) / 2
            : Width - Left - Right - barWidth;
        int panelHeight = height - top - bottom;

        int beforePeak = DrawDensityPanel(
            svg, feature, before, xLow, xHigh, limit, xBins, yBins,
            Left, top, panelWidth, panelHeight, paired ? "Before correction" : "As measured",
            featureName, showY: true, unit: unit);

        int afterPeak = 0;
        if (paired)
        {
            afterPeak = DrawDensityPanel(
                svg, feature, after, xLow, xHigh, limit, xBins, yBins,
                Left + panelWidth + gap, top, panelWidth, panelHeight, "After correction",
                featureName, showY: false, unit: unit);
        }

        ViridisBar(svg, Width - Right - barWidth + 10, top, panelHeight, beforePeak, afterPeak, paired);
        return svg.ToString();
    }

    /// <summary>
    /// Exponent for the density color ramp, as in matplotlib's PowerNorm. Below 1 it lifts
    /// sparse cells; the further below, the more of the ramp goes to the sparse end.
    /// </summary>
    private const double DensityGamma = 0.4;

    /// <summary>Draws one density panel and returns the count in its busiest cell.</summary>
    private static int DrawDensityPanel(
        Svg svg, ReadOnlySpan<double> feature, ReadOnlySpan<double> error,
        double xLow, double xHigh, double limit, int xBins, int yBins,
        double left, double top, double width, double height,
        string title, string featureName, bool showY, string unit)
    {
        var counts = new int[xBins * yBins];
        int peak = 0;
        for (int i = 0; i < feature.Length; i++)
        {
            int cx = Bucket(feature[i], xLow, xHigh, xBins);
            int cy = Bucket(error[i], -limit, limit, yBins);
            if (cx < 0 || cy < 0) continue;
            peak = Math.Max(peak, ++counts[(cy * xBins) + cx]);
        }

        var pixels = new byte[xBins * yBins * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            // Empty stays white, as in the reference figures: an empty cell is an absence of
            // data, not the low end of a density.
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
        }

        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;

            // A power law rather than a log or a straight fraction. Straight fraction leaves
            // one bright cell in a dark field, because the core of a density like this runs
            // orders of magnitude above its tails. A log overcorrects: with a peak of 2,854 it
            // puts a 500-count cell at 0.78 of the ramp, so most of the core saturates to
            // yellow and the structure inside it - which is the part worth looking at - is
            // flattened. The exponent below puts that same cell at 0.50, keeping the top of
            // the ramp for cells actually near the peak while still lifting the sparse tail
            // clear of black.
            double t = peak > 0 ? Math.Pow((double)counts[i] / peak, DensityGamma) : 0;
            int cy = i / xBins, cx = i % xBins;
            SetPixel(pixels, xBins, cx, yBins - 1 - cy, Viridis(t));
        }

        svg.Image(left, top, width, height, Png.DataUri(pixels, xBins, yBins));
        svg.Rect(left, top, width, 1, Axis0);
        svg.Rect(left, top + height, width, 1, Axis0);
        svg.Rect(left, top, 1, height, Axis0);
        svg.Rect(left + width, top, 1, height, Axis0);
        svg.Text(left + (width / 2), top - 12, title, anchor: "middle", size: 16, bold: true);

        var y = new Axis(-limit, limit, top, top + height, invert: true);
        var x = new Axis(xLow, xHigh, left, left + width);
        svg.Line(left, y.Map(0), left + width, y.Map(0), "#ffffff", 1.2, "4 3");
        MedianTrend(svg, feature, error, x, y, xLow, xHigh, xBins, left, left + width);

        foreach (double tick in x.Ticks(4))
        {
            double px = x.Map(tick);
            svg.Line(px, top + height, px, top + height + 4, Axis0, 1);
            svg.Text(px, top + height + 16, x.Format(tick), anchor: "middle", size: 13, fill: Muted);
        }

        svg.Text(left + (width / 2), top + height + 34, featureName, anchor: "middle", size: 15);

        if (showY)
        {
            foreach (double tick in y.Ticks(5))
            {
                double py = y.Map(tick);
                svg.Line(left - 4, py, left, py, Axis0, 1);
                svg.Text(left - 7, py + 4, y.Format(tick), anchor: "end", size: 13, fill: Muted);
            }

            svg.Text(13, top + (height / 2), $"mass error ({unit})", anchor: "middle", size: 15, rotate: -90);
        }

        return peak;
    }

    /// <summary>
    /// Median error per column, drawn over the density.
    /// </summary>
    /// <remarks>
    /// Cased - a dark stroke under a white one - because viridis runs from near-black to
    /// near-yellow and no single color reads against both ends of it.
    /// </remarks>
    private static void MedianTrend(
        Svg svg, ReadOnlySpan<double> feature, ReadOnlySpan<double> error,
        Axis x, Axis y, double low, double high, int bins, double clipLeft, double clipRight)
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
            // A column of a handful of rows is noise, and a trend line through noise reads as
            // signal.
            if (values is null || values.Count < 20) continue;
            values.Sort();
            double px = x.Map(low + ((b + 0.5) * step));
            if (px < clipLeft || px > clipRight) continue;
            points.Add((px, y.Map(values[values.Count / 2])));
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points);
        svg.Polyline(span, "#1a1d21", 3.2);
        svg.Polyline(span, "#ffffff", 1.6);
    }

    private static void ViridisBar(
        Svg svg, double x, double top, double height, int beforePeak, int afterPeak, bool paired)
    {
        const int steps = 48;
        double band = height / steps;
        for (int i = 0; i < steps; i++)
        {
            double t = 1 - ((double)i / (steps - 1));
            svg.Rect(x, top + (i * band), 13, band + 0.6, ColorOf(Viridis(t)));
        }

        svg.Rect(x, top, 13, 1, Axis0);
        svg.Rect(x, top + height, 13, 1, Axis0);
        svg.Text(x + 16, top + 8, "peak", size: 10, fill: Muted);
        svg.Text(x + 16, top + height, "0", size: 10, fill: Muted);
        svg.Text(x + 16, top + height + 15, "fragments", size: 10, fill: Muted);

        string peaks = paired
            ? $"peak {beforePeak:N0} / {afterPeak:N0}"
            : $"peak {beforePeak:N0}";
        svg.Text(x + 16, top + height + 29, peaks, size: 9, fill: Muted);
    }

    /// <summary>
    /// The viridis ramp, interpolated between its usual anchors.
    /// </summary>
    /// <remarks>
    /// Chosen for the reason it is usually chosen: it is perceptually near-uniform, so equal
    /// steps in density look like equal steps in color, and it rises monotonically in
    /// lightness so it survives being printed in grayscale.
    /// </remarks>
    private static (byte R, byte G, byte B) Viridis(double t)
    {
        (double R, double G, double B)[] anchors =
        {
            (68, 1, 84), (72, 40, 120), (62, 74, 137), (49, 104, 142), (38, 130, 142),
            (31, 158, 137), (53, 183, 121), (109, 205, 89), (180, 222, 44), (253, 231, 37),
        };

        double clamped = Math.Clamp(t, 0, 1) * (anchors.Length - 1);
        int i = Math.Min((int)clamped, anchors.Length - 2);
        double f = clamped - i;

        return (
            (byte)Math.Round(anchors[i].R + ((anchors[i + 1].R - anchors[i].R) * f)),
            (byte)Math.Round(anchors[i].G + ((anchors[i + 1].G - anchors[i].G) * f)),
            (byte)Math.Round(anchors[i].B + ((anchors[i + 1].B - anchors[i].B) * f)));
    }

    /// <summary>
    /// Each fold's accuracy against the pooled figure, with a band at one standard
    /// deviation either side of the fold mean.
    /// </summary>
    /// <remarks>
    /// The variance is the point of plotting this at all. A single held-out number says how
    /// the model did on one split; the picture says whether that number was luck. Folds
    /// sitting almost on top of each other mean the estimate is stable and the headline
    /// figure can be quoted as-is. Folds scattered across the band mean the cohort contains
    /// regions the model handles very differently, and the pooled number is an average over
    /// them rather than a description of any of them.
    /// </remarks>
    public static string FoldSpread(
        IReadOnlyList<double> perFold, double pooled, double spread, string unit, string metric)
    {
        const int height = 215;
        var svg = new Svg(Width, height);
        if (perFold.Count == 0) return Empty(svg, "No folds to show.");

        double lowest = pooled, highest = pooled;
        foreach (double value in perFold)
        {
            lowest = Math.Min(lowest, value);
            highest = Math.Max(highest, value);
        }

        // Pad by the spread so the band is visible even when the folds are nearly identical,
        // which is the common and desirable case.
        double pad = Math.Max(double.IsNaN(spread) ? 0 : spread * 2, (highest - lowest) * 0.6);
        if (pad <= 0) pad = Math.Abs(pooled) * 0.02;
        if (pad <= 0) pad = 1;

        var x = new Axis(lowest - pad, highest + pad, Left, Width - Right);
        double axisY = height - 54;
        double dotY = axisY - 46;

        if (!double.IsNaN(spread) && spread > 0)
        {
            double mean = 0;
            foreach (double value in perFold) mean += value;
            mean /= perFold.Count;

            double bandLow = x.Map(mean - spread);
            double bandHigh = x.Map(mean + spread);
            svg.Rect(bandLow, dotY - 22, bandHigh - bandLow, 44, After,
                "fill-opacity=\"0.12\"");
            svg.Text((bandLow + bandHigh) / 2, dotY - 28, "+/- 1 sd", anchor: "middle",
                size: 10, fill: Muted);
        }

        double pooledX = x.Map(pooled);
        svg.Line(pooledX, dotY - 26, pooledX, dotY + 26, Before, 2);
        svg.Text(pooledX, dotY + 40, "pooled " + pooled.ToString("0.0000", CultureInfo.InvariantCulture),
            anchor: "middle", size: 10, fill: Before);

        for (int i = 0; i < perFold.Count; i++)
        {
            double px = x.Map(perFold[i]);
            // Spread the dots vertically so folds with near-identical values stay countable
            // rather than drawing on top of one another.
            double py = dotY - 14 + (i * (28.0 / Math.Max(1, perFold.Count - 1)));
            svg.Rect(px - 3, py - 3, 6, 6, After);
            svg.Text(px + 7, py + 3.5, (i + 1).ToString(CultureInfo.InvariantCulture), size: 9, fill: Muted);
        }

        foreach (double tick in x.Ticks(6))
        {
            double px = x.Map(tick);
            svg.Line(px, axisY, px, axisY + 4, Axis0, 1);
            svg.Text(px, axisY + 18, x.Format(tick), anchor: "middle", size: 13, fill: Muted);
        }

        svg.Line(Left, axisY, Width - Right, axisY, Axis0, 1);
        svg.Text(Left, 22, $"{metric} per fold ({unit})", size: 16, bold: true);
        return svg.ToString();
    }

    public static string FeatureImportance(IReadOnlyList<string> names, IReadOnlyList<double> importance)
    {
        int rows = Math.Min(names.Count, importance.Count);
        int height = Math.Max(190, 44 + (rows * 26));
        var svg = new Svg(Width, height);
        if (rows == 0) return Empty(svg, "Importance was not computed.");

        var order = new int[rows];
        for (int i = 0; i < rows; i++) order[i] = i;
        Array.Sort(order, (a, b) => importance[b].CompareTo(importance[a]));

        double max = 0;
        foreach (double value in importance) max = Math.Max(max, value);
        if (max <= 0) max = 1;

        const int labelWidth = 230;
        double barLeft = labelWidth + 10;
        double barSpan = Width - barLeft - 60;

        for (int i = 0; i < rows; i++)
        {
            int index = order[i];
            double y = 28 + (i * 26);
            svg.Text(labelWidth, y + 13, names[index], anchor: "end", size: 13);
            double barWidth = barSpan * (importance[index] / max);
            svg.Rect(barLeft, y + 2, barWidth, 14, After);
            svg.Text(
                barLeft + barWidth + 6, y + 13,
                importance[index].ToString("0.000", CultureInfo.InvariantCulture),
                size: 11, fill: Muted);
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
            svg.Text(Left - 8, py + 4, y.Format(tick), anchor: "end", size: 13, fill: Muted);
        }

        foreach (double tick in x.Ticks(7))
        {
            double px = x.Map(tick);
            if (drawGrid) svg.Line(px, Top, px, Height - Bottom, Grid, 1);
            svg.Text(px, Height - Bottom + 17, x.Format(tick), anchor: "middle", size: 13, fill: Muted);
        }

        svg.Line(Left, Height - Bottom, Width - Right, Height - Bottom, Axis0, 1);
        svg.Line(Left, Top, Left, Height - Bottom, Axis0, 1);
        svg.Text(Left + ((Width - Right - Left) / 2.0), Height - 6, xLabel, anchor: "middle", size: 15);
        svg.Text(14, Top + ((Height - Bottom - Top) / 2.0), yLabel, anchor: "middle", size: 15, rotate: -90);
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
        svg.Rect(x, 13, 11, 11, Before);
        svg.Text(x + 17, 24, beforeLabel, size: 13);
        if (!hasAfter) return;
        svg.Rect(x + 110, 13, 11, 11, After);
        svg.Text(x + 130, 24, afterLabel, size: 13);
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
    /// so it cannot follow the reader's color scheme the way the vector layers do. The
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
