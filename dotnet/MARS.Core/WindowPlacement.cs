// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;

namespace MARS.Core;

/// <summary>
/// Whether the matching window was in the right place, judged from the shape of the matched
/// error distribution alone.
/// </summary>
/// <remarks>
/// <para>
/// A window that sits over the fragment peaks produces a distribution with a mode near its
/// centre and very little density at its edges. A window that does not produces one of two
/// shapes, and both are unmistakable: a distribution still climbing as it reaches an edge,
/// because the real population lies beyond it, or one spread flatly across the whole window,
/// because nothing in it was ever the fragment.
/// </para>
/// <para>
/// Two statistics separate those cases, and neither needs a reference run, an instrument
/// model or a tolerance assumption - only the window's own width. On the Stellar reference
/// cohort, as acquired, they read 0.13 and 0.08. With the library shifted to simulate
/// instrument offsets of -0.25, -0.50 and -0.75 Th they read 0.83/0.78, 1.00/0.98 and
/// 0.94/0.83. The thresholds here sit at 0.4, a factor of three clear of a healthy run and
/// a factor of two below the mildest failure.
/// </para>
/// <para>
/// Peakedness - how far the mode stands above a flat distribution - was measured and
/// discarded: it reads 2.13 on both the healthy run and the worst failure, because a
/// distribution piling up against a window edge has a mode too.
/// </para>
/// <para>
/// This is the backstop to <see cref="MonoisotopicAnchor"/>, and the check that the anchor
/// worked. It runs for ppm tolerances as well, where the anchor does not, because a window
/// can be in the wrong place for reasons that have nothing to do with isotopes.
/// </para>
/// </remarks>
public sealed class WindowPlacement
{
    /// <summary>Histogram resolution. Forty bins over the window, so each is 5% of its width.</summary>
    public const int Bins = 40;

    /// <summary>Rows below which the shape says nothing.</summary>
    public const int MinRows = 500;

    /// <summary>Edge density, as a fraction of the mode's, above which the window looks truncated.</summary>
    public const double SuspectEdgeRatio = 0.4;

    /// <summary>How far off centre the mode may sit, as a fraction of the half-window.</summary>
    public const double SuspectModeOffset = 0.4;

    /// <summary>Centre of the densest bin, as a position inside the window.</summary>
    /// <remarks>
    /// Measured from the window's centre, not from zero. When a run is matched at a coarse
    /// shift the two differ by that shift, and it is the distance from the centre that says
    /// whether the window is over the peaks.
    /// </remarks>
    public double ModeTh { get; init; }

    /// <summary>Where the window was centred, in Th from the library m/z.</summary>
    public double CentreTh { get; init; }

    /// <summary>|mode| as a fraction of the half-window. Near zero when the window is centred.</summary>
    public double ModeOffsetFraction { get; init; }

    /// <summary>
    /// Density in the busier of the two outermost bins, as a fraction of the mode's density.
    /// Near zero when the error distribution dies away before it reaches the window edge.
    /// </summary>
    public double EdgeDensityRatio { get; init; }

    /// <summary>Half-width of the window the rows were matched in, in Th.</summary>
    public double HalfWindowTh { get; init; }

    public int Rows { get; init; }

    /// <summary>False when there were too few rows to judge.</summary>
    public bool Assessable { get; init; }

    public bool MisCentred => Assessable && ModeOffsetFraction > SuspectModeOffset;

    public bool Truncated => Assessable && EdgeDensityRatio > SuspectEdgeRatio;

    public bool Suspect => MisCentred || Truncated;

