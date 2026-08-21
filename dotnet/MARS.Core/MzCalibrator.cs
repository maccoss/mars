// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/calibration.py (MzCalibrator).

using System;
using System.Collections.Generic;
using pwiz.Osprey.ML;

namespace MARS.Core;

/// <summary>
/// Hyperparameters, transcribed from the Python MzCalibrator, which constructs
/// xgboost.XGBRegressor(n_estimators=100, max_depth=6, learning_rate=0.1,
/// random_state=42, objective="reg:squarederror") and leaves the rest at the XGBoost
/// library default.
/// </summary>
public sealed class CalibrationOptions
{
    public int NEstimators { get; set; } = 100;

    public int MaxDepth { get; set; } = 6;

    public double LearningRate { get; set; } = 0.1;

    /// <summary>
    /// Under squared error the hessian is the sample weight, so this is a sample count.
    /// XGBoost's default of 1 carries over directly because reg:squarederror gives it the
    /// same meaning there.
    /// </summary>
    public double MinChildWeight { get; set; } = 1.0;

    public double Subsample { get; set; } = 1.0;

    public double ColSampleByTree { get; set; } = 1.0;

    public double Gamma { get; set; }

    public double RegLambda { get; set; } = 1.0;

    public double RegAlpha { get; set; }

    public int MaxBins { get; set; } = 256;

    public int Seed { get; set; } = 42;

    /// <summary>Fraction of rows held out to report validation error. 0 disables the split.</summary>
    public double ValidationSplit { get; set; } = 0.2;

    /// <summary>
    /// Weight training rows by observed peak intensity, normalized to mean 1. More intense
    /// fragments give a better-determined centroid, so they should count for more.
    /// </summary>
    public bool WeightByIntensity { get; set; } = true;

    /// <summary>Histogram threads. Determinism holds at any value; see Osprey.ML.</summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;

    /// <summary>
    /// Cap on training rows, applied by deterministic stride subsampling. 0 means no cap,
    /// which is what the Python implementation does.
    /// </summary>
    public int MaxTrainingRows { get; set; }

    /// <summary>Rows sampled when estimating permutation importance. 0 disables it.</summary>
    public int ImportanceSampleRows { get; set; } = 50000;
}

public sealed class TrainingStatistics
{
    public int RowsMatched { get; init; }

    public int RowsUsed { get; init; }

    public int RowsTrain { get; init; }

    public int RowsValidation { get; init; }

    public double TrainMae { get; init; }

    public double TrainRmse { get; init; }

    public double ValidationMae { get; init; }

    public double ValidationRmse { get; init; }

    public ErrorSummary Before { get; init; }

    public ErrorSummary After { get; init; }

    /// <summary>Permutation importance per active feature, normalized to sum to 1.</summary>
    public double[] PermutationImportance { get; init; } = Array.Empty<double>();

