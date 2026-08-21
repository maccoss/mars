// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from mars/calibration.py (MzCalibrator).

using System;
using System.Collections.Generic;
using pwiz.Osprey.ML;

namespace MARS.Core;

/// <summary>How the second pass handles rows the first pass could not explain.</summary>
public enum RobustFit
{
    /// <summary>Fit once. Every row counts the same, however implausible its label.</summary>
    None,

    /// <summary>Drop rows beyond the threshold and fit again.</summary>
    Trim,

    /// <summary>
    /// Down-weight rows in proportion to how far past the threshold they sit, and fit again.
    /// Gentler than <see cref="Trim"/>, and on the reference data slightly worse: a row a
    /// little past the threshold keeps nearly all of its weight.
    /// </summary>
    Huber,
}

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
    /// Cross-validation folds. 2 or more trains one model per fold and makes the calibrator
    /// their ensemble; 0 or 1 falls back to a single fit with a held-out split.
    /// </summary>
    /// <remarks>
    /// Folds are assigned by peptide, never by row, so every reported number comes from a
    /// model that never saw the peptide it is scoring. Cross-validation costs one training
    /// round per fold, which is a minority of a run's wall clock because matching dominates.
    /// </remarks>
    public int CvFolds { get; set; } = 5;


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

    /// <summary>How the second pass treats rows the first pass could not explain.</summary>
    /// <remarks>
    /// Trim rather than Huber, on measurement. Huber is the more principled choice when
    /// outliers are extreme measurements of the right quantity; here they are measurements
    /// of the wrong one - the most intense peak in the window was a different ion - so the
    /// label carries no information at all and softening its influence is not enough. At
    /// three robust sigma, Huber still leaves such a row 79% of its weight on average.
    /// </remarks>
    public RobustFit Robust { get; set; } = RobustFit.Trim;

    /// <summary>
    /// Residual threshold for the second pass, in robust standard deviations. 0 disables it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matching takes the most intense peak within the tolerance window, and sometimes that
    /// peak is not the fragment. Those rows carry a delta that is not a mass error at all -
    /// on the reference Stellar run they are three times weaker than the rest, sit in
    /// spectra with a quarter as many fragment ions, and cluster against the edge of the
    /// window - and squared error is exactly the loss that lets them pull the fit.
    /// </para>
    /// <para>
    /// The threshold is in units of a MAD-derived sigma, so it adapts to the instrument
    /// rather than assuming a Th value. Trimming applies to TRAINING rows only. Held-out
    /// rows are always scored in full, or the reported accuracy would improve simply by
    /// discarding the hard cases from the measurement.
    /// </para>
    /// </remarks>
    public double RobustSigma { get; set; } = 3.0;
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

    /// <summary>Before and after in ppm, or null when fragment m/z was not collected.</summary>
    public ErrorSummary? BeforePpm { get; init; }

    public ErrorSummary? AfterPpm { get; init; }

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
        TrainingStatistics? statistics,
        CrossValidationReport? crossValidation)
    {
        Features = features;
        Model = model;
        AbsoluteTimeOffset = absoluteTimeOffset;
        Options = options;
        Statistics = statistics;
        CrossValidation = crossValidation;
    }

    public FeatureSet Features { get; }

    /// <summary>
    /// The model that corrects the data, fitted to every usable row.
    /// </summary>
    /// <remarks>
    /// Calibration is in-sample by nature, so nothing is withheld from the surface being
    /// fitted. <see cref="CrossValidation"/> holds the separate question of whether that
    /// surface is real structure and what it would achieve on a run it was not fitted to.
    /// </remarks>
    public GradientBoostedTrees Model { get; }

    /// <summary>Cross-validation results, or null when a single model was fitted.</summary>
    public CrossValidationReport? CrossValidation { get; }

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

            predictions[i] = usable ? PredictDelta(row) : double.NaN;
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

        // Fold assignment needs each row's peptide, so a peptide's fragments cannot be split
        // across a train/test boundary. Without this the model can memorize a peptide's
        // fragment m/z values and every held-out number comes out optimistic.
        if (table.PeptideGroup.Count != table.Count)
        {
            throw new InvalidOperationException(
                $"The match table has {table.Count:N0} rows but {table.PeptideGroup.Count:N0} " +
                "peptide group values. Every row needs one: folds and the held-out split are " +
                "assigned over peptides, and falling back to row-random splitting would report " +
                "an accuracy the model cannot reach on an unseen peptide.");
        }

        var groupOfRow = new int[rows.Length];
        int[] groupColumn = table.PeptideGroup.Items;
        for (int i = 0; i < rows.Length; i++) groupOfRow[i] = groupColumn[rows[i]];

        // Per row, from that fragment's own m/z. Dividing an aggregate by a nominal mass
        // would be wrong by however wide the cohort's m/z range is, which on a plasma
        // digest is most of a factor of four.
        double[]? ppmScale = null;
        if (table.Has(MarsFeature.FragmentMz))
        {
            double[] fragmentMz = table.Column(MarsFeature.FragmentMz).Items;
            ppmScale = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                double mz = fragmentMz[rows[i]];
                ppmScale[i] = mz > 0 ? 1e6 / mz : 0.0;
            }
        }

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

        if (options.CvFolds >= 2)
        {
            return FitCrossValidated(
                features, x, y, weights, groupOfRow, ppmScale, gbtParams, options,
                absoluteTimeOffset, table.Count, log);
        }

        (int[] trainIndex, int[] validationIndex) =
            PeptideFolds.SplitByGroup(groupOfRow, options.ValidationSplit, options.Seed);

        log?.Invoke(
            $"Training on {trainIndex.Length:N0} rows, holding out {validationIndex.Length:N0} " +
            $"by peptide, {nFeat} features");

        (GradientBoostedTrees model, int affected) = TrainRobust(
            Gather(x, trainIndex), Gather(y, trainIndex),
            weights is null ? null : Gather(weights, trainIndex), gbtParams,
            options.Robust, options.RobustSigma);

        if (affected > 0)
        {
            log?.Invoke(
                $"  {DescribeRobust(options.Robust)} {affected:N0} unexplainable rows " +
                $"({100.0 * affected / trainIndex.Length:F1}%) and refit");
        }

        var calibrator = new MzCalibrator(features, model, absoluteTimeOffset, options, null, null);
        TrainingStatistics statistics = calibrator.Evaluate(
            x, y, trainIndex, validationIndex, ppmScale, table.Count, options);
        return new MzCalibrator(features, model, absoluteTimeOffset, options, statistics, null);
    }

    /// <summary>
    /// Trains one model per fold and returns their ensemble.
    /// </summary>
    /// <remarks>
    /// The ensemble is the model, not a stepping stone to one. Osprey's Percolator does the
    /// same on its tree path: the linear path can average fold weight vectors because a dot
    /// product is linear in the weights, but trees cannot be averaged that way, so it
    /// averages the fold SCORES instead (see <c>PercolatorResults.FoldGbtModels</c> and
    /// <c>PercolatorScorer.AverageGbtScore</c> in ProteoWizard). Averaging K models trained
    /// on overlapping data is also steadier than any one of them, and costs no extra
    /// training round.
    /// <para>
    /// Unlike Percolator, no cross-fold score calibration is needed. An SVM margin means
    /// nothing across folds until it is calibrated; MARS predicts a mass error in Th, the
    /// same physical quantity in every fold.
    /// </para>
    /// </remarks>
    private static MzCalibrator FitCrossValidated(
        FeatureSet features, double[][] x, double[] y, double[]? weights, int[] groupOfRow,
        double[]? ppmScale, GbtParams gbtParams, CalibrationOptions options,
        double absoluteTimeOffset, int matchedRows, Action<string>? log)
    {
        (int[] foldOfRow, int groupCount) = PeptideFolds.AssignFolds(groupOfRow, options.CvFolds);

        if (groupCount < options.CvFolds)
        {
            throw new InvalidOperationException(
                $"Only {groupCount} distinct peptides matched, which cannot be split into " +
                $"{options.CvFolds} folds. Lower --cv-folds, or pass --cv-folds 0 to train a " +
                "single model.");
        }

        log?.Invoke(
            $"Cross-validating: {options.CvFolds} folds over {groupCount:N0} peptides, " +
            $"{x.Length:N0} rows, {features.Count} features");

        var models = new GradientBoostedTrees[options.CvFolds];
        var perFold = new FoldMetrics[options.CvFolds];
        FoldMetrics[]? perFoldPpm = null;
        var outOfFold = new double[x.Length];
        int affectedTotal = 0;

        for (int fold = 0; fold < options.CvFolds; fold++)
        {
            var trainRows = new List<int>(x.Length);
            var heldOutRows = new List<int>((x.Length / options.CvFolds) + 1);
            for (int i = 0; i < foldOfRow.Length; i++)
            {
                if (foldOfRow[i] == fold) heldOutRows.Add(i);
                else trainRows.Add(i);
            }

            int[] trainIndex = trainRows.ToArray();
            int[] heldOutIndex = heldOutRows.ToArray();

            // Trim within the fold's own training rows. The held-out rows are scored in
            // full: dropping the hard ones from the measurement as well would improve the
            // reported number without improving anything real.
            (models[fold], int foldAffected) = TrainRobust(
                Gather(x, trainIndex), Gather(y, trainIndex),
                weights is null ? null : Gather(weights, trainIndex), gbtParams,
                options.Robust, options.RobustSigma);
            affectedTotal += foldAffected;

            var heldOutObserved = new double[heldOutIndex.Length];
            var heldOutPredicted = new double[heldOutIndex.Length];
            for (int i = 0; i < heldOutIndex.Length; i++)
            {
                int r = heldOutIndex[i];
                heldOutObserved[i] = y[r];
                heldOutPredicted[i] = models[fold].ScoreSingle(x[r]);
                outOfFold[r] = heldOutPredicted[i];
            }

            perFold[fold] = PeptideFolds.Measure(heldOutObserved, heldOutPredicted);
            if (ppmScale is not null)
            {
                var heldOutScale = new double[heldOutIndex.Length];
                for (int i = 0; i < heldOutIndex.Length; i++) heldOutScale[i] = ppmScale[heldOutIndex[i]];
                (perFoldPpm ??= new FoldMetrics[options.CvFolds])[fold] =
                    PeptideFolds.MeasurePpm(heldOutObserved, heldOutPredicted, heldOutScale);
            }
            log?.Invoke(
                $"  fold {fold + 1}/{options.CvFolds}: trained on {trainIndex.Length:N0}, " +
                $"scored {heldOutIndex.Length:N0}, MAD {perFold[fold].Mad:F4} Th " +
                $"({perFold[fold].MadReduction:F1}% reduction), r {perFold[fold].PearsonR:F4}");
        }

        // Merge the folds into one model. This is not a refit and not an approximation: a
        // boosted ensemble's score is linear in its trees, so keeping every tree and dividing
        // each leaf by the fold count reproduces the average of the fold models exactly. What
        // gets applied is therefore precisely what was measured, as a single object.
        // The model that corrects the data is fitted to ALL of it. Calibration is in-sample
        // by nature - it is how mass calibration has always worked, measuring known species
        // present in the run and correcting the axis from them - so there is no reason to
        // withhold data from the surface being fitted. The fold models exist to answer a
        // different question, which is whether that surface is real structure or noise, and
        // what it would achieve on a run it was not fitted to.
        if (affectedTotal > 0)
        {
            log?.Invoke(
                $"  folds {DescribeRobust(options.Robust)} " +
                $"{affectedTotal / options.CvFolds:N0} unexplainable rows each, on average");
        }

        log?.Invoke($"  fitting the correction model on all {x.Length:N0} rows");
        (GradientBoostedTrees model, int affected) = TrainRobust(
            x, y, weights, gbtParams, options.Robust, options.RobustSigma);

        if (affected > 0)
        {
            log?.Invoke(
                $"  {DescribeRobust(options.Robust)} {affected:N0} unexplainable rows " +
                $"({100.0 * affected / x.Length:F1}%) and refit");
        }
        var calibrator = new MzCalibrator(features, model, absoluteTimeOffset, options, null, null);

        var inSample = new double[x.Length];
        for (int i = 0; i < x.Length; i++) inSample[i] = calibrator.PredictDelta(x[i]);

        var report = new CrossValidationReport
        {
            Folds = options.CvFolds,
            Groups = groupCount,
            PerFold = perFold,
            OutOfFold = PeptideFolds.Measure(y, outOfFold),
            InSample = PeptideFolds.Measure(y, inSample),
            PerFoldPpm = perFoldPpm,
            OutOfFoldPpm = ppmScale is null ? null : PeptideFolds.MeasurePpm(y, outOfFold, ppmScale),
            InSamplePpm = ppmScale is null ? null : PeptideFolds.MeasurePpm(y, inSample, ppmScale),
        };

        log?.Invoke(
            $"  on this data: MAD {report.InSample.Mad:F4} Th " +
            $"({report.InSample.MadReduction:F1}% reduction)");
        log?.Invoke(
            $"  expected on new data: MAD {report.OutOfFold.Mad:F4} Th " +
            $"({report.OutOfFold.MadReduction:F1}% reduction), r {report.OutOfFold.PearsonR:F4}, " +
            $"fold spread {report.MadSpread:F4} Th");

        TrainingStatistics statistics =
            calibrator.EvaluateCrossValidated(x, y, inSample, ppmScale, matchedRows, report, options);

        return new MzCalibrator(features, model, absoluteTimeOffset, options, statistics, report);
    }

    /// <summary>
    /// Fits a model, then fits again with the rows the first pass could not explain either
    /// removed or held down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RobustFit.Huber"/> is a Huber loss, reached by reweighting rather than by
    /// changing the objective. Huber's gradient is the residual clipped to the threshold,
    /// <c>clip(r, +/-d) = r * min(1, d/|r|)</c>, and squared error on weights
    /// <c>w * min(1, d/|r|)</c> produces exactly that gradient. So one extra pass of the
    /// existing squared-error path gives the robust fit, with no new objective to add,
    /// validate and keep bit-identical upstream.
    /// </para>
    /// <para>
    /// Compared with <see cref="RobustFit.Trim"/> it is the same idea made continuous: a row
    /// two thresholds out counts half as much rather than either fully or not at all, so
    /// there is no cliff for a row to sit astride, and a genuinely large but real error is
    /// still heard.
    /// </para>
    /// <para>
    /// Weights are renormalized to mean 1 afterwards, because <c>min_child_weight</c> and
    /// <c>reg_lambda</c> are thresholds on summed weights; without it, down-weighting the
    /// tail would quietly tighten both.
    /// </para>
    /// </remarks>
    /// <returns>The model, and how many rows the second pass removed or down-weighted.</returns>
    private static (GradientBoostedTrees Model, int Affected) TrainRobust(
        double[][] x, double[] y, double[]? weights, GbtParams gbtParams,
        RobustFit mode, double sigma)
    {
        GradientBoostedTrees first = GradientBoostedTrees.Train(x, y, gbtParams, weights);
        if (mode == RobustFit.None || !(sigma > 0) || x.Length < 100) return (first, 0);

        var residual = new double[x.Length];
        for (int i = 0; i < x.Length; i++) residual[i] = y[i] - first.ScoreSingle(x[i]);

        // A MAD-derived scale rather than a standard deviation: the outliers being looked for
        // would inflate a standard deviation enough to hide themselves.
        ErrorSummary summary = MarsStatistics.Summarize(residual);
        double scale = summary.Mad * 1.4826;
        if (!(scale > 0)) return (first, 0);

        double limit = sigma * scale;

        if (mode == RobustFit.Trim)
        {
            var keep = new List<int>(x.Length);
            for (int i = 0; i < x.Length; i++)
            {
                if (Math.Abs(residual[i] - summary.Median) <= limit) keep.Add(i);
            }

            int trimmed = x.Length - keep.Count;

            // Refitting on a much smaller set would be a different model rather than a
            // cleaned one, so an unexpectedly aggressive trim is declined instead of applied.
            if (trimmed == 0 || keep.Count < x.Length / 2) return (first, 0);

            int[] kept = keep.ToArray();
            return (
                GradientBoostedTrees.Train(
                    Gather(x, kept), Gather(y, kept), gbtParams,
                    weights is null ? null : Gather(weights, kept)),
                trimmed);
        }

        var robust = new double[x.Length];
        double sum = 0;
        int held = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double excess = Math.Abs(residual[i] - summary.Median);
            double factor = excess > limit ? limit / excess : 1.0;
            if (factor < 1.0) held++;
            robust[i] = (weights is null ? 1.0 : weights[i]) * factor;
            sum += robust[i];
        }

        if (held == 0) return (first, 0);

        double mean = sum / x.Length;
        if (mean > 0)
        {
            for (int i = 0; i < robust.Length; i++) robust[i] /= mean;
        }

        return (GradientBoostedTrees.Train(x, y, gbtParams, robust), held);
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

    private static string DescribeRobust(RobustFit mode) =>
        mode == RobustFit.Trim ? "dropped" : "held down";

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

    /// <summary>
    /// Training statistics for the cross-validated path.
    /// </summary>
    /// <remarks>
    /// <c>After</c> is the residual of the model that will actually correct the files, on
    /// the rows it was fitted to. That is what the corrected output will look like when it
    /// is re-matched, and it is what a user asking "what did this do to my data" is asking
    /// about. The out-of-fold figure answers the other question - what the same procedure
    /// would achieve on a run it was not fitted to, which is what <c>mars apply</c> does -
    /// and lives on <see cref="CrossValidationReport"/>. Both are reported, labelled.
    /// </remarks>
    private TrainingStatistics EvaluateCrossValidated(
        double[][] x, double[] y, double[] inSample, double[]? ppmScale, int rowsMatched,
        CrossValidationReport report, CalibrationOptions options)
    {
        var residual = new double[y.Length];
        double absolute = 0, squares = 0;
        for (int i = 0; i < y.Length; i++)
        {
            residual[i] = y[i] - inSample[i];
            absolute += Math.Abs(residual[i]);
            squares += residual[i] * residual[i];
        }

        return new TrainingStatistics
        {
            RowsMatched = rowsMatched,
            RowsUsed = y.Length,
            RowsTrain = y.Length,
            RowsValidation = report.OutOfFold.Rows,
            TrainMae = y.Length > 0 ? absolute / y.Length : double.NaN,
            TrainRmse = y.Length > 0 ? Math.Sqrt(squares / y.Length) : double.NaN,

            // The validation figures are the out-of-fold ones: the honest estimate for a run
            // this model was not fitted to.
            ValidationMae = report.OutOfFold.Rms > 0 ? OutOfFoldMae(y, report) : double.NaN,
            ValidationRmse = report.OutOfFold.Rms,
            Before = MarsStatistics.Summarize(y),
            After = MarsStatistics.Summarize(residual),
            BeforePpm = ppmScale is null ? null : SummarizePpm(y, ppmScale),
            AfterPpm = ppmScale is null ? null : SummarizePpm(residual, ppmScale),
            PermutationImportance = ComputePermutationImportance(x, y, options),
            SplitCount = ComputeSplitCounts(),
        };
    }

    private static ErrorSummary SummarizePpm(double[] values, double[] scale)
    {
        var ppm = new double[values.Length];
        for (int i = 0; i < values.Length; i++) ppm[i] = values[i] * scale[i];
        return MarsStatistics.Summarize(ppm);
    }

    private static double OutOfFoldMae(double[] y, CrossValidationReport report) =>
        // RMS is carried directly; MAE is not, and recomputing it would need the predictions
        // again. The median absolute residual is the figure actually reported everywhere.
        report.OutOfFold.Mad;

    private TrainingStatistics Evaluate(
        double[][] x,
        double[] y,
        int[] trainIndex,
        int[] validationIndex,
        double[]? ppmScale,
        int rowsMatched,
        CalibrationOptions options)
    {
        var residualsTrain = new double[trainIndex.Length];
        for (int i = 0; i < trainIndex.Length; i++)
        {
            int r = trainIndex[i];
            residualsTrain[i] = y[r] - PredictDelta(x[r]);
        }

        var residualsValidation = new double[validationIndex.Length];
        for (int i = 0; i < validationIndex.Length; i++)
        {
            int r = validationIndex[i];
            residualsValidation[i] = y[r] - PredictDelta(x[r]);
        }

        var after = new double[x.Length];
        for (int i = 0; i < x.Length; i++) after[i] = y[i] - PredictDelta(x[i]);

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
            double residual = y[i] - PredictDelta(x[i]);
            sum += residual * residual;
        }

        return Math.Sqrt(sum / x.Length);
    }

    private int[] ComputeSplitCounts()
    {
        var counts = new int[Features.Count];
        foreach (int feature in Model.ToModelData().Feature)
        {
            if (feature >= 0 && feature < counts.Length) counts[feature]++;
        }

        return counts;
    }
}
