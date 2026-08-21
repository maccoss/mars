// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using MARS.Core;
using Xunit;

namespace MARS.Test;

public sealed class PeptideFoldsTest
{
    private static int[] GroupsOf(params int[] groups) => groups;

    [Fact]
    public void EveryRowOfAPeptideLandsInOneFold()
    {
        // 12 peptides, a different number of rows each, deliberately interleaved so a
        // row-order split would scatter them.
        var groupOfRow = new List<int>();
        for (int row = 0; row < 600; row++) groupOfRow.Add(row % 12);

        (int[] foldOfRow, int groupCount) = PeptideFolds.AssignFolds(groupOfRow.ToArray(), folds: 5);

        Assert.Equal(12, groupCount);

        var foldOfGroup = new Dictionary<int, int>();
        for (int row = 0; row < groupOfRow.Count; row++)
        {
            int group = groupOfRow[row];
            if (foldOfGroup.TryGetValue(group, out int fold))
            {
                // This is the property the whole design rests on. A peptide split across
                // folds lets the model memorize its fragment m/z values and report an
                // accuracy it cannot reach on anything new.
                Assert.Equal(fold, foldOfRow[row]);
            }
            else
            {
                foldOfGroup[group] = foldOfRow[row];
            }
        }
    }

    [Fact]
    public void FoldsAreBalancedAndEveryFoldIsUsed()
    {
        var groupOfRow = new int[1000];
        for (int i = 0; i < groupOfRow.Length; i++) groupOfRow[i] = i / 10; // 100 peptides

        (int[] foldOfRow, _) = PeptideFolds.AssignFolds(groupOfRow, folds: 5);

        var groupsPerFold = new int[5];
        var seen = new HashSet<int>();
        for (int row = 0; row < groupOfRow.Length; row++)
        {
            if (seen.Add(groupOfRow[row])) groupsPerFold[foldOfRow[row]]++;
        }

        Assert.All(groupsPerFold, count => Assert.Equal(20, count));
    }