    /// <summary>Number of splits made on each active feature.</summary>
    public int[] SplitCount { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The m/z calibration model: predicts the mass error of a peak from its spectral context,
/// so the corrected value is <c>observed - PredictDelta(features)</c>.
/// </summary>
public sealed class MzCalibrator
{
    internal MzCalibrator(
        FeatureSet features,
        GradientBoostedTrees model,
        double absoluteTimeOffset,
        CalibrationOptions options,
        TrainingStatistics? statistics)
    {
        Features = features;
        Model = model;
        AbsoluteTimeOffset = absoluteTimeOffset;
        Options = options;
        Statistics = statistics;
    }

    public FeatureSet Features { get; }

    public GradientBoostedTrees Model { get; }

    /// <summary>
    /// Seconds subtracted from every raw acquisition timestamp to produce the
    /// <see cref="MarsFeature.AbsoluteTime"/> the model was trained on: the earliest
    /// acquisition start across the training files.
    /// <para>
    /// This has to travel with the model. The Python implementation re-bases absolute_time
    /// to the earliest run before fitting, but feeds the raw Unix timestamp back in when it
    /// writes the corrected file, so every inference row lands far above the largest
    /// training value and the feature degenerates to a constant branch. Carrying the offset
    /// keeps training and inference on the same scale.
    /// </para>
    /// </summary>
    public double AbsoluteTimeOffset { get; }

    public CalibrationOptions Options { get; }

    public TrainingStatistics? Statistics { get; }

    /// <summary>Predicted mass error in Th. Subtract from the observed m/z to correct it.</summary>
    public double PredictDelta(double[] featureRow) => Model.ScoreSingle(featureRow);

    /// <summary>
    /// Predicted mass error for every row of a match table, parallel to the table's rows.
    /// A row with any undefined feature scores NaN rather than being silently dropped, so
    /// the result lines up with the table and with a dump of it.
    /// </summary>
    /// <remarks>
    /// This exists so the learned function can be compared against another implementation
    /// on identical rows. Comparing two boosting implementations tree by tree is not
    /// meaningful; comparing what they predict for the same input is.
    /// </remarks>
    public double[] PredictAll(MatchTable table)
    {
        int featureCount = Features.Count;
        var columns = new double[featureCount][];
        for (int j = 0; j < featureCount; j++)
            columns[j] = table.Column(Features.Features[j]).Items;

        var predictions = new double[table.Count];
        var row = new double[featureCount];

        for (int i = 0; i < table.Count; i++)
        {
            bool usable = true;
            for (int j = 0; j < featureCount; j++)
            {
                double value = columns[j][i];
                if (double.IsNaN(value))
                {
                    usable = false;
                    break;
                }

                row[j] = value;
            }

            predictions[i] = usable ? Model.ScoreSingle(row) : double.NaN;
        }

        return predictions;
    }

    /// <summary>
    /// Fits a calibrator on matched fragments.
    /// </summary>
    /// <param name="table">Matched fragments, one row per library fragment matched to a peak.</param>
    /// <param name="options">Hyperparameters.</param>
    /// <param name="absoluteTimeOffset">
    /// Earliest acquisition start across the input files, already subtracted from the
    /// table's <see cref="MarsFeature.AbsoluteTime"/> column.
    /// </param>
    /// <param name="log">Optional progress sink.</param>
    public static MzCalibrator Fit(
        MatchTable table,
        CalibrationOptions options,
        double absoluteTimeOffset,
        Action<string>? log = null)
    {
        if (table.Count == 0)
            throw new InvalidOperationException("No fragment matches: nothing to train on.");

        FeatureSet features = SelectFeatures(table, log);
        int[] rows = SelectRows(table, features, options, log);
        if (rows.Length == 0)
            throw new InvalidOperationException("Every candidate training row had a missing feature value.");

        int nFeat = features.Count;
        var x = new double[rows.Length][];
        var y = new double[rows.Length];
        double[]? weights = options.WeightByIntensity ? new double[rows.Length] : null;

        double[] deltaMz = table.DeltaMz.Items;
        double[] intensity = table.ObservedIntensity.Items;
        var columns = new double[nFeat][];
        for (int j = 0; j < nFeat; j++) columns[j] = table.Column(features.Features[j]).Items;

        double weightSum = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            int r = rows[i];
            var row = new double[nFeat];
            for (int j = 0; j < nFeat; j++) row[j] = columns[j][r];
            x[i] = row;
            y[i] = deltaMz[r];
            if (weights is not null)
            {
                weights[i] = intensity[r];
                weightSum += intensity[r];
            }
        }

        if (weights is not null)
        {
            // The Python implementation normalizes weights to mean 1. That matters here:
            // min_child_weight thresholds the summed hessian, which under squared error is
            // the summed weight, so raw detector counts would make the threshold meaningless.
            double meanWeight = weightSum / rows.Length;
            if (meanWeight > 0)
            {
                for (int i = 0; i < weights.Length; i++) weights[i] /= meanWeight;
            }
        }

        (int[] trainIndex, int[] validationIndex) = SplitTrainValidation(rows.Length, options);

        double[][] xTrain = Gather(x, trainIndex);
        double[] yTrain = Gather(y, trainIndex);
        double[]? wTrain = weights is null ? null : Gather(weights, trainIndex);

        log?.Invoke($"Training on {trainIndex.Length:N0} rows, holding out {validationIndex.Length:N0}, {nFeat} features");

        var gbtParams = new GbtParams
        {
            Objective = GbtObjective.SquaredError,
            NTrees = options.NEstimators,
            MaxDepth = options.MaxDepth,
            LearningRate = options.LearningRate,
            MinChildWeight = options.MinChildWeight,
            Subsample = options.Subsample,
            ColSample = options.ColSampleByTree,
            Gamma = options.Gamma,
            RegLambda = options.RegLambda,
            RegAlpha = options.RegAlpha,
            MaxBins = options.MaxBins,
            Seed = (ulong)options.Seed,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism <= 0
                ? Environment.ProcessorCount
                : options.MaxDegreeOfParallelism,
        };

        GradientBoostedTrees model = GradientBoostedTrees.Train(xTrain, yTrain, gbtParams, wTrain);

        var calibrator = new MzCalibrator(features, model, absoluteTimeOffset, options, null);
        TrainingStatistics statistics = calibrator.Evaluate(x, y, trainIndex, validationIndex, table.Count, options);
        return new MzCalibrator(features, model, absoluteTimeOffset, options, statistics);
    }

    /// <summary>
    /// Applies Python's feature-availability rules: the four always-on features, then each
    /// optional feature that has at least one row carrying a value. The injection-time group
    /// stands or falls together because none of it is defined without an injection time.
    /// </summary>
    private static FeatureSet SelectFeatures(MatchTable table, Action<string>? log)
    {
        var active = new List<MarsFeature>(MarsFeatures.Count);
        foreach (MarsFeature feature in new[]
        {
            MarsFeature.PrecursorMz, MarsFeature.FragmentMz, MarsFeature.LogTic, MarsFeature.LogIntensity,
        })
        {
            if (table.Has(feature)) active.Add(feature);
            else log?.Invoke($"Required feature '{MarsFeatures.NameOf(feature)}' was not collected");
        }

        if (table.AnyFinite(MarsFeature.AbsoluteTime)) active.Add(MarsFeature.AbsoluteTime);

        if (table.AnyFinite(MarsFeature.InjectionTime))
        {
            active.Add(MarsFeature.InjectionTime);
            AddIfPresent(table, active, MarsFeature.TicInjectionTime);
            AddIfPresent(table, active, MarsFeature.FragmentIons);
            foreach (MarsFeature f in MarsFeatures.NeighborFeatures) AddIfPresent(table, active, f);
            foreach (MarsFeature f in MarsFeatures.RatioFeatures) AddIfPresent(table, active, f);
        }
        else
        {
            log?.Invoke("No spectrum reported an ion injection time; skipping the injection-time features");
        }

        AddIfPresent(table, active, MarsFeature.Rfa2Temp);
        AddIfPresent(table, active, MarsFeature.Rfc2Temp);

        var set = new FeatureSet(active);
        log?.Invoke($"Using {set.Count} features: {string.Join(", ", set.Names())}");
        return set;
    }

    private static void AddIfPresent(MatchTable table, List<MarsFeature> active, MarsFeature feature)
    {
        if (table.AnyFinite(feature)) active.Add(feature);
    }

    private static T[] Gather<T>(T[] source, int[] index)
    {
        var result = new T[index.Length];
        for (int i = 0; i < index.Length; i++) result[i] = source[index[i]];
        return result;
    }

    /// <summary>
    /// Keeps rows whose selected features are all finite, then applies the optional row cap
    /// by stride so the retained rows stay spread across the whole run rather than
    /// clustering at the start.
    /// </summary>
    private static int[] SelectRows(MatchTable table, FeatureSet features, CalibrationOptions options, Action<string>? log)
    {
        int n = table.Count;
        var columns = new double[features.Count][];
        for (int j = 0; j < features.Count; j++) columns[j] = table.Column(features.Features[j]).Items;
        double[] deltaMz = table.DeltaMz.Items;

        var kept = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(deltaMz[i])) continue;
            bool ok = true;
            for (int j = 0; j < columns.Length; j++)
            {
                if (!double.IsFinite(columns[j][i]))
                {
                    ok = false;
                    break;
                }
            }

            if (ok) kept.Add(i);
        }

