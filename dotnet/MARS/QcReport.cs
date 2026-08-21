// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// The QC path's text report. Layout follows the Python mars_qc_summary.txt so the two can
// be diffed directly during the port.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MARS.Core;

namespace MARS.Cli;

public static class QcReport
{
    public static void Write(
        string path,
        MzCalibrator calibrator,
        MatchStatistics matchStatistics,
        IReadOnlyList<string> inputFiles,
        MatchOptions matchOptions)
    {
        TrainingStatistics stats = calibrator.Statistics
            ?? throw new InvalidOperationException("Model carries no training statistics.");

        var text = new StringBuilder();
        text.AppendLine("Mars Calibration QC Summary");
        text.AppendLine(new string('=', 50));
        text.AppendLine();

        text.AppendLine("Input:");
        foreach (string file in inputFiles) text.AppendLine("  " + Path.GetFileName(file));
        text.AppendLine($"  Tolerance: {DescribeTolerance(matchOptions)}");
        text.AppendLine($"  Minimum intensity: {matchOptions.MinIntensity:N0}");
        if (matchOptions.MaxIsolationWindowWidth is double width)
            text.AppendLine($"  Maximum isolation window: {width:F2} Th");
        text.AppendLine();

        text.AppendLine("Before Calibration:");
        AppendSummary(text, stats.Before, stats.BeforePpm);
        text.AppendLine();

        text.AppendLine(calibrator.CrossValidation is null
            ? "After Calibration:"
            : "After Calibration (these files, corrected):");
        AppendSummary(text, stats.After, stats.AfterPpm);
        text.AppendLine();

        text.AppendLine($"Improvement: {Reduction(stats.Before.StdDev, stats.After.StdDev):F1}% reduction in std dev");
        text.AppendLine($"             {Reduction(stats.Before.Mad, stats.After.Mad):F1}% reduction in MAD");
        text.AppendLine($"             {Reduction(stats.Before.Rms, stats.After.Rms):F1}% reduction in RMS");
        if (stats.BeforePpm is ErrorSummary bp && stats.AfterPpm is ErrorSummary ap)
        {
            text.AppendLine(
                $"             MAD {bp.Mad:F2} -> {ap.Mad:F2} ppm, " +
                $"median {bp.Median:+0.00;-0.00} -> {ap.Median:+0.00;-0.00} ppm");
        }
        text.AppendLine();

        if (calibrator.CrossValidation is CrossValidationReport estimate)
        {
            // The two numbers answer different questions and a reader deserves both. The
            // figures above describe these files. This one describes what the same procedure
            // would achieve on a run it was not fitted to, which is what `mars apply` does.
            text.AppendLine(
                $"Expected on data not used to fit: MAD {estimate.OutOfFold.Mad:F4} Th" +
                (estimate.OutOfFoldPpm is FoldMetrics oofPpm ? $" ({oofPpm.Mad:F2} ppm)" : string.Empty) +
                $", {estimate.OutOfFold.MadReduction:F1}% reduction, from cross-validation below.");
        }

        text.AppendLine();

        if (calibrator.CrossValidation is CrossValidationReport cv)
        {
            text.AppendLine("Cross-Validation (folds split by peptide)");
            text.AppendLine(new string('=', 40));
            text.AppendLine($"Folds: {cv.Folds}  over {cv.Groups:N0} peptides");
            text.AppendLine();
            bool ppm = cv.PerFoldPpm is not null;
            text.AppendLine(ppm
                ? "  fold        rows      MAD Th    MAD ppm     RMS ppm   reduction   Pearson r"
                : "  fold        rows      MAD Th     RMS Th   reduction   Pearson r");
            for (int i = 0; i < cv.PerFold.Length; i++)
            {
                FoldMetrics fold = cv.PerFold[i];
                text.AppendLine(ppm
                    ? $"  {i + 1,4}  {fold.Rows,10:N0}  {fold.Mad,10:F4} {cv.PerFoldPpm![i].Mad,10:F2}" +
                      $"  {cv.PerFoldPpm[i].Rms,10:F2}  {fold.MadReduction,9:F1}%  {fold.PearsonR,10:F4}"
                    : $"  {i + 1,4}  {fold.Rows,10:N0}  {fold.Mad,10:F4} {fold.Rms,10:F4}" +
                      $"  {fold.MadReduction,9:F1}%  {fold.PearsonR,10:F4}");
            }

            text.AppendLine();
            text.AppendLine(
                $"  pooled out-of-fold: MAD {cv.OutOfFold.Mad:F4} Th" +
                (cv.OutOfFoldPpm is FoldMetrics op ? $" ({op.Mad:F2} ppm)" : string.Empty) +
                $", RMS {cv.OutOfFold.Rms:F4} Th, r {cv.OutOfFold.PearsonR:F4}");
            text.AppendLine(
                $"  spread across folds: MAD {cv.MadSpread:F4} Th, RMS {cv.RmsSpread:F4} Th, " +
                $"r {cv.PearsonRSpread:F4}");
            text.AppendLine(
                $"  on this data: MAD {cv.InSample.Mad:F4} Th" +
                (cv.InSamplePpm is FoldMetrics ip ? $" ({ip.Mad:F2} ppm)" : string.Empty) +
                $"; gap to the estimate above {cv.OptimismMad:F4} Th ({OptimismVerdict(cv)})");
            text.AppendLine();
            text.AppendLine("  Every row above was scored by a model that never saw its peptide,");
            text.AppendLine("  so this is the estimate for a run the model was not fitted to.");
            text.AppendLine("  The figures at the top of this report describe THESE files, which the");
            text.AppendLine("  applied model was fitted to - as mass calibration normally is.");
            text.AppendLine($"  {DescribeSpread(cv)}");
            text.AppendLine();
            text.AppendLine();
        }

        text.AppendLine("Calibration Model Summary");
        text.AppendLine(new string('=', 40));
        text.AppendLine($"Matched fragments: {stats.RowsMatched:N0}");
        text.AppendLine($"Training samples:  {stats.RowsUsed:N0}");
        text.AppendLine(calibrator.CrossValidation is null
            ? $"Train/Val split:   {stats.RowsTrain:N0} / {stats.RowsValidation:N0}"
            : $"Model:             fitted on all {stats.RowsUsed:N0} rows; " +
              $"{calibrator.CrossValidation.Folds}-fold cross-validation run alongside");
        text.AppendLine($"Spectra examined:  {matchStatistics.SpectraSeen:N0}");
        text.AppendLine($"Library precursors matched: {matchStatistics.UniqueEntriesMatched:N0}");
        text.AppendLine();

        text.AppendLine("Model performance:");
        bool crossValidated = calibrator.CrossValidation is not null;
        text.AppendLine($"  {(crossValidated ? "On this data MAE: " : "Train MAE:  ")}{stats.TrainMae:F4} Th");
        text.AppendLine($"  {(crossValidated ? "On this data RMSE:" : "Train RMSE: ")}{stats.TrainRmse:F4} Th");
        if (stats.RowsValidation > 0)
        {
            text.AppendLine($"  {(crossValidated ? "Out-of-fold MAD:  " : "Val MAE:    ")}{stats.ValidationMae:F4} Th");
            text.AppendLine($"  {(crossValidated ? "Out-of-fold RMSE: " : "Val RMSE:   ")}{stats.ValidationRmse:F4} Th");
        }

        text.AppendLine();
        text.AppendLine("Hyperparameters:");
        text.AppendLine($"  n_estimators:   {calibrator.Options.NEstimators}");
        text.AppendLine($"  max_depth:      {calibrator.Options.MaxDepth}");
        text.AppendLine($"  learning_rate:  {calibrator.Options.LearningRate.ToString("R", CultureInfo.InvariantCulture)}");
        text.AppendLine($"  min_child_weight: {calibrator.Options.MinChildWeight.ToString("R", CultureInfo.InvariantCulture)}");
        text.AppendLine($"  subsample:      {calibrator.Options.Subsample.ToString("R", CultureInfo.InvariantCulture)}");
        text.AppendLine($"  colsample:      {calibrator.Options.ColSampleByTree.ToString("R", CultureInfo.InvariantCulture)}");
        text.AppendLine($"  reg_lambda:     {calibrator.Options.RegLambda.ToString("R", CultureInfo.InvariantCulture)}");
        text.AppendLine($"  max_bin:        {calibrator.Options.MaxBins}");
        text.AppendLine($"  seed:           {calibrator.Options.Seed}");
        text.AppendLine();

        text.AppendLine("Feature importance (permutation, normalized):");
        string[] names = calibrator.Features.Names();
        for (int i = 0; i < names.Length; i++)
        {
            double importance = i < stats.PermutationImportance.Length ? stats.PermutationImportance[i] : 0.0;
            int splits = i < stats.SplitCount.Length ? stats.SplitCount[i] : 0;
            text.AppendLine($"  {names[i]}: {importance:F3}  ({splits:N0} splits)");
        }

        text.AppendLine();
        text.AppendLine("Note: importance is permutation-based (the rise in RMSE when a feature is");
        text.AppendLine("shuffled), not XGBoost's gain. Values are not comparable term by term with");
        text.AppendLine("the Python report; the ranking is.");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>
    /// Reads the fold-to-fold spread relative to the accuracy being reported. A spread is
    /// only meaningful against the size of the thing it varies around.
    /// </summary>
    private static string DescribeSpread(CrossValidationReport cv)
    {
        if (cv.PerFold.Length < 2 || !(cv.OutOfFold.Mad > 0) || double.IsNaN(cv.MadSpread))
            return "Too few folds to judge how much the estimate varies.";

        double relative = cv.MadSpread / cv.OutOfFold.Mad;
        return relative switch
        {
            < 0.05 => "The folds agree closely, so the pooled figure is a stable estimate.",
            < 0.15 => "The folds vary a little; the pooled figure is a reasonable estimate.",
            _ => "The folds disagree substantially, so the pooled figure is an average over "
                 + "populations the model handles differently rather than a description of any "
                 + "one of them.",
        };
    }

    /// <summary>
    /// Plain-language reading of the gap between in-sample and out-of-fold accuracy. The
    /// number alone does not say whether it is large, and "large" here is relative to the
    /// error being corrected rather than absolute.
    /// </summary>
    private static string OptimismVerdict(CrossValidationReport cv)
    {
        if (!(cv.OutOfFold.Mad > 0)) return "not assessable";

        double relative = cv.OptimismMad / cv.OutOfFold.Mad;
        return relative switch
        {
            < 0.05 => "negligible; the fit is driven by the instrument, not these peptides",
            < 0.15 => "modest",
            < 0.30 => "substantial: the fit leans on the particular peptides in this run",
            _ => "large: the fit is thin, and reusing this model elsewhere would disappoint",
        };
    }

    private static void AppendSummary(StringBuilder text, ErrorSummary summary, ErrorSummary? ppm)
    {
        text.AppendLine($"  Matches: {summary.Count:N0}");

        // Both scales, always. Th is what an ion trap is specified in and ppm is what a
        // high-resolution instrument is specified in, and the same file can be read by
        // people who think in either.
        if (ppm is ErrorSummary p)
        {
            text.AppendLine($"  Mean delta:   {summary.Mean,9:F4} Th   {p.Mean,8:F2} ppm");
            text.AppendLine($"  Median delta: {summary.Median,9:F4} Th   {p.Median,8:F2} ppm");
            text.AppendLine($"  Std delta:    {summary.StdDev,9:F4} Th   {p.StdDev,8:F2} ppm");
            text.AppendLine($"  MAD delta:    {summary.Mad,9:F4} Th   {p.Mad,8:F2} ppm");
            text.AppendLine($"  RMS delta:    {summary.Rms,9:F4} Th   {p.Rms,8:F2} ppm");
            text.AppendLine($"  MAE delta:    {summary.Mae,9:F4} Th   {p.Mae,8:F2} ppm");
            return;
        }

        text.AppendLine($"  Mean delta m/z:   {summary.Mean:F4} Th");
        text.AppendLine($"  Median delta m/z: {summary.Median:F4} Th");
        text.AppendLine($"  Std delta m/z:    {summary.StdDev:F4} Th");
        text.AppendLine($"  MAD delta m/z:    {summary.Mad:F4} Th");
        text.AppendLine($"  RMS delta m/z:    {summary.Rms:F4} Th");
        text.AppendLine($"  MAE delta m/z:    {summary.Mae:F4} Th");
    }

    private static double Reduction(double before, double after) =>
        before > 0 ? (before - after) / before * 100.0 : 0.0;

    private static string DescribeTolerance(MatchOptions options) =>
        options.TolerancePpm > 0
            ? $"+/-{options.TolerancePpm:F1} ppm"
            : $"+/-{options.MzToleranceTh:F3} Th";
}
