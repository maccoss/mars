// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using MARS.Cli;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

/// <summary>
/// Ion injection time is only a feature when it varies.
/// </summary>
/// <remarks>
/// A trap sets it per spectrum from its automatic gain control, so it says how full the trap
/// was. A Bruker or Sciex TOF accumulates for a fixed period, so every spectrum carries the
/// same number: <c>injection_time</c> becomes a constant, which a tree can never split on, and
/// <c>tic_injection_time</c> becomes TIC times that constant - <c>log_tic</c> rescaled, and a
/// duplicate that splits permutation importance with the feature it duplicates.
/// </remarks>
public class InjectionTimeTest
{
    [Fact]
    public void AVaryingInjectionTimeIsUsed() =>
        Assert.Equal(InjectionTimeUse.Varying, Probe(constant: false));

    [Fact]
    public void AConstantInjectionTimeIsNotUsed() =>
        Assert.Equal(InjectionTimeUse.Constant, Probe(constant: true));

    /// <summary>Both cases that are not "varying" drop the feature group.</summary>
    [Theory]
    [InlineData(InjectionTimeUse.Varying, true)]
    [InlineData(InjectionTimeUse.Constant, false)]
    [InlineData(InjectionTimeUse.Absent, false)]
    public void OnlyAVaryingInjectionTimeTurnsTheGroupOn(InjectionTimeUse use, bool expected) =>
        Assert.Equal(expected, CalibrateCommand.UseInjectionTime(use));

    /// <summary>
    /// The group is both features or neither: a constant injection time makes
    /// tic_injection_time useless in exactly the same way it makes injection_time useless.
    /// </summary>
    [Fact]
    public void TheGroupIsBothFeaturesOrNeither()
    {
        MarsFeature[] on = FragmentMatcher.CollectedFeatures(true, rfa2: false, rfc2: false);
        MarsFeature[] off = FragmentMatcher.CollectedFeatures(false, rfa2: false, rfc2: false);

        Assert.Contains(MarsFeature.InjectionTime, on);
        Assert.Contains(MarsFeature.TicInjectionTime, on);
        Assert.DoesNotContain(MarsFeature.InjectionTime, off);
        Assert.DoesNotContain(MarsFeature.TicInjectionTime, off);
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
