// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;

namespace MARS.Core;

/// <summary>The scale a <see cref="WindowPlacement"/>'s window and mode are measured on.</summary>
public enum WindowUnit
{
    /// <summary>Thomson. An absolute window is the same width at every m/z.</summary>
    Th,

    /// <summary>Parts per million. A ppm window is a different width at every m/z.</summary>
    Ppm,
}

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
/// can be in the wrong place for reasons that have nothing to do with isotopes. What it
/// cannot see is a population that is centred and tight but wrong, which is what an offset
/// of almost exactly one isotope spacing produces in a ppm window - the
/// <see cref="MonoisotopicAnchor"/> remarks describe that case.
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

    /// <summary>Centre of the densest bin, as a position inside the window, in <see cref="Unit"/>.</summary>
    /// <remarks>
    /// Measured from the window's centre, not from zero. When a run is matched at a coarse
    /// shift the two differ by that shift, and it is the distance from the centre that says
    /// whether the window is over the peaks.
    /// </remarks>
    public double Mode { get; init; }

    /// <summary>Where the window was centred, in Th from the library m/z.</summary>
    /// <remarks>
    /// Always Th, whatever <see cref="Unit"/> is: the coarse shift is an absolute offset, and
    /// it is only ever non-zero for an absolute window, since the anchor does not run for ppm.
    /// </remarks>
    public double CentreTh { get; init; }

    /// <summary>The scale <see cref="Mode"/> and <see cref="HalfWindow"/> are measured on.</summary>
    public WindowUnit Unit { get; init; } = WindowUnit.Th;

    /// <summary>|mode| as a fraction of the half-window. Near zero when the window is centred.</summary>
    public double ModeOffsetFraction { get; init; }

    /// <summary>
    /// Density in the busier of the two outermost bins, as a fraction of the mode's density.
    /// Near zero when the error distribution dies away before it reaches the window edge.
    /// </summary>
    public double EdgeDensityRatio { get; init; }

    /// <summary>Half-width of the window the rows were matched in, in <see cref="Unit"/>.</summary>
    public double HalfWindow { get; init; }

    public int Rows { get; init; }

    /// <summary>False when there were too few rows to judge.</summary>
    public bool Assessable { get; init; }

    public bool MisCentred => Assessable && ModeOffsetFraction > SuspectModeOffset;

    public bool Truncated => Assessable && EdgeDensityRatio > SuspectEdgeRatio;

    public bool Suspect => MisCentred || Truncated;

    /// <summary>
    /// Measures the placement of an absolute window of half-width
    /// <paramref name="halfWindowTh"/> from the errors matched inside it.
    /// </summary>
    /// <param name="delta">Matched errors, observed minus library m/z, in Th.</param>
    /// <param name="halfWindowTh">Half-width of the matching window, in Th.</param>
    /// <param name="centreTh">
    /// Where the window was centred relative to the library m/z - the coarse shift the run
    /// was matched at, zero when it was matched where the library put it.
    /// </param>
    public static WindowPlacement Measure(
        ReadOnlySpan<double> delta, double halfWindowTh, double centreTh = 0) =>
        Measure(delta, halfWindowTh, centreTh, WindowUnit.Th);

    /// <summary>
    /// Measures the placement of a ppm window, which is a different width in Th at every m/z.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each row is converted to its own error in ppm first, which is what puts every row in
    /// the same window: a +/-10 ppm tolerance is +/-0.005 Th at m/z 500 and +/-0.015 Th at
    /// m/z 1500, so pooling raw Th errors against one representative window would judge most
    /// of the cohort against a window it was never matched in.
    /// </para>
    /// <para>
    /// That is not a rounding error, it inverts the result. Rows from the narrow end of the
    /// m/z range pile into the middle bins whatever their shape, because their whole window
    /// is a fraction of the one being drawn; rows from the wide end have their tails cut off
    /// at the edges, which is exactly the density the truncation test looks for. A sample
    /// spread uniformly across each fragment's own +/-10 ppm window over m/z 100 to 1500 - the
    /// flattest distribution there is, and one this check exists to catch - reads an edge
    /// ratio of 0.36 pooled in Th against the median window, comfortably healthy, having also
    /// dropped 13% of its rows as outside a window they were matched inside. Per row it reads
    /// 1.00.
    /// </para>
    /// </remarks>
    /// <param name="delta">Matched errors, observed minus library m/z, in Th.</param>
    /// <param name="fragmentMz">
    /// Each row's window centre in m/z - the <c>fragment_mz</c> feature, which is the library
    /// m/z plus any shift the run was matched at, and is what the matcher sized the ppm
    /// window from.
    /// </param>
    /// <param name="tolerancePpm">Half-width of the matching window, in ppm.</param>
    /// <param name="centreTh">
    /// The coarse shift the run was matched at, in Th. The anchor does not run for ppm, so
    /// this is non-zero only when one was forced with --mz-shift.
    /// </param>
    public static WindowPlacement MeasurePpm(
        ReadOnlySpan<double> delta, ReadOnlySpan<double> fragmentMz, double tolerancePpm,
        double centreTh = 0)
    {
        if (delta.Length != fragmentMz.Length)
            throw new ArgumentException("One fragment m/z per error is needed.", nameof(fragmentMz));

        // Converted after the shift is taken off, so each value is the row's position inside
        // its own window: zero at the centre, +/-tolerancePpm at the edges.
        var ppm = new double[delta.Length];
        for (int i = 0; i < delta.Length; i++)
            ppm[i] = fragmentMz[i] > 0 ? (delta[i] - centreTh) * 1e6 / fragmentMz[i] : double.NaN;

        WindowPlacement placement = Measure(ppm, tolerancePpm, 0, WindowUnit.Ppm);
        return new WindowPlacement
        {
            Mode = placement.Mode,
            ModeOffsetFraction = placement.ModeOffsetFraction,
            EdgeDensityRatio = placement.EdgeDensityRatio,
            HalfWindow = placement.HalfWindow,
            CentreTh = centreTh,
            Unit = WindowUnit.Ppm,
            Rows = placement.Rows,
            Assessable = placement.Assessable,
        };
    }

    private static WindowPlacement Measure(
        ReadOnlySpan<double> delta, double halfWindow, double centre, WindowUnit unit)
    {
        if (delta.Length < MinRows || !(halfWindow > 0))
        {
            return new WindowPlacement
            {
                Rows = delta.Length, HalfWindow = halfWindow, CentreTh = centre, Unit = unit,
            };
        }

        var histogram = new int[Bins];
        int binned = 0;
        double width = 2 * halfWindow;
        for (int i = 0; i < delta.Length; i++)
        {
            double value = delta[i] - centre;
            if (!double.IsFinite(value)) continue;

            int bin = (int)((value + halfWindow) / width * Bins);
            if (bin < 0 || bin >= Bins) continue;
            histogram[bin]++;
            binned++;
        }

        if (binned < MinRows)
        {
            return new WindowPlacement
            {
                Rows = binned, HalfWindow = halfWindow, CentreTh = centre, Unit = unit,
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
        double modeAt = -halfWindow + ((mode + 0.5) * width / Bins);

        return new WindowPlacement
        {
            Mode = modeAt,
            ModeOffsetFraction = Math.Abs(modeAt) / halfWindow,
            EdgeDensityRatio = peak > 0 ? edge / peak : 0,
            HalfWindow = halfWindow,
            CentreTh = centre,
            Unit = unit,
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
                $"its mode sits at {FormatMode()}, {ModeOffsetFraction:P0} of the way to the " +
                $"edge of a {FormatWindow()} window, and {EdgeDensityRatio:P0} of the mode's " +
                "density is still there at the edge",
            (true, false) =>
                $"its mode sits at {FormatMode()}, {ModeOffsetFraction:P0} of the way to the " +
                $"edge of a {FormatWindow()} window",
            _ =>
                $"{EdgeDensityRatio:P0} of the mode's density is still present at the edge of a " +
                $"{FormatWindow()} window",
        };

        string remedy = CentreTh != 0
            ? $"The window was already recentred by {CentreTh:+0.000;-0.000} Th and still does not "
              + "sit over the peaks, so the offset the anchor found was not the whole story: widen "
              + "--tolerance, or set --mz-shift if you know what this run's offset is."
            : Unit == WindowUnit.Ppm
                ? "Check the instrument's calibration, and widen --tolerance-ppm enough to admit "
                  + "the real peak. The m/z anchor does not run for a ppm window, so nothing has "
                  + "tried to recentre this; --mz-shift sets the offset by hand if you know it."
                : "Check the instrument's calibration, and widen --tolerance enough to admit the "
                  + "real peak.";

        return "WARNING: the matched errors do not sit inside the matching window the way a real "
            + $"fragment population does - {what}. That means the window is not over the peaks: the "
            + "monoisotopic peak is probably outside it, and what was matched is noise or an isotope "
            + "of the fragment. An isotope match trains and reports exactly like a real one, so the "
            + $"accuracy figures will not show it. {remedy}";
    }

    /// <summary>The two statistics, for the QC report, whether or not they are suspect.</summary>
    public string Describe() => Assessable
        ? $"mode {FormatMode()} from the window centre "
          + $"({ModeOffsetFraction:P0} of the half-window), "
          + $"edge density {EdgeDensityRatio:P0} of the mode's"
        : $"not assessable ({Rows:N0} rows, {MinRows:N0} needed)";

    private string FormatMode() => Unit == WindowUnit.Ppm
        ? $"{Mode:+0.0;-0.0} ppm"
        : $"{Mode:+0.000;-0.000} Th";

    private string FormatWindow() => Unit == WindowUnit.Ppm
        ? $"+/-{HalfWindow:0.#} ppm"
        : $"+/-{HalfWindow:0.###} Th";
}
