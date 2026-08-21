// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
    public void TheAfterFiguresDescribeTheDataThatWasActuallyCorrected()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        CrossValidationReport cv = calibrator.CrossValidation!;
        TrainingStatistics stats = calibrator.Statistics!;

        // The headline "after" figure describes the applied model on the rows it was fitted
        // to, which is what the corrected files will look like when re-matched. Quoting the
        // out-of-fold number here would understate what the correction achieved.
        Assert.Equal(cv.InSample.Mad, stats.After.Mad, 12);

        // And the out-of-fold estimate must not be better than the in-sample one, or the
        // arithmetic is wrong somewhere.
        Assert.True(cv.InSample.Mad <= cv.OutOfFold.Mad + 1e-12,
            $"in-sample {cv.InSample.Mad:R} should not be worse than out-of-fold {cv.OutOfFold.Mad:R}");
    }

    [Fact]
    public void CrossValidationEstimatesWhatApplyWouldAchieveElsewhere()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        CrossValidationReport cv = calibrator.CrossValidation!;

        // Every row scored by a model that never saw its peptide, and every row scored
        // exactly once.
        Assert.Equal(2000, cv.OutOfFold.Rows);
        Assert.True(cv.OptimismMad >= -1e-12, $"gap should not be negative: {cv.OptimismMad:R}");
    }

    [Fact]
    public void SingleFitStillWorksAndCarriesNoCrossValidation()
    {
        MzCalibrator calibrator = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 0, ImportanceSampleRows = 500 },
            absoluteTimeOffset: 0);

        Assert.Null(calibrator.CrossValidation);
        Assert.NotNull(calibrator.Statistics);
    }

    [Fact]
    public void TheAppliedModelIsAnOrdinarySingleFit()
    {
        MzCalibrator folded = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, NEstimators = 20, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        MzCalibrator single = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 0, NEstimators = 20, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        // Cross-validating does not change what gets applied: one model of the requested
        // size, fitted to every row. The folds are a measurement taken alongside it, so
        // correcting costs the same either way.
        Assert.Equal(20, folded.Model.ToModelData().TreeRoot.Length);
        Assert.Equal(
            single.Model.ToModelData().TreeRoot.Length,
            folded.Model.ToModelData().TreeRoot.Length);
    }

    [Fact]
    public void TwoIdenticalFitsProduceAByteIdenticalModelFile()
    {
        string Fit(int threads)
        {
            MzCalibrator calibrator = MzCalibrator.Fit(
                BuildMatches(),
                new CalibrationOptions { CvFolds = 5, MaxDegreeOfParallelism = threads, ImportanceSampleRows = 0 },
                absoluteTimeOffset: 0);

            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
            try
            {
                MarsModelIo.Save(calibrator, path);

                // The version string is stamped from the assembly and the file is otherwise
                // fully determined by the input, so hashing the whole thing is a fair test.
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        // Same input, same bytes - including across thread counts. MARS writes m/z values
        // into files that get reprocessed and compared downstream, so a model that varied
        // run to run would make every such comparison unreliable.
        string first = Fit(1);
        Assert.Equal(first, Fit(1));
        Assert.Equal(first, Fit(8));
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

public sealed class ResidualTrimTest
{
    /// <summary>
    /// A clean linear relationship, plus a contaminated minority whose label is unrelated to
    /// its features - the shape a mismatched peak takes, where the recorded delta belongs to
    /// some other ion.
    /// </summary>
    private static MatchTable BuildContaminated(double contaminatedFraction)
    {
        MarsFeature[] collect =
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz,
            MarsFeature.LogTic, MarsFeature.LogIntensity,
        };

        var table = new MatchTable(collect);
        var random = new Random(20260823);
        const int peptides = 300, rowsEach = 10;

        for (int p = 0; p < peptides; p++)
        {
            double fragmentMz = 200.0 + (random.NextDouble() * 1000.0);
            for (int r = 0; r < rowsEach; r++)
            {
                double intensity = 500.0 + (random.NextDouble() * 100000.0);
                bool contaminated = random.NextDouble() < contaminatedFraction;

                table.Set(MarsFeature.PrecursorMz, 400.0 + (p % 20));
                table.Set(MarsFeature.FragmentMz, fragmentMz);
                table.Set(MarsFeature.LogTic, Math.Log10(1.0e6));
                table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));

                double truth = (fragmentMz * 6.0e-5) - 0.02;
                table.DeltaMz.Add(contaminated
                    // Uniform across the matching window: what you get when the most intense
                    // peak in the window was not the fragment.
                    ? (random.NextDouble() - 0.5) * 0.6
                    : truth + ((random.NextDouble() - 0.5) * 0.004));

                table.ObservedIntensity.Add(intensity);
                table.PeptideGroup.Add(p);
                table.CommitRow();
            }
        }

        return table;
    }

    [Fact]
    public void TrimmingImprovesAccuracyOnContaminatedData()
    {
        var options = new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 0 };

        MzCalibrator without = MzCalibrator.Fit(
            BuildContaminated(0.15), new CalibrationOptions
            {
                CvFolds = 5, ImportanceSampleRows = 0, TrimResidualSigma = 0,
            },
            absoluteTimeOffset: 0);

        MzCalibrator with = MzCalibrator.Fit(
            BuildContaminated(0.15), options, absoluteTimeOffset: 0);

        // Out-of-fold, and the held-out rows are scored in full either way - the contaminated
        // ones included. So this is a real improvement, not the measurement getting easier.
        Assert.True(
            with.CrossValidation!.OutOfFold.Mad < without.CrossValidation!.OutOfFold.Mad,
            $"trimmed {with.CrossValidation.OutOfFold.Mad:R} should beat " +
            $"untrimmed {without.CrossValidation.OutOfFold.Mad:R}");
    }

    [Fact]
    public void TrimmingIsOffWhenTheSigmaIsZero()
    {
        MzCalibrator a = MzCalibrator.Fit(
            BuildContaminated(0.1),
            new CalibrationOptions { CvFolds = 0, ImportanceSampleRows = 0, TrimResidualSigma = 0 },
            absoluteTimeOffset: 0);

        MzCalibrator b = MzCalibrator.Fit(
            BuildContaminated(0.1),
            new CalibrationOptions { CvFolds = 0, ImportanceSampleRows = 0, TrimResidualSigma = 0 },
            absoluteTimeOffset: 0);

        // Deterministic, and identical to itself: disabling the second pass must not leave
        // any residue of it.
        Assert.Equal(a.Statistics!.After.Mad, b.Statistics!.After.Mad, 12);
    }

    [Fact]
    public void CleanDataIsBarelyTrimmedAtAll()
    {
        MzCalibrator clean = MzCalibrator.Fit(
            BuildContaminated(0.0),
            new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        MzCalibrator dirty = MzCalibrator.Fit(
            BuildContaminated(0.2),
            new CalibrationOptions { CvFolds = 5, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        // The threshold is in robust sigma, so it adapts: clean data has a tight residual
        // distribution and loses almost nothing, while contaminated data has a wide one and
        // the tail is what gets cut. A fixed Th threshold would not do that.
        Assert.True(clean.CrossValidation!.OutOfFold.Mad < dirty.CrossValidation!.OutOfFold.Mad);
    }
}
