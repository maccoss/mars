// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using MARS.Core;
using Xunit;

namespace MARS.Test;

public sealed class PeakSearchTest
{
    private static readonly double[] Mz = { 100.0, 100.4, 100.5, 101.0, 101.5, 102.0, 103.5, 104.0 };
    private static readonly double[] Intensity = { 10, 500, 300, 700, 200, 900, 50, 400 };

    [Fact]
    public void FindsTheMostIntensePeakNotTheClosest()
    {
        // 100.4 is nearer to the target than 100.5, but MARS takes the most intense peak in
        // the window: a stronger peak has a better determined centroid.
        bool found = PeakSearch.TryFindMostIntensePeak(
            100.45, Mz, Intensity, toleranceTh: 0.1, minIntensity: 0, tolerancePpm: 0,
            out double mz, out double intensity);

        Assert.True(found);
        Assert.Equal(100.4, mz);
        Assert.Equal(500, intensity);
    }

    [Fact]
    public void TiesResolveToTheLowestMz()
    {
        var mz = new[] { 500.0, 500.1, 500.2 };
        var intensity = new[] { 100.0, 100.0, 100.0 };

        bool found = PeakSearch.TryFindMostIntensePeak(
            500.1, mz, intensity, 0.5, 0, 0, out double best, out _);

        Assert.True(found);
        Assert.Equal(500.0, best);
    }

    [Fact]
    public void MinimumIntensityFiltersCandidates()
    {
        bool found = PeakSearch.TryFindMostIntensePeak(
            100.2, Mz, Intensity, toleranceTh: 0.25, minIntensity: 600, tolerancePpm: 0,
            out _, out _);
        Assert.False(found);

        Assert.True(PeakSearch.TryFindMostIntensePeak(
            100.2, Mz, Intensity, 0.25, 400, 0, out double mz, out _));
        Assert.Equal(100.4, mz);
    }

    [Fact]
    public void PpmToleranceOverridesAbsoluteTolerance()
    {
        var mz = new[] { 999.99, 1000.0, 1000.02 };
        var intensity = new[] { 100.0, 50.0, 400.0 };

        // 10 ppm at 1000 Th is 0.01 Th, so 1000.02 is out of range even though it is the
        // most intense peak.
        Assert.True(PeakSearch.TryFindMostIntensePeak(
            1000.0, mz, intensity, toleranceTh: 5.0, minIntensity: 0, tolerancePpm: 10,
            out double found, out _));
        Assert.Equal(999.99, found);
    }

    [Fact]
    public void RangeSumIsExclusiveLowAndInclusiveHigh()
    {
        // (100.4, 101.0] holds 100.5 and 101.0 but not 100.4.
        double sum = PeakSearch.SumIntensityInRange(Mz, Intensity, 100.4, 101.0);
        Assert.Equal(300 + 700, sum);

        Assert.Equal(0.0, PeakSearch.SumIntensityInRange(Mz, Intensity, 105.0, 106.0));
    }

    /// <summary>
    /// The sweep used when correcting must produce exactly what the per-fragment binary
    /// search produces when training, or training and inference would see different values
    /// for the same feature.
    /// </summary>
    [Fact]
    public void SweepMatchesPerPeakRangeSums()
    {
        var random = new Random(4242);
        var mz = new double[400];
        var intensity = new double[400];
        double value = 200.0;
        for (var i = 0; i < mz.Length; i++)
        {
            value += 0.05 + (random.NextDouble() * 3.0);
            mz[i] = value;
            intensity[i] = random.NextDouble() * 10000.0;
        }

        foreach ((double low, double high) in MarsFeatures.NeighborWindows)
        {
            var swept = new double[mz.Length];
            PeakSearch.ComputeNeighborWindow(mz, intensity, low, high, swept);

            for (var i = 0; i < mz.Length; i++)
            {
                double expected = PeakSearch.SumIntensityInRange(mz, intensity, mz[i] + low, mz[i] + high);
                Assert.Equal(expected, swept[i]);
            }
        }
    }
}

public sealed class StatisticsTest
{
    [Fact]
    public void MedianInterpolatesForEvenCounts()
    {
        Assert.Equal(2.5, MarsStatistics.Median(new[] { 1.0, 2.0, 3.0, 4.0 }));
        Assert.Equal(3.0, MarsStatistics.Median(new[] { 1.0, 2.0, 3.0, 4.0, 100.0 }));
    }

    [Fact]
    public void MedianAbsoluteDeviationIsAboutTheMedian()
    {
        // median is 3; absolute deviations are 2,1,0,1,2 so the MAD is 1.
        Assert.Equal(1.0, MarsStatistics.MedianAbsoluteDeviation(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 }));
    }

    [Fact]
    public void StandardDeviationUsesTheSampleConvention()
    {
        // ddof = 1, matching pandas Series.std, which the Python report used.
        double[] values = { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 };
        Assert.Equal(2.13809, MarsStatistics.StdDev(values), 5);
    }

    [Fact]
    public void SummaryReportsEveryScale()
    {
        double[] values = { -0.1, 0.0, 0.1, 0.2 };
        ErrorSummary summary = MarsStatistics.Summarize(values);

        Assert.Equal(4, summary.Count);
        Assert.Equal(0.05, summary.Mean, 12);
        Assert.Equal(0.05, summary.Median, 12);
        Assert.Equal(0.1, summary.Mae, 12);
    }
}