        if (kept.Count < n)
            log?.Invoke($"Dropped {n - kept.Count:N0} rows with a missing feature value ({kept.Count:N0} retained)");

        if (options.MaxTrainingRows > 0 && kept.Count > options.MaxTrainingRows)
        {
            var capped = new int[options.MaxTrainingRows];
            for (int i = 0; i < capped.Length; i++)
                capped[i] = kept[(int)((long)i * kept.Count / capped.Length)];
            log?.Invoke($"Capped training rows at {capped.Length:N0} of {kept.Count:N0} by even stride");
            return capped;
        }

        return kept.ToArray();
    }

    /// <summary>
    /// Deterministic train/validation split. sklearn's train_test_split permutation cannot
    /// be reproduced outside sklearn, so this uses a seeded Fisher-Yates over XorShift64.
    /// The split differs from the Python one row for row; the reported validation error is
    /// still an honest held-out estimate.
    /// </summary>
    private static (int[] Train, int[] Validation) SplitTrainValidation(int n, CalibrationOptions options)
    {
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;

        if (options.ValidationSplit <= 0)
            return (order, Array.Empty<int>());

        var rng = new XorShift64((ulong)options.Seed);
        for (int i = n - 1; i > 0; i--)
        {
            int j = (int)(rng.Next() % (ulong)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        int validationCount = (int)Math.Round(n * options.ValidationSplit);
        validationCount = Math.Clamp(validationCount, 0, n - 1);

        var validation = new int[validationCount];
        var train = new int[n - validationCount];
        Array.Copy(order, 0, validation, 0, validationCount);
        Array.Copy(order, validationCount, train, 0, train.Length);

        // Restore ascending row order within each side so downstream accumulation order is
        // a function of the data, not of the shuffle.
        Array.Sort(validation);
        Array.Sort(train);
        return (train, validation);
    }

    private TrainingStatistics Evaluate(
        double[][] x,
        double[] y,
        int[] trainIndex,
        int[] validationIndex,
        int rowsMatched,
        CalibrationOptions options)
    {
        var residualsTrain = new double[trainIndex.Length];
        for (int i = 0; i < trainIndex.Length; i++)
        {
            int r = trainIndex[i];
            residualsTrain[i] = y[r] - Model.ScoreSingle(x[r]);
        }

        var residualsValidation = new double[validationIndex.Length];
        for (int i = 0; i < validationIndex.Length; i++)
        {
            int r = validationIndex[i];
            residualsValidation[i] = y[r] - Model.ScoreSingle(x[r]);
        }

        var after = new double[x.Length];
        for (int i = 0; i < x.Length; i++) after[i] = y[i] - Model.ScoreSingle(x[i]);

        return new TrainingStatistics
        {
            RowsMatched = rowsMatched,
            RowsUsed = x.Length,
            RowsTrain = trainIndex.Length,
            RowsValidation = validationIndex.Length,
            TrainMae = MarsStatistics.MeanAbsolute(residualsTrain),
            TrainRmse = MarsStatistics.Rms(residualsTrain),
            ValidationMae = validationIndex.Length > 0 ? MarsStatistics.MeanAbsolute(residualsValidation) : double.NaN,
            ValidationRmse = validationIndex.Length > 0 ? MarsStatistics.Rms(residualsValidation) : double.NaN,
            Before = MarsStatistics.Summarize(y),
            After = MarsStatistics.Summarize(after),
            PermutationImportance = ComputePermutationImportance(x, y, options),
            SplitCount = ComputeSplitCounts(),
        };
    }

    /// <summary>
    /// Permutation importance: the increase in RMSE when one feature's values are shuffled
    /// across rows, normalized to sum to 1.
    /// <para>
    /// This is NOT XGBoost's gain importance, which the Python implementation reports.
    /// Osprey.ML does not retain per-split gain, and permutation importance answers the
    /// question people actually ask of these numbers -- how much does this feature carry --
    /// without depending on tree internals. Values are not comparable to the Python ones
    /// term by term; the ranking is.
    /// </para>
    /// </summary>
    private double[] ComputePermutationImportance(double[][] x, double[] y, CalibrationOptions options)
    {
        int nFeat = Features.Count;
        var importance = new double[nFeat];
        if (options.ImportanceSampleRows <= 0 || x.Length == 0) return importance;

        int sampleSize = Math.Min(options.ImportanceSampleRows, x.Length);
        var sample = new double[sampleSize][];
        var target = new double[sampleSize];
        for (int i = 0; i < sampleSize; i++)
        {
            int r = (int)((long)i * x.Length / sampleSize);
            sample[i] = (double[])x[r].Clone();
            target[i] = y[r];
        }

        double baseline = RootMeanSquareResidual(sample, target);

        var rng = new XorShift64((ulong)options.Seed + 977UL);
        var scratch = new double[sampleSize];
        double total = 0;
        for (int j = 0; j < nFeat; j++)
        {
            for (int i = 0; i < sampleSize; i++) scratch[i] = sample[i][j];

            for (int i = sampleSize - 1; i > 0; i--)
            {
                int k = (int)(rng.Next() % (ulong)(i + 1));
                (sample[i][j], sample[k][j]) = (sample[k][j], sample[i][j]);
            }

            double shuffled = RootMeanSquareResidual(sample, target);
            importance[j] = Math.Max(0.0, shuffled - baseline);
            total += importance[j];

            for (int i = 0; i < sampleSize; i++) sample[i][j] = scratch[i];
        }

        if (total > 0)
        {
            for (int j = 0; j < nFeat; j++) importance[j] /= total;
        }

        return importance;
    }

    private double RootMeanSquareResidual(double[][] x, double[] y)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double residual = y[i] - Model.ScoreSingle(x[i]);
            sum += residual * residual;
        }

        return Math.Sqrt(sum / x.Length);
    }

    private int[] ComputeSplitCounts()
    {
        var counts = new int[Features.Count];
        GbtModelData data = Model.ToModelData();
        foreach (int feature in data.Feature)
        {
            if (feature >= 0 && feature < counts.Length) counts[feature]++;
        }

        return counts;
    }
}