    /// <summary>
    /// Measures the placement of a window of half-width <paramref name="halfWindowTh"/> from
    /// the errors matched inside it.
    /// </summary>
    /// <param name="delta">Matched errors, observed minus library m/z.</param>
    /// <param name="halfWindowTh">Half-width of the matching window, in Th.</param>
    /// <param name="centreTh">
    /// Where the window was centred relative to the library m/z - the coarse shift the run
    /// was matched at, zero when it was matched where the library put it.
    /// </param>
    public static WindowPlacement Measure(
        ReadOnlySpan<double> delta, double halfWindowTh, double centreTh = 0)
    {
        if (delta.Length < MinRows || !(halfWindowTh > 0))
        {
            return new WindowPlacement
            {
                Rows = delta.Length, HalfWindowTh = halfWindowTh, CentreTh = centreTh,
            };
        }

        var histogram = new int[Bins];
        int binned = 0;
        double width = 2 * halfWindowTh;
        for (int i = 0; i < delta.Length; i++)
        {
            double value = delta[i] - centreTh;
            if (!double.IsFinite(value)) continue;

            int bin = (int)((value + halfWindowTh) / width * Bins);
            if (bin < 0 || bin >= Bins) continue;
            histogram[bin]++;
            binned++;
        }

        if (binned < MinRows)
        {
            return new WindowPlacement
            {
                Rows = binned, HalfWindowTh = halfWindowTh, CentreTh = centreTh,
            };
        }

        // Three-bin mean, narrowing at the ends. Enough to stop a single noisy bin from
        // deciding where the mode is, little enough to leave an edge pile-up at the edge.
        var smoothed = new double[Bins];
        for (int i = 0; i < Bins; i++)
        {
            int low = Math.Max(0, i - 1);
            int high = Math.Min(Bins - 1, i + 1);
            int sum = 0;
            for (int j = low; j <= high; j++) sum += histogram[j];
            smoothed[i] = (double)sum / (high - low + 1);
        }

        int mode = 0;
        for (int i = 1; i < Bins; i++)
        {
            if (smoothed[i] > smoothed[mode]) mode = i;
        }

        double peak = smoothed[mode];
        double edge = Math.Max(smoothed[0], smoothed[Bins - 1]);
        double modeTh = -halfWindowTh + ((mode + 0.5) * width / Bins);

        return new WindowPlacement
        {
            ModeTh = modeTh,
            ModeOffsetFraction = Math.Abs(modeTh) / halfWindowTh,
            EdgeDensityRatio = peak > 0 ? edge / peak : 0,
            HalfWindowTh = halfWindowTh,
            CentreTh = centreTh,
            Rows = binned,
            Assessable = true,
        };
    }

    /// <summary>The warning for a suspect placement, or null when there is nothing to say.</summary>
    public string? Warning()
    {
        if (!Suspect) return null;

        string what = (MisCentred, Truncated) switch
        {
            (true, true) =>
                $"its mode sits at {ModeTh:+0.000;-0.000} Th, {ModeOffsetFraction:P0} of the way to the " +
                $"edge of a +/-{HalfWindowTh:0.###} Th window, and {EdgeDensityRatio:P0} of the mode's " +
                "density is still there at the edge",
            (true, false) =>
                $"its mode sits at {ModeTh:+0.000;-0.000} Th, {ModeOffsetFraction:P0} of the way to the " +
                $"edge of a +/-{HalfWindowTh:0.###} Th window",
            _ =>
                $"{EdgeDensityRatio:P0} of the mode's density is still present at the edge of a " +
                $"+/-{HalfWindowTh:0.###} Th window",
        };

        string remedy = CentreTh != 0
            ? $"The window was already recentred by {CentreTh:+0.000;-0.000} Th and still does not "
              + "sit over the peaks, so the offset the anchor found was not the whole story: widen "
              + "--tolerance, or set --mz-shift if you know what this run's offset is."
            : "Check the instrument's calibration, and widen --tolerance enough to admit the real "
              + "peak.";

        return "WARNING: the matched errors do not sit inside the matching window the way a real "
            + $"fragment population does - {what}. That means the window is not over the peaks: the "
            + "monoisotopic peak is probably outside it, and what was matched is noise or an isotope "
            + "of the fragment. An isotope match trains and reports exactly like a real one, so the "
            + $"accuracy figures will not show it. {remedy}";
    }

    /// <summary>The two statistics, for the QC report, whether or not they are suspect.</summary>
    public string Describe() => Assessable
        ? $"mode {ModeTh:+0.000;-0.000} Th from the window centre "
          + $"({ModeOffsetFraction:P0} of the half-window), "
          + $"edge density {EdgeDensityRatio:P0} of the mode's"
        : $"not assessable ({Rows:N0} rows)";
}