public sealed class PeptideMassTest
{
    /// <summary>
    /// Reference values for the y and b series of a peptide, computed from the standard
    /// monoisotopic residue masses.
    /// </summary>
    [Fact]
    public void FragmentMzMatchesKnownValues()
    {
        const string peptide = "LLQDANYNVEK";

        // Singly protonated y ions.
        Assert.Equal(147.11280, PeptideMass.FragmentMz(peptide, 'y', 1, 1), 4);
        Assert.Equal(276.15539, PeptideMass.FragmentMz(peptide, 'y', 2, 1), 4);

        // Singly protonated b ions.
        Assert.Equal(227.17540, PeptideMass.FragmentMz(peptide, 'b', 2, 1), 4);

        // The precursor: neutral peptide plus two protons over two charges.
        double neutral = 0;
        foreach (char residue in peptide) neutral += PeptideMass.Residue(residue);
        neutral += PeptideMass.Water;
        double doubly = (neutral + (2 * PeptideMass.Proton)) / 2;
        Assert.Equal(653.8355, doubly, 3);
    }

    [Fact]
    public void ChargeTwoHalvesTheNeutralMass()
    {
        double singly = PeptideMass.FragmentMz("PEPTIDEK", 'y', 4, 1);
        double doubly = PeptideMass.FragmentMz("PEPTIDEK", 'y', 4, 2);
        Assert.Equal((singly + PeptideMass.Proton) / 2, doubly, 9);
    }

    [Fact]
    public void ModificationsApplyOnlyInsideTheFragment()
    {
        const string peptide = "ACDEFGHIK";
        var carbamidomethyl = new List<(int, double)> { (2, 57.021464) };

        // C is residue 2, so it is inside b3 but outside y3.
        double b3Plain = PeptideMass.FragmentMz(peptide, 'b', 3, 1);
        double b3Modified = PeptideMass.FragmentMz(peptide, 'b', 3, 1, carbamidomethyl);
        Assert.Equal(b3Plain + 57.021464, b3Modified, 9);

        double y3Plain = PeptideMass.FragmentMz(peptide, 'y', 3, 1);
        double y3Modified = PeptideMass.FragmentMz(peptide, 'y', 3, 1, carbamidomethyl);
        Assert.Equal(y3Plain, y3Modified, 9);
    }

    [Fact]
    public void ModifiedSequencesSplitIntoResiduesAndDeltas()
    {
        (string stripped, List<(int Position, double Mass)> modifications) =
            PeptideMass.SplitModifiedSequence("LSC[+57.021464]AASGFTFSSYAM[+15.994915]SWVR");

        Assert.Equal("LSCAASGFTFSSYAMSWVR", stripped);
        Assert.Equal(2, modifications.Count);
        Assert.Equal(3, modifications[0].Position);
        Assert.Equal(57.021464, modifications[0].Mass, 9);
        Assert.Equal(15, modifications[1].Position);
    }

    [Fact]
    public void UnknownResiduesYieldNaNRatherThanAWrongMass()
    {
        Assert.True(double.IsNaN(PeptideMass.FragmentMz("PEPXIDE", 'y', 5, 1)));
        Assert.True(double.IsNaN(PeptideMass.FragmentMz("PEPTIDE", 'y', 99, 1)));
        Assert.True(double.IsNaN(PeptideMass.FragmentMz("PEPTIDE", 'q', 3, 1)));
    }
}

public sealed class FeatureSetTest
{
    [Fact]
    public void FeatureNamesAreTheOnDiskContract()
    {
        // These strings are what a model file records; changing one silently invalidates
        // every model written before the change.
        Assert.Equal("precursor_mz", MarsFeatures.NameOf(MarsFeature.PrecursorMz));
        Assert.Equal("ions_above_0_1", MarsFeatures.NameOf(MarsFeature.IonsAbove01));
        Assert.Equal("adjacent_ratio_below_2_3", MarsFeatures.NameOf(MarsFeature.AdjacentRatioBelow23));
        Assert.Equal(MarsFeatures.Count, MarsFeatures.Names.Length);
    }

    [Fact]
    public void NeighborWindowsUseTheDocumentedHalfThOffsets()
    {
        // Windows sit half a Th off the isotope spacing so each one centres on an isotope
        // peak rather than straddling two.
        Assert.Equal((0.5, 1.5), MarsFeatures.NeighborWindows[0]);
        Assert.Equal((-1.5, -0.5), MarsFeatures.NeighborWindows[3]);
        Assert.Equal(MarsFeatures.NeighborFeatures.Length, MarsFeatures.RatioFeatures.Length);
        Assert.Equal(MarsFeatures.NeighborWindows.Length, MarsFeatures.NeighborFeatures.Length);
    }

    [Fact]
    public void SlotLookupFollowsTheDeclaredOrder()
    {
        var set = new FeatureSet(new[] { MarsFeature.FragmentMz, MarsFeature.LogTic, MarsFeature.Rfa2Temp });

        Assert.Equal(0, set.SlotOf(MarsFeature.FragmentMz));
        Assert.Equal(2, set.SlotOf(MarsFeature.Rfa2Temp));
        Assert.Equal(-1, set.SlotOf(MarsFeature.PrecursorMz));
        Assert.False(set.NeedsNeighborDensity);

        var withNeighbors = new FeatureSet(new[] { MarsFeature.FragmentMz, MarsFeature.IonsAbove12 });
        Assert.True(withNeighbors.NeedsNeighborDensity);
    }

    [Fact]
    public void UnknownFeatureNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => FeatureSet.FromNames(new[] { "precursor_mz", "not_a_feature" }));
        Assert.Throws<ArgumentException>(() =>
            new FeatureSet(new[] { MarsFeature.LogTic, MarsFeature.LogTic }));
    }
}