    [Fact]
    public void AssignmentIsDeterministic()
    {
        var groupOfRow = new int[500];
        for (int i = 0; i < groupOfRow.Length; i++) groupOfRow[i] = (i * 7) % 53;

        (int[] first, _) = PeptideFolds.AssignFolds(groupOfRow, folds: 4);
        (int[] second, _) = PeptideFolds.AssignFolds(groupOfRow, folds: 4);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RefusesFewerThanTwoFolds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PeptideFolds.AssignFolds(GroupsOf(1, 2, 3), folds: 1));
    }

    [Fact]
    public void HeldOutSplitKeepsPeptidesWhole()
    {
        var groupOfRow = new int[1000];
        for (int i = 0; i < groupOfRow.Length; i++) groupOfRow[i] = i / 10;

        (int[] train, int[] validation) = PeptideFolds.SplitByGroup(groupOfRow, 0.2, seed: 42);

        Assert.Equal(groupOfRow.Length, train.Length + validation.Length);
        Assert.Empty(train.Intersect(validation));

        var trainGroups = new HashSet<int>(train.Select(r => groupOfRow[r]));
        var validationGroups = new HashSet<int>(validation.Select(r => groupOfRow[r]));
        Assert.Empty(trainGroups.Intersect(validationGroups));

        // Whole groups, so the fraction is approximate rather than exact.
        Assert.InRange(validation.Length, 150, 250);
    }

    [Fact]
    public void HeldOutSplitOfZeroKeepsEverythingForTraining()
    {
        var groupOfRow = new int[100];
        for (int i = 0; i < groupOfRow.Length; i++) groupOfRow[i] = i / 5;

        (int[] train, int[] validation) = PeptideFolds.SplitByGroup(groupOfRow, 0, seed: 42);

        Assert.Equal(100, train.Length);
        Assert.Empty(validation);
    }

    [Fact]
    public void MeasureReportsResidualAccuracy()
    {
        var observed = new double[] { 1.0, 2.0, 3.0, 4.0 };
        var predicted = new double[] { 1.1, 1.9, 3.1, 3.9 };

        FoldMetrics metrics = PeptideFolds.Measure(observed, predicted);

        Assert.Equal(4, metrics.Rows);
        Assert.Equal(0.1, metrics.Mad, 6);
        Assert.Equal(0.1, metrics.Rms, 6);

        // Near 1 but not 1: the residuals alternate sign, so the predictions are not a
        // linear transform of the observations.
        Assert.Equal(0.9965, metrics.PearsonR, 4);
    }

    [Fact]
    public void CorrelationIsOneForAnyPositiveLinearTransform()
    {
        var observed = new double[] { 1.0, 2.0, 3.0, 4.0 };
        var predicted = new double[] { 0.5, 1.5, 2.5, 3.5 };

        // Shifted by a constant, so the model has a bias but tracks the error perfectly.
        // Pearson r cannot see the bias; that is what the median residual is for.
        FoldMetrics metrics = PeptideFolds.Measure(observed, predicted);
        Assert.Equal(1.0, metrics.PearsonR, 10);
        Assert.Equal(0.5, metrics.Median, 10);
    }

    [Fact]
    public void PerfectPredictionLeavesNoResidual()
    {
        var observed = new double[] { -0.05, 0.02, 0.11, -0.2, 0.07 };
        FoldMetrics metrics = PeptideFolds.Measure(observed, observed);

        Assert.Equal(0.0, metrics.Mad, 12);
        Assert.Equal(0.0, metrics.Rms, 12);
    }

    [Fact]
    public void AConstantPredictionHasNoCorrelationToReport()
    {
        var observed = new double[] { 1.0, 2.0, 3.0, 4.0 };
        var predicted = new double[] { 2.5, 2.5, 2.5, 2.5 };

        // Not zero: a constant has no variance, so the correlation is undefined rather than
        // absent. Reporting 0 would read as "measured, and there is no relationship".
        Assert.True(double.IsNaN(PeptideFolds.Measure(observed, predicted).PearsonR));
    }

    [Fact]
    public void SpreadAcrossFoldsIsTheSampleStandardDeviation()
    {
        FoldMetrics Fold(double mad) => new()
        {
            Rows = 100, Mad = mad, Rms = mad, StdDev = mad, Median = 0,
            PearsonR = 0.5, MadBefore = 1.0,
        };

        var report = new CrossValidationReport
        {
            Folds = 3,
            Groups = 30,
            PerFold = new[] { Fold(0.10), Fold(0.12), Fold(0.14) },
            OutOfFold = Fold(0.12),
            InSample = Fold(0.10),
        };

        Assert.Equal(0.02, report.MadSpread, 6);
        Assert.Equal(0.02, report.OptimismMad, 6);
    }
}

public sealed class CrossValidatedFitTest
{
    private static MatchTable BuildMatches(int peptides = 200, int rowsEach = 10)
    {
        MarsFeature[] collect =
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz,
            MarsFeature.LogTic, MarsFeature.LogIntensity,
        };

        var table = new MatchTable(collect);
        var random = new Random(20260821);

