// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using MARS.Cli;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

/// <summary>
/// Ion injection time is only a feature when it varies - but the features scaled by it are
/// features whenever it exists.
/// </summary>
/// <remarks>
/// <para>
/// A trap sets it per spectrum from its automatic gain control, so it says how full the trap
/// was. A Bruker or Sciex TOF accumulates for a fixed period, so every spectrum carries the
/// same number: <c>injection_time</c> becomes a constant, which a tree can never split on, and
/// <c>tic_injection_time</c> becomes TIC times that constant - <c>log_tic</c> rescaled, and a
/// duplicate that splits permutation importance with the feature it duplicates.
/// </para>
/// <para>
/// The ion-population features are a separate question, and getting the two confused was
/// expensive. They are peak sums over m/z windows, multiplied by the injection time to turn a
/// rate into a count. A constant injection time scales them all by the same factor, which
/// leaves every one of them varying and every split available. Dropping them alongside the
/// injection time took the Stellar reference cohort from 18 features to 5 and its corrected
/// MAD from 0.0463 Th to 0.0581 - on the instrument MARS was written for.
/// </para>
/// </remarks>
public class InjectionTimeTest
{
    [Fact]
    public void AVaryingInjectionTimeIsUsed() =>
        Assert.Equal(InjectionTimeUse.Varying, Probe(constant: false));

    [Fact]
    public void AConstantInjectionTimeIsNotUsed() =>
        Assert.Equal(InjectionTimeUse.Constant, Probe(constant: true));

    /// <summary>
    /// Collection is not the decision. A run that records an injection time gets every column
    /// that depends on one, and whether the injection time itself earns a place is settled
    /// later from the whole column.
    /// </summary>
    [Theory]
    [InlineData(InjectionTimeUse.Varying)]
    [InlineData(InjectionTimeUse.Constant)]
    public void ARunThatRecordsAnInjectionTimeCollectsEveryColumnThatNeedsOne(InjectionTimeUse use)
    {
        MarsFeature[] features = FragmentMatcher.CollectedFeatures(use, rfa2: false, rfc2: false);

        Assert.Contains(MarsFeature.InjectionTime, features);
        Assert.Contains(MarsFeature.TicInjectionTime, features);
        Assert.Contains(MarsFeature.FragmentIons, features);
        foreach (MarsFeature f in MarsFeatures.NeighborFeatures) Assert.Contains(f, features);
        foreach (MarsFeature f in MarsFeatures.RatioFeatures) Assert.Contains(f, features);
    }

    /// <summary>
    /// A column that is flat over its first few hundred rows and moves later has to read as
    /// varying. This is the case that was wrong, and it is not a corner case: an ion trap sits
    /// at the method's ceiling for the entire void volume, so every real gradient looks like
    /// this. Judged on its head, a standard Stellar DIA run reads as constant while two thirds
    /// of its spectra are off the ceiling.
    /// </summary>
    [Fact]
    public void AColumnThatOnlyMovesLaterInTheRunStillVaries()
    {
        var table = new MatchTable(new[] { MarsFeature.InjectionTime });

        for (var i = 0; i < 5000; i++)
        {
            // Flat for the first 4,000 rows, then the trap starts filling.
            table.Set(MarsFeature.InjectionTime, i < 4000 ? 10.0 : 10.0 - ((i - 4000) * 0.001));
            Row(table);
        }

        Assert.True(table.Varies(MarsFeature.InjectionTime));
    }

    [Fact]
    public void AGenuinelyConstantColumnDoesNotVary()
    {
        var table = new MatchTable(new[] { MarsFeature.InjectionTime });
        for (var i = 0; i < 1000; i++)
        {
            table.Set(MarsFeature.InjectionTime, 10.672768592834);
            Row(table);
        }

        Assert.False(table.Varies(MarsFeature.InjectionTime));
    }

    /// <summary>A column of nothing does not vary, and must not be reported as though it did.</summary>
    [Fact]
    public void AnAbsentColumnDoesNotVary()
    {
        var table = new MatchTable(new[] { MarsFeature.InjectionTime });
        for (var i = 0; i < 10; i++)
        {
            table.Set(MarsFeature.InjectionTime, double.NaN);
            Row(table);
        }

        Assert.False(table.Varies(MarsFeature.InjectionTime));
        Assert.False(table.AnyFinite(MarsFeature.InjectionTime));
    }

    /// <summary>
    /// End to end through the fit: a constant injection time drops itself and
    /// tic_injection_time, and takes nothing else with it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheInjectionTimeItselfLeavesWhenItIsConstant(bool constant)
    {
        MarsFeature[] collect =
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz, MarsFeature.LogTic,
            MarsFeature.LogIntensity, MarsFeature.InjectionTime, MarsFeature.TicInjectionTime,
            MarsFeature.FragmentIons,
        };

        var table = new MatchTable(collect);
        var random = new Random(11);

        for (var i = 0; i < 2000; i++)
        {
            double injection = constant ? 10.0 : 6.0 + (random.NextDouble() * 4.0);
            double intensity = 500 + (random.NextDouble() * 100000);

            table.Set(MarsFeature.PrecursorMz, 400 + (random.NextDouble() * 500));
            table.Set(MarsFeature.FragmentMz, 300 + (random.NextDouble() * 700));
            table.Set(MarsFeature.LogTic, 6.0 + random.NextDouble());
            table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));
            table.Set(MarsFeature.InjectionTime, injection);
            table.Set(MarsFeature.TicInjectionTime, 1e6 * injection);
            table.Set(MarsFeature.FragmentIons, intensity * injection);

            table.DeltaMz.Add(0.01 + (random.NextDouble() * 0.01));
            table.ObservedIntensity.Add(intensity);
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        MzCalibrator calibrator = MzCalibrator.Fit(
            table, new CalibrationOptions { CvFolds = 0, ImportanceSampleRows = 0 }, 0);

        Assert.Equal(!constant, calibrator.Features.Contains(MarsFeature.InjectionTime));
        Assert.Equal(!constant, calibrator.Features.Contains(MarsFeature.TicInjectionTime));

        // The one that must survive either way.
        Assert.True(calibrator.Features.Contains(MarsFeature.FragmentIons));
    }

    private static void Row(MatchTable table)
    {
        table.DeltaMz.Add(0.01);
        table.ObservedIntensity.Add(1000);
        table.PeptideGroup.Add(0);
        table.CommitRow();
    }

    /// <summary>
    /// With no injection time at all they do have to go: there is nothing to turn a rate into
    /// a count with, and the matcher yields NaN for every one of them.
    /// </summary>
    [Fact]
    public void TheIonPopulationFeaturesNeedAnInjectionTimeToExist()
    {
        MarsFeature[] features =
            FragmentMatcher.CollectedFeatures(InjectionTimeUse.Absent, rfa2: false, rfc2: false);

        Assert.DoesNotContain(MarsFeature.FragmentIons, features);
        foreach (MarsFeature f in MarsFeatures.NeighborFeatures) Assert.DoesNotContain(f, features);
        foreach (MarsFeature f in MarsFeatures.RatioFeatures) Assert.DoesNotContain(f, features);
    }

    private static InjectionTimeUse Probe(bool constant)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-inj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 40, chromatogramCount: 0, peaksPerSpectrum: 6,
                constantInjectionTime: constant);

            using var source = new MzMLSpectrumSource(path);
            return CalibrateCommand.ProbeInjectionTime(source);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
