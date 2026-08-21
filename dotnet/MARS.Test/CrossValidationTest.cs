// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using MARS.Core;
using pwiz.Osprey.ML;
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

        Assert.Null(calibrator.CrossValidation);
        Assert.NotNull(calibrator.Statistics);
    }

    [Fact]
    public void TheAppliedModelCarriesEveryFoldsTrees()
    {
        MzCalibrator folded = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 5, NEstimators = 20, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        MzCalibrator single = MzCalibrator.Fit(
            BuildMatches(), new CalibrationOptions { CvFolds = 0, NEstimators = 20, ImportanceSampleRows = 0 },
            absoluteTimeOffset: 0);

        // The merged model holds all five folds' trees, which is why it predicts what
        // averaging them would - and why it costs five times as much to score.
        Assert.Equal(
            5 * single.Model.ToModelData().TreeRoot.Length,
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

public sealed class EnsembleMergeTest
{
    /// <summary>Trains k models on deliberately different slices, so they really do differ.</summary>
    private static GradientBoostedTrees[] TrainDifferentModels(int k)
    {
        const int rows = 900;
        var x = new double[rows][];
        var y = new double[rows];
        var random = new Random(20260822);

        for (int i = 0; i < rows; i++)
        {
            double mz = 200.0 + (random.NextDouble() * 1000.0);
            double time = random.NextDouble() * 3600.0;
            x[i] = new[] { mz, time, random.NextDouble(), random.NextDouble() };
            y[i] = (mz * 2.0e-5) + (time * 5.0e-6) - 0.01 + ((random.NextDouble() - 0.5) * 0.01);
        }

        var models = new GradientBoostedTrees[k];
        for (int f = 0; f < k; f++)
        {
            // A different contiguous slice per model, so no two see the same rows in the same
            // order and the trees genuinely diverge.
            // With one model there is nothing to hold out; it trains on everything.
            int holdout = k > 1 ? rows / k : 0;
            int from = f * holdout;
            int count = rows - holdout;
            var slice = new double[count][];
            var labels = new double[count];
            int at = 0;
            for (int i = 0; i < rows; i++)
            {
                if (holdout > 0 && i >= from && i < from + holdout) continue;
                slice[at] = x[i];
                labels[at] = y[i];
                at++;
            }

            models[f] = GradientBoostedTrees.Train(
                slice[..at], labels[..at],
                new GbtParams { Objective = GbtObjective.SquaredError, NTrees = 30, MaxDepth = 4, Seed = (ulong)(f + 1) });
        }

        return models;
    }

    [Fact]
    public void MergedModelPredictsExactlyWhatAveragingWouldEvenWhenTheFoldsDisagree()
    {
        GradientBoostedTrees[] models = TrainDifferentModels(5);
        GradientBoostedTrees merged = EnsembleMerge.Merge(models);

        var random = new Random(11);
        double maxSpread = 0;

        for (int trial = 0; trial < 300; trial++)
        {
            var row = new[]
            {
                200.0 + (random.NextDouble() * 1000.0), random.NextDouble() * 3600.0,
                random.NextDouble(), random.NextDouble(),
            };

            double sum = 0, low = double.MaxValue, high = double.MinValue;
            foreach (GradientBoostedTrees model in models)
            {
                double score = model.ScoreSingle(row);
                sum += score;
                low = Math.Min(low, score);
                high = Math.Max(high, score);
            }

            maxSpread = Math.Max(maxSpread, high - low);

            // Not "close": identical. A boosted ensemble's score is linear in its trees, so
            // scaling every leaf by 1/k reproduces the mean of the models exactly.
            Assert.Equal(sum / models.Length, merged.ScoreSingle(row), 12);
        }

        // The fixture is only meaningful if the models actually disagree somewhere. If they
        // all predicted the same thing, the equality above would be vacuous.
        Assert.True(maxSpread > 1e-6, $"fold models were identical (max spread {maxSpread:R})");
    }

    [Fact]
    public void MergedModelHoldsEveryTreeFromEveryFold()
    {
        GradientBoostedTrees[] models = TrainDifferentModels(4);
        GradientBoostedTrees merged = EnsembleMerge.Merge(models);

        int foldTrees = 0;
        foreach (GradientBoostedTrees model in models) foldTrees += model.ToModelData().TreeRoot.Length;

        // The trees add up, which is exactly why merging does not make scoring cheaper.
        Assert.Equal(foldTrees, merged.ToModelData().TreeRoot.Length);
    }

    [Fact]
    public void MergingOneModelReturnsItUnchanged()
    {
        GradientBoostedTrees[] models = TrainDifferentModels(1);
        Assert.Same(models[0], EnsembleMerge.Merge(models));
    }

    [Fact]
    public void ChildIndicesAreRemappedSoEveryTreeStaysWalkable()
    {
        GradientBoostedTrees[] models = TrainDifferentModels(3);
        GbtModelData merged = EnsembleMerge.Merge(models).ToModelData();

        // A concatenation that forgot to offset Left/Right would still score - it would just
        // walk into another fold's nodes and return a plausible wrong number.
        for (int i = 0; i < merged.Feature.Length; i++)
        {
            if (merged.Feature[i] < 0)
            {
                Assert.Equal(-1, merged.Left[i]);
                Assert.Equal(-1, merged.Right[i]);
                continue;
            }

            Assert.InRange(merged.Left[i], i + 1, merged.Feature.Length - 1);
            Assert.InRange(merged.Right[i], i + 1, merged.Feature.Length - 1);
        }

        foreach (int root in merged.TreeRoot) Assert.InRange(root, 0, merged.Feature.Length - 1);
    }

    [Fact]
    public void RefusesModelsThatDisagreeOnShape()
    {
        static GradientBoostedTrees Stub(int featureCount) => GradientBoostedTrees.FromModelData(
            new GbtModelData
            {
                BaseScore = 0,
                FeatureCount = featureCount,
                Objective = GbtObjective.SquaredError,
                Feature = new[] { -1 },
                Threshold = new[] { 0.0 },
                Left = new[] { -1 },
                Right = new[] { -1 },
                Leaf = new[] { 0.5 },
                TreeRoot = new[] { 0 },
            });

        // Averaging models built over different feature vectors would silently score rows
        // against the wrong columns, so it is refused rather than attempted.
        Assert.Throws<ArgumentException>(() => EnsembleMerge.Merge(new[] { Stub(4), Stub(5) }));
        Assert.Throws<ArgumentException>(() => EnsembleMerge.Merge(Array.Empty<GradientBoostedTrees>()));
    }
}