        for (int p = 0; p < peptides; p++)
        {
            // One fragment m/z per peptide, repeated across spectra. This is what makes a
            // row-random split leak: the same m/z would appear on both sides of it.
            double fragmentMz = 200.0 + (random.NextDouble() * 1000.0);
            for (int r = 0; r < rowsEach; r++)
            {
                double intensity = 500.0 + (random.NextDouble() * 100000.0);
                table.Set(MarsFeature.PrecursorMz, 400.0 + (p % 20));
                table.Set(MarsFeature.FragmentMz, fragmentMz);
                table.Set(MarsFeature.LogTic, Math.Log10(1.0e6));
                table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));
                table.DeltaMz.Add((fragmentMz * 2.0e-5) - 0.01 + ((random.NextDouble() - 0.5) * 0.004));
                table.ObservedIntensity.Add(intensity);
                table.PeptideGroup.Add(p);
                table.CommitRow();
            }
        }

        return table;
    }

    [Fact]
    public void ProducesOneModelPerFoldAndReportsEveryOne()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        Assert.Equal(5, calibrator.Models.Count);

        CrossValidationReport cv = calibrator.CrossValidation!;
        Assert.Equal(5, cv.Folds);
        Assert.Equal(200, cv.Groups);
        Assert.Equal(5, cv.PerFold.Length);

        // Every row got exactly one out-of-fold prediction, so the pooled count is the
        // total. A fold that silently scored nothing would show up here.
        Assert.Equal(2000, cv.OutOfFold.Rows);
        Assert.Equal(2000, cv.PerFold.Sum(f => f.Rows));
    }

    [Fact]
    public void ReportedAccuracyIsOutOfFoldNotInSample()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        CrossValidationReport cv = calibrator.CrossValidation!;
        TrainingStatistics stats = calibrator.Statistics!;

        // The headline "after" figure has to be the honest one. If these ever diverge, the
        // report is quoting an accuracy the model does not have on unseen peptides.
        Assert.Equal(cv.OutOfFold.Mad, stats.After.Mad, 12);

        // And in-sample must be at least as good, or the arithmetic is wrong somewhere.
        Assert.True(cv.InSample.Mad <= cv.OutOfFold.Mad + 1e-12,
            $"in-sample {cv.InSample.Mad:R} should not be worse than out-of-fold {cv.OutOfFold.Mad:R}");
    }

    [Fact]
    public void SingleFitStillWorksAndCarriesNoCrossValidation()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 0, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        Assert.Single(calibrator.Models);
        Assert.Null(calibrator.CrossValidation);
        Assert.NotNull(calibrator.Statistics);
    }

    [Fact]
    public void EnsemblePredictionIsTheMeanOfTheFoldModels()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 3, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        var row = new double[calibrator.Features.Count];
        row[calibrator.Features.SlotOf(MarsFeature.PrecursorMz)] = 405.0;
        row[calibrator.Features.SlotOf(MarsFeature.FragmentMz)] = 650.0;
        row[calibrator.Features.SlotOf(MarsFeature.LogTic)] = 6.0;
        row[calibrator.Features.SlotOf(MarsFeature.LogIntensity)] = 4.0;

        double mean = calibrator.Models.Sum(m => m.ScoreSingle(row)) / calibrator.Models.Count;
        Assert.Equal(mean, calibrator.PredictDelta(row), 12);
    }

    [Fact]
    public void RefusesToFoldFewerPeptidesThanFolds()
    {
        MatchTable table = BuildMatches(peptides: 3, rowsEach: 50);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => MzCalibrator.Fit(table, new CalibrationOptions { CvFolds = 5 }, absoluteTimeOffset: 0));

        Assert.Contains("cv-folds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesATableMissingItsPeptideColumn()
    {
        MarsFeature[] collect =
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz,
            MarsFeature.LogTic, MarsFeature.LogIntensity,
        };

        var table = new MatchTable(collect);
        for (int i = 0; i < 100; i++)
        {
            table.Set(MarsFeature.PrecursorMz, 400.0);
            table.Set(MarsFeature.FragmentMz, 600.0 + i);
            table.Set(MarsFeature.LogTic, 6.0);
            table.Set(MarsFeature.LogIntensity, 4.0);
            table.DeltaMz.Add(0.01);
            table.ObservedIntensity.Add(1000.0);
            // PeptideGroup deliberately not filled.
            table.CommitRow();
        }

        // Falling back to a row-random split here would silently report an accuracy the
        // model cannot reach, which is worse than refusing.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => MzCalibrator.Fit(table, new CalibrationOptions(), absoluteTimeOffset: 0));

        Assert.Contains("peptide group", error.Message, StringComparison.Ordinal);
    }
}
