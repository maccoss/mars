// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;
using Xunit;

namespace MARS.Test;

public sealed class CalibrationTest : IDisposable
{
    private readonly string _directory;

    public CalibrationTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-cal-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A synthetic run whose mass error is a known function of two features. The model has
    /// to recover it well enough to cut the spread substantially, or nothing downstream
    /// means anything.
    /// </summary>
    private static MatchTable BuildSyntheticMatches(int rows = 20000, double noise = 0.01)
    {
        MarsFeature[] collect =
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz, MarsFeature.LogTic,
            MarsFeature.LogIntensity, MarsFeature.AbsoluteTime,
        };

        var table = new MatchTable(collect);
        var random = new Random(20260819);

        for (var i = 0; i < rows; i++)
        {
            double fragmentMz = 200.0 + (random.NextDouble() * 1000.0);
            double absoluteTime = random.NextDouble() * 3600.0;
            double intensity = 500.0 + (random.NextDouble() * 100000.0);

            // The truth: error grows with m/z and drifts through the run.
            double truth = (fragmentMz * 2.0e-5) + (absoluteTime * 5.0e-6) - 0.01;
            double error = truth + ((random.NextDouble() - 0.5) * noise);

            table.Set(MarsFeature.PrecursorMz, 400.0 + (i % 20));
            table.Set(MarsFeature.FragmentMz, fragmentMz);
            table.Set(MarsFeature.LogTic, Math.Log10(1.0e6));
            table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));
            table.Set(MarsFeature.AbsoluteTime, absoluteTime);
            table.DeltaMz.Add(error);
            table.ObservedIntensity.Add(intensity);
            // Several rows per peptide, as real matching produces: one peptide is matched in
            // many spectra. Cross-validation keeps them together.
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        return table;
    }

    [Fact]
    public void ModelRecoversAKnownSystematicError()
    {
        MatchTable table = BuildSyntheticMatches();
        var options = new CalibrationOptions { ImportanceSampleRows = 2000 };

        MzCalibrator calibrator = MzCalibrator.Fit(table, options, absoluteTimeOffset: 0);
        TrainingStatistics stats = calibrator.Statistics!;

        Assert.Equal(20000, stats.RowsUsed);

        // The "after" figures are out-of-fold: each row scored by a model that never trained
        // on its peptide. That is a little worse than the in-sample number this threshold
        // used to be set against, and deliberately so - it is what the model achieves on data
        // it has not seen. A collapse to under 40% of the original spread still means the
        // systematic part was found; what is left is the injected noise.
        Assert.True(stats.After.StdDev < 0.40 * stats.Before.StdDev,
            $"spread should collapse: {stats.Before.StdDev:R} -> {stats.After.StdDev:R}");
        Assert.True(Math.Abs(stats.After.Median) < 0.002,
            $"residuals should centre near zero, got {stats.After.Median:R}");

        // fragment_mz and absolute_time carry the signal; the rest is noise.
        double[] importance = stats.PermutationImportance;
        int fragmentSlot = calibrator.Features.SlotOf(MarsFeature.FragmentMz);
        int timeSlot = calibrator.Features.SlotOf(MarsFeature.AbsoluteTime);
        int precursorSlot = calibrator.Features.SlotOf(MarsFeature.PrecursorMz);
        Assert.True(importance[fragmentSlot] > importance[precursorSlot]);
        Assert.True(importance[timeSlot] > importance[precursorSlot]);
    }

    [Fact]
    public void TrainingIsReproducible()
    {
        var options = new CalibrationOptions { ImportanceSampleRows = 0 };

        MzCalibrator first = MzCalibrator.Fit(BuildSyntheticMatches(4000), options, 0);
        MzCalibrator second = MzCalibrator.Fit(BuildSyntheticMatches(4000), options, 0);

        var row = new double[first.Features.Count];
        for (var i = 0; i < row.Length; i++) row[i] = 300.0 + i;

        Assert.Equal(first.PredictDelta(row), second.PredictDelta(row));
    }

    /// <summary>
    /// Histogram threads must not move a single prediction. This is the invariant that lets
    /// MARS use every core without giving up reproducible output.
    /// </summary>
    [Fact]
    public void TrainingIsDeterministicAcrossThreadCounts()
    {
        MzCalibrator single = MzCalibrator.Fit(
            BuildSyntheticMatches(6000),
            new CalibrationOptions { MaxDegreeOfParallelism = 1, ImportanceSampleRows = 0 }, 0);

        MzCalibrator many = MzCalibrator.Fit(
            BuildSyntheticMatches(6000),
            new CalibrationOptions { MaxDegreeOfParallelism = 16, ImportanceSampleRows = 0 }, 0);

        var random = new Random(7);
        for (var trial = 0; trial < 50; trial++)
        {
            var row = new double[single.Features.Count];
            for (var i = 0; i < row.Length; i++) row[i] = random.NextDouble() * 1200.0;
            Assert.Equal(single.PredictDelta(row), many.PredictDelta(row));
        }
    }

    [Fact]
    public void ModelFileRoundTripsExactly()
    {
        MzCalibrator original = MzCalibrator.Fit(
            BuildSyntheticMatches(3000), new CalibrationOptions { ImportanceSampleRows = 500 }, 1.7e9);

        string path = Path.Combine(_directory, "model.json");
        MarsModelIo.Save(original, path);
        MzCalibrator reloaded = MarsModelIo.Load(path);

        Assert.Equal(original.Features.Names(), reloaded.Features.Names());
        Assert.Equal(original.AbsoluteTimeOffset, reloaded.AbsoluteTimeOffset);
        Assert.Equal(original.Options.NEstimators, reloaded.Options.NEstimators);

        var random = new Random(11);
        for (var trial = 0; trial < 100; trial++)
        {
            var row = new double[original.Features.Count];
            for (var i = 0; i < row.Length; i++) row[i] = random.NextDouble() * 2000.0;
            Assert.Equal(original.PredictDelta(row), reloaded.PredictDelta(row));
        }
    }

    /// <summary>
    /// The acquisition-time offset has to survive into the model file. Without it, a
    /// correction run feeds raw Unix timestamps to a model trained on times re-based to the
    /// earliest run, and every inference row lands past the largest value the model saw.
    /// </summary>
    [Fact]
    public void AbsoluteTimeOffsetTravelsWithTheModel()
    {
        const double offset = 1733198754.0;
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildSyntheticMatches(2000), new CalibrationOptions { ImportanceSampleRows = 0 }, offset);

        string path = Path.Combine(_directory, "offset.json");
        MarsModelIo.Save(calibrator, path);

        Assert.Equal(offset, MarsModelIo.Load(path).AbsoluteTimeOffset);
    }

    [Fact]
    public void EmptyTableIsRejected()
    {
        var empty = new MatchTable(new[] { MarsFeature.FragmentMz });
        Assert.Throws<InvalidOperationException>(() => MzCalibrator.Fit(empty, new CalibrationOptions(), 0));
    }

    /// <summary>
    /// Missing values must never reach the model. Osprey.ML maps NaN to bin 0, while
    /// XGBoost learns a per-node default direction, so a NaN slipping through would diverge
    /// from the reference in a way that is very hard to trace.
    /// </summary>
    [Fact]
    public void RowsWithMissingFeaturesAreDropped()
    {
        MarsFeature[] collect = { MarsFeature.FragmentMz, MarsFeature.LogIntensity, MarsFeature.InjectionTime };
        var table = new MatchTable(collect);

        for (var i = 0; i < 4000; i++)
        {
            table.Set(MarsFeature.FragmentMz, 300.0 + (i % 700));
            table.Set(MarsFeature.LogIntensity, 3.0 + ((i % 30) * 0.05));

            // One row in ten has no injection time, as happens when an instrument omits it.
            table.Set(MarsFeature.InjectionTime, i % 10 == 0 ? double.NaN : 0.02);
            table.DeltaMz.Add(0.001 * (i % 7));
            table.ObservedIntensity.Add(1000.0);
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        MzCalibrator calibrator = MzCalibrator.Fit(
            table, new CalibrationOptions { ImportanceSampleRows = 0 }, 0);

        Assert.Equal(4000, calibrator.Statistics!.RowsMatched);
        Assert.Equal(3600, calibrator.Statistics.RowsUsed);
    }
}

