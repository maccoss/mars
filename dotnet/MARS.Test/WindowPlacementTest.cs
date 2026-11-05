// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using MARS.Core;
using Xunit;

namespace MARS.Test;

/// <summary>
/// The backstop: does the shape of the matched errors say the window was in the right place?
/// The numbers asserted here are the ones measured on the Stellar reference cohort, with the
/// library shifted to simulate an instrument offset.
/// </summary>
public sealed class WindowPlacementTest
{
    /// <summary>Errors clustered around <paramref name="centre"/>, as a real run's are.</summary>
    private static double[] Clustered(double centre, double spread, double halfWindow, int n = 20000)
    {
        var random = new Random(11);
        var values = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Box-Muller, rejected back into the window the way the matcher's own window
            // would have rejected it.
            double value;
            do
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                value = centre + (spread * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
            }
            while (Math.Abs(value) > halfWindow);

            values[i] = value;
        }

        return values;
    }

    private static double[] Uniform(double halfWindow, int n = 20000)
    {
        var random = new Random(13);
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = (random.NextDouble() * 2 * halfWindow) - halfWindow;
        return values;
    }

    [Fact]
    public void ACentredPopulationLooksHealthy()
    {
        WindowPlacement placement = WindowPlacement.Measure(Clustered(0, 0.08, 0.3), 0.3);

        Assert.True(placement.Assessable);
        Assert.False(placement.Suspect);
        Assert.True(placement.ModeOffsetFraction < 0.2, $"mode offset {placement.ModeOffsetFraction}");
        Assert.True(placement.EdgeDensityRatio < 0.2, $"edge ratio {placement.EdgeDensityRatio}");
    }

    [Fact]
    public void APopulationAgainstTheEdgeIsBothMisCentredAndTruncated()
    {
        // What a -0.25 Th instrument offset does to a 0.3 Th window: the real peak is just
        // outside, so what is left inside piles up against that edge.
        WindowPlacement placement = WindowPlacement.Measure(Clustered(-0.28, 0.08, 0.3), 0.3);

        Assert.True(placement.MisCentred);
        Assert.True(placement.Truncated);
        Assert.NotNull(placement.Warning());
    }

    [Fact]
    public void AFlatDistributionIsTruncated()
    {
        // The -0.50 Th case: nothing in the window was ever the fragment, so the errors are
        // spread evenly across it and the edges are as dense as the middle.
        WindowPlacement placement = WindowPlacement.Measure(Uniform(0.3), 0.3);

        Assert.True(placement.Truncated);
        Assert.True(placement.EdgeDensityRatio > 0.8, $"edge ratio {placement.EdgeDensityRatio}");
    }

    [Fact]
    public void TheWindowCentreMovesWithTheShift()
    {
        // A run matched at a -0.75 Th shift has its errors around -0.75, and that is exactly
        // where they should be. The check has to be told where the window was, or the run
        // that the anchor just fixed is the one it reports as broken.
        double[] delta = Array.ConvertAll(Clustered(0, 0.08, 0.3), d => d - 0.75);

        WindowPlacement judged = WindowPlacement.Measure(delta, 0.3, centreTh: -0.75);
        Assert.True(judged.Assessable);
        Assert.False(judged.Suspect);

        // Judged against a centre of zero every row is outside the window, which is not a
        // verdict of "healthy" - it is no verdict at all.
        Assert.False(WindowPlacement.Measure(delta, 0.3).Assessable);
    }

    [Fact]
    public void AShiftedRunStillReportsItsErrorFromTheLibrary()
    {
        // The shift moves the window, not the measurement: the mode is reported relative to
        // the window centre, while CentreTh keeps the distance from the library m/z. A run
        // 0.75 Th off is a run 0.75 Th off however well it was matched.
        double[] delta = Array.ConvertAll(Clustered(0, 0.08, 0.3), d => d - 0.75);
        WindowPlacement placement = WindowPlacement.Measure(delta, 0.3, centreTh: -0.75);

        Assert.Equal(-0.75, placement.CentreTh);
        Assert.True(Math.Abs(placement.Mode) < 0.1, $"mode {placement.Mode}");
    }

    [Fact]
    public void TooFewRowsSaysNothing()
    {
        WindowPlacement placement = WindowPlacement.Measure(Clustered(0, 0.08, 0.3, n: 100), 0.3);

        Assert.False(placement.Assessable);
        Assert.False(placement.Suspect);
        Assert.Null(placement.Warning());
    }

    [Fact]
    public void TheWarningSaysWhenRecentringAlreadyHappened()
    {
        // A shift was applied and the window still is not over the peaks: the remedy is not
        // the same one as for a run that was never shifted.
        double[] delta = Array.ConvertAll(Clustered(-0.28, 0.08, 0.3), d => d - 0.5);
        string? warning = WindowPlacement.Measure(delta, 0.3, centreTh: -0.5).Warning();

        Assert.NotNull(warning);
        Assert.Contains("already recentred", warning);
    }

    // ---- ppm windows ------------------------------------------------------------------

    /// <summary>
    /// A ppm run: each row placed at <paramref name="positions"/>[i] inside its OWN window,
    /// -1 at one edge and +1 at the other, with fragments spread over m/z 100 to 1500 so the
    /// window is a different width in Th on every row.
    /// </summary>
    private static (double[] Delta, double[] FragmentMz) PpmRun(
        double[] positions, double tolerancePpm, double shiftTh = 0)
    {
        var random = new Random(23);
        var delta = new double[positions.Length];
        var fragmentMz = new double[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            // fragment_mz is the window centre: the library m/z plus any shift.
            fragmentMz[i] = 100 + (random.NextDouble() * 1400);
            delta[i] = shiftTh + (positions[i] * tolerancePpm * 1e-6 * fragmentMz[i]);
        }

        return (delta, fragmentMz);
    }

    // The case the review reproduced. Every row is spread evenly across its own +/-10 ppm
    // window: nothing in that is a fragment population, and saying so is what the check is
    // for. Judged in Th against one window sized at the median m/z, as this first did, the
    // same rows read an edge ratio of 0.36 - healthy.
    [Fact]
    public void APpmRunFlatInEveryRowsOwnWindowIsTruncated()
    {
        (double[] delta, double[] mz) = PpmRun(Uniform(1.0, n: 50000), tolerancePpm: 10);

        WindowPlacement placement = WindowPlacement.MeasurePpm(delta, mz, 10);

        Assert.True(placement.Assessable);
        Assert.True(placement.Truncated, placement.Describe());
        Assert.True(placement.EdgeDensityRatio > 0.9, placement.Describe());
        Assert.Equal(50000, placement.Rows);
    }

    [Fact]
    public void ACentredPpmRunLooksHealthyAndIsReportedInPpm()
    {
        (double[] delta, double[] mz) = PpmRun(
            Clustered(0, 0.25, 1.0, n: 50000), tolerancePpm: 10);

        WindowPlacement placement = WindowPlacement.MeasurePpm(delta, mz, 10);

        Assert.False(placement.Suspect, placement.Describe());
        Assert.Equal(WindowUnit.Ppm, placement.Unit);
        Assert.Equal(10, placement.HalfWindow);

        // The mode is a position inside a ppm window, so reporting it in Th would be wrong.
        string described = placement.Describe();
        Assert.Contains(" ppm from the window centre", described);
        Assert.DoesNotContain(" Th", described);
    }

    [Fact]
    public void APpmWarningNamesThePpmToleranceAndSaysTheAnchorDidNotRun()
    {
        (double[] delta, double[] mz) = PpmRun(Uniform(1.0, n: 50000), tolerancePpm: 10);

        string? warning = WindowPlacement.MeasurePpm(delta, mz, 10).Warning();

        Assert.NotNull(warning);
        Assert.Contains("+/-10 ppm window", warning);
        Assert.Contains("--tolerance-ppm", warning);
        Assert.Contains("does not run for a ppm window", warning);
    }

    // --mz-shift works with --tolerance-ppm. The rows then sit around the shift, and judging
    // them from zero would put every one of them outside the window.
    [Fact]
    public void APpmRunMatchedAtAForcedShiftIsJudgedFromThatShift()
    {
        (double[] delta, double[] mz) = PpmRun(
            Clustered(0, 0.25, 1.0, n: 50000), tolerancePpm: 10, shiftTh: -0.5);

        WindowPlacement placement = WindowPlacement.MeasurePpm(delta, mz, 10, centreTh: -0.5);

        Assert.True(placement.Assessable);
        Assert.False(placement.Suspect, placement.Describe());
        Assert.Equal(-0.5, placement.CentreTh);
        Assert.False(WindowPlacement.MeasurePpm(delta, mz, 10).Assessable);
    }

    // ---- The HTML report ----------------------------------------------------------------

    // The figures are what will not show a wrong-peak match, so the warning has to come
    // before them - in the HTML as well as the text summary.
    [Fact]
    public void TheHtmlReportPutsASuspectWindowAboveTheFigures()
    {
        var placements = new Dictionary<string, WindowPlacement>
        {
            ["good.mzML"] = WindowPlacement.Measure(Clustered(0, 0.08, 0.3), 0.3),
            ["bad.mzML"] = WindowPlacement.Measure(Uniform(0.3), 0.3),
        };

        string html = WriteHtml(placements);

        int window = html.IndexOf("Matching window", StringComparison.Ordinal);
        int firstFigure = html.IndexOf("Mass error distribution", StringComparison.Ordinal);
        Assert.True(window >= 0, "no matching-window block");
        Assert.True(window < firstFigure, "the matching-window block comes after the figures");

        Assert.Contains("verdict alert", html);
        Assert.Contains("Treat every figure in this report as unreliable", html);
        Assert.Contains("bad.mzML", html);
        Assert.Contains("good.mzML", html);
    }

    // A clean run is still reported - a saved report should show the check ran - but not
    // as an alert.
    [Fact]
    public void TheHtmlReportRecordsAHealthyWindowWithoutAlarm()
    {
        var placements = new Dictionary<string, WindowPlacement>
        {
            ["good.mzML"] = WindowPlacement.Measure(Clustered(0, 0.08, 0.3), 0.3),
        };

        string html = WriteHtml(placements);

        Assert.Contains("Matching window", html);
        Assert.Contains("edge density", html);
        Assert.DoesNotContain("verdict alert", html);
        Assert.DoesNotContain("unreliable", html);
    }

    private static string WriteHtml(IReadOnlyDictionary<string, WindowPlacement> placements)
    {
        var random = new Random(7);
        int n = 600;
        var error = new double[n];
        var mz = new double[n];
        var rt = new double[n];
        for (int i = 0; i < n; i++)
        {
            error[i] = (random.NextDouble() - 0.5) * 0.3;
            mz[i] = 400 + (random.NextDouble() * 600);
            rt[i] = random.NextDouble() * 60;
        }

        var data = new MARS.Report.QcHtmlReport.Data
        {
            ErrorBefore = error,
            ErrorAfter = Array.Empty<double>(),
            RetentionTime = rt,
            FragmentMz = mz,
            Features = Array.Empty<(string, double[])>(),
            ImportanceNames = Array.Empty<string>(),
            Importance = Array.Empty<double>(),
        };

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".html");
        try
        {
            MARS.Report.QcHtmlReport.Write(
                path, data, statistics: null,
                new MatchStatistics { SpectraSeen = 10, FragmentsMatched = 600 },
                new[] { "good.mzML", "bad.mzML" }, "0.3 Th", "26.3.0",
                uncorrected: null, scale: null, placementByFile: placements);
            return System.IO.File.ReadAllText(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
