// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
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
        Assert.True(Math.Abs(placement.ModeTh) < 0.1, $"mode {placement.ModeTh}");
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
}