public sealed class SpectrumCorrectorTest
{
    private static MzCalibrator FitTrivialModel(out MarsFeature[] features)
    {
        features = new[] { MarsFeature.FragmentMz };
        var table = new MatchTable(features);

        // A constant offset the model can reproduce anywhere.
        for (var i = 0; i < 3000; i++)
        {
            table.Set(MarsFeature.FragmentMz, 200.0 + (i * 0.3));
            table.DeltaMz.Add(0.02);
            table.ObservedIntensity.Add(1000.0);
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        return MzCalibrator.Fit(table, new CalibrationOptions { ImportanceSampleRows = 0 }, 0);
    }

    [Fact]
    public void CorrectionSubtractsThePredictedError()
    {
        MzCalibrator calibrator = FitTrivialModel(out _);
        var corrector = new SpectrumCorrector(calibrator, new CorrectionOptions());

        var spectrum = new SpectrumRecord
        {
            MsLevel = 2,
            PeakCount = 3,
            MzArray = new[] { 400.0, 500.0, 600.0 },
            IntensityArray = new[] { 1000.0, 2000.0, 3000.0 },
            PrecursorMzLow = 399.5,
            PrecursorMzHigh = 400.5,
            PrecursorMzCenter = 400.0,
            SummedIntensity = 6000.0,
            InjectionTime = 0.02,
        };

        var corrected = new double[3];
        SpectrumCorrectionResult result = corrector.Correct(spectrum, null, new CorrectionWorkspace(), corrected);

        Assert.True(result.Corrected);
        for (var i = 0; i < 3; i++)
        {
            // corrected = observed - predicted error, and the error here is a constant 0.02.
            Assert.InRange(spectrum.MzArray[i] - corrected[i], 0.015, 0.025);
        }
    }

    [Fact]
    public void ClampingKeepsTheArrayStrictlyAscending()
    {
        MzCalibrator calibrator = FitTrivialModel(out _);
        var corrector = new SpectrumCorrector(
            calibrator, new CorrectionOptions { Monotonicity = MonotonicityPolicy.ClampAscending });

        // Two peaks a hair apart: any per-peak correction can reorder them.
        var spectrum = new SpectrumRecord
        {
            MsLevel = 2,
            PeakCount = 4,
            MzArray = new[] { 500.0, 500.0000001, 500.0000002, 700.0 },
            IntensityArray = new[] { 100.0, 100.0, 100.0, 100.0 },
            PrecursorMzCenter = 500.0,
            PrecursorMzLow = 499.5,
            PrecursorMzHigh = 500.5,
            SummedIntensity = 400.0,
            InjectionTime = 0.02,
        };

        var corrected = new double[4];
        corrector.Correct(spectrum, null, new CorrectionWorkspace(), corrected);

        for (var i = 1; i < corrected.Length; i++)
        {
            Assert.True(corrected[i] > corrected[i - 1],
                $"m/z array must stay strictly ascending: [{i - 1}]={corrected[i - 1]:R} [{i}]={corrected[i]:R}");
        }
    }

    [Fact]
    public void WideIsolationWindowsAreLeftAlone()
    {
        MzCalibrator calibrator = FitTrivialModel(out _);
        var corrector = new SpectrumCorrector(
            calibrator, new CorrectionOptions { MaxIsolationWindowWidth = 5.0 });

        var spectrum = new SpectrumRecord
        {
            MsLevel = 2,
            PeakCount = 2,
            MzArray = new[] { 400.0, 500.0 },
            IntensityArray = new[] { 1000.0, 1000.0 },
            PrecursorMzLow = 400.0,
            PrecursorMzHigh = 430.0,
            PrecursorMzCenter = 415.0,
            SummedIntensity = 2000.0,
        };

        var corrected = new double[2];
        SpectrumCorrectionResult result = corrector.Correct(spectrum, null, new CorrectionWorkspace(), corrected);

        Assert.False(result.Corrected);
        Assert.Equal(spectrum.MzArray, corrected);
    }

    [Fact]
    public void Ms1SpectraAreNeverCorrected()
    {
        MzCalibrator calibrator = FitTrivialModel(out _);
        var corrector = new SpectrumCorrector(calibrator, new CorrectionOptions());

        var spectrum = new SpectrumRecord
        {
            MsLevel = 1,
            PeakCount = 2,
            MzArray = new[] { 400.0, 500.0 },
            IntensityArray = new[] { 1000.0, 1000.0 },
            SummedIntensity = 2000.0,
        };

        var corrected = new double[2];
        Assert.False(corrector.Correct(spectrum, null, new CorrectionWorkspace(), corrected).Corrected);
        Assert.Equal(spectrum.MzArray, corrected);
    }
}
