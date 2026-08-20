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
        AppendSummary(text, stats.Before);
        text.AppendLine();

        text.AppendLine("After Calibration:");
        AppendSummary(text, stats.After);
        text.AppendLine();

        text.AppendLine($"Improvement: {Reduction(stats.Before.StdDev, stats.After.StdDev):F1}% reduction in std dev");
        text.AppendLine($"             {Reduction(stats.Before.Mad, stats.After.Mad):F1}% reduction in MAD");
        text.AppendLine($"             {Reduction(stats.Before.Rms, stats.After.Rms):F1}% reduction in RMS");
        text.AppendLine();
        text.AppendLine();

        text.AppendLine("Calibration Model Summary");
        text.AppendLine(new string('=', 40));
        text.AppendLine($"Matched fragments: {stats.RowsMatched:N0}");
        text.AppendLine($"Training samples:  {stats.RowsUsed:N0}");
        text.AppendLine($"Train/Val split:   {stats.RowsTrain:N0} / {stats.RowsValidation:N0}");
        text.AppendLine($"Spectra examined:  {matchStatistics.SpectraSeen:N0}");
        text.AppendLine($"Library precursors matched: {matchStatistics.UniqueEntriesMatched:N0}");
        text.AppendLine();

        text.AppendLine("Model performance:");
        text.AppendLine($"  Train MAE:  {stats.TrainMae:F4} Th");
        text.AppendLine($"  Train RMSE: {stats.TrainRmse:F4} Th");
        if (stats.RowsValidation > 0)
        {
            text.AppendLine($"  Val MAE:    {stats.ValidationMae:F4} Th");
            text.AppendLine($"  Val RMSE:   {stats.ValidationRmse:F4} Th");
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

    private static void AppendSummary(StringBuilder text, ErrorSummary summary)
    {
        text.AppendLine($"  Matches: {summary.Count:N0}");
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
