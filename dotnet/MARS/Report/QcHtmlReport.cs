// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// The single-file HTML QC report.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MARS.Core;

namespace MARS.Report;

/// <summary>
/// Writes the QC figures and summary as one self-contained HTML file.
///
/// Self-contained is the requirement, not a nicety: the report is meant to be attached to
/// an email and opened by someone who does not have the data, the tool, or a network path
/// back to either. Everything is inline - the figures are SVG elements in the markup, the
/// styling is one embedded stylesheet, and there is no script. Nothing is fetched when the
/// file is opened, which also means it renders in mail clients that block remote content.
/// </summary>
public static class QcHtmlReport
{
    /// <summary>Per-row data the figures are drawn from.</summary>
    public sealed class Data
    {
        public required double[] ErrorBefore { get; init; }

        /// <summary>Error left after the model's correction. Empty when no model was fitted.</summary>
        public required double[] ErrorAfter { get; init; }

        public required double[] RetentionTime { get; init; }

        public required double[] FragmentMz { get; init; }

        /// <summary>Feature name to per-row values, in model order.</summary>
        public required IReadOnlyList<(string Name, double[] Values)> Features { get; init; }

        public required IReadOnlyList<string> ImportanceNames { get; init; }

        public required IReadOnlyList<double> Importance { get; init; }

        /// <summary>Cross-validation results, or null when a single model was fitted.</summary>
        public CrossValidationReport? CrossValidation { get; init; }
    }

    /// <param name="statistics">Training statistics, or null when no model was fitted.</param>
    /// <param name="uncorrected">
    /// Summary of the uncorrected error. Used when <paramref name="statistics"/> is null,
    /// which is the `mars qc` case: there is no before-and-after to show, but the error
    /// that is there is the whole point of the report.
    /// </param>
    public static void Write(
        string path,
        Data data,
        TrainingStatistics? statistics,
        MatchStatistics matchStatistics,
        IReadOnlyList<string> inputFiles,
        string toleranceDescription,
        string version,
        ErrorSummary? uncorrected = null)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var html = new StringBuilder(1 << 20);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>MARS QC report</title>");
        html.Append("<style>").Append(Style).Append("</style></head><body><main>");

        html.Append("<h1>MARS QC report</h1>");
        if (statistics is null)
            html.Append("<p class=\"sub\">Pre-calibration. No model was fitted.</p>");
        html.Append("<p class=\"sub\">MARS ").Append(Svg.Escape(version)).Append(" &middot; ")
            .Append(inputFiles.Count.ToString("N0", CultureInfo.InvariantCulture))
            .Append(inputFiles.Count == 1 ? " input file" : " input files").Append("</p>");

        AppendVerdict(html, statistics, uncorrected, data.CrossValidation);
        AppendSummaryTables(html, statistics, uncorrected, matchStatistics, inputFiles, toleranceDescription);

        bool corrected = data.ErrorAfter.Length == data.ErrorBefore.Length;

        Figure(html, "Mass error distribution",
            corrected
                ? "The uncorrected error against what is left in these files after correction. "
                  + "If these two distributions are not visibly different, the model found "
                  + "nothing to remove."
                : "The mass error as measured, before any correction. Its width is what a model "
                  + "would have to work with; how much of it is removable is what calibrating "
                  + "would show.",
            Charts.ErrorHistogram(data.ErrorBefore, data.ErrorAfter, "Th"));

        Figure(html, "Error across retention time and fragment m/z",
            corrected
                ? "Median error per cell, both panels on one color scale so they can be compared "
                  + "directly. Structure on the left is the systematic component MARS exists to "
                  + "remove; the right panel washing out is the goal. Blank cells held too few "
                  + "fragments to take a median from."
                : "Median error per cell. Visible structure - bands, gradients, blocks - is "
                  + "systematic error, and systematic error is the kind MARS can remove. A "
                  + "featureless panel means the error is mostly noise. Blank cells held too few "
                  + "fragments to take a median from.",
            Charts.ErrorHeatmapPair(
                data.RetentionTime, data.FragmentMz, data.ErrorBefore, data.ErrorAfter, "Th"));

        AppendCrossValidation(html, data.CrossValidation);

        if (data.Importance.Count > 0)
        {
            Figure(html, "Feature importance",
                "Permutation importance: how much the validation error degrades when one feature "
                + "is shuffled. A feature near zero is carrying no weight and could be dropped.",
                Charts.FeatureImportance(data.ImportanceNames, data.Importance));
        }

        if (data.Features.Count > 0)
        {
            html.Append("<h2>Error against each feature</h2>");
            html.Append("<p class=\"note\">Binned density of the measured error, with the median "
                      + "error per column drawn over it"
                      + (corrected ? " before and after correction" : string.Empty)
                      + ". The trend line is the part to read: a sloped line is a real dependence"
                      + (corrected
                          ? ", and a flat line after correction means the model captured it."
                          : " that a model could exploit; a flat line means this feature says "
                            + "nothing about the error here.")
                      + "</p>");
        }

        foreach ((string name, double[] values) in data.Features)
        {
            Figure(html, name, null,
                Charts.FeatureVersusError(values, data.ErrorBefore, data.ErrorAfter, name, "Th"));
        }

        html.Append("</main></body></html>");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
    }

    private static void AppendVerdict(
        StringBuilder html, TrainingStatistics? statistics, ErrorSummary? uncorrected,
        CrossValidationReport? crossValidation)
    {
        if (statistics is null)
        {
            AppendPreCalibrationVerdict(html, uncorrected);
            return;
        }

        double before = statistics.Before.Mad;
        double after = statistics.After.Mad;
        if (!(before > 0)) return;

        double reduction = 100 * (1 - (after / before));
        // Say what the numbers mean rather than leaving the reader to decide what counts as
        // a good result. A run with little systematic error left is a legitimate outcome and
        // should not read as a failure.
        string verdict = reduction switch
        {
            >= 25 => "The correction removed a substantial part of the mass error.",
            >= 10 => "The correction removed a modest part of the mass error.",
            >= 2 => "The correction changed little. This data was already close to calibrated.",
            _ => "The correction removed essentially nothing. There is no systematic error here "
                 + "to remove, and the corrected file is little different from the input.",
        };

        html.Append("<div class=\"verdict\"><strong>")
            .Append(reduction.ToString("0.0", CultureInfo.InvariantCulture))
            .Append("% reduction</strong> in median absolute error, ")
            .Append(before.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" &rarr; ")
            .Append(after.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" Th. ")
            .Append(verdict);

        if (crossValidation is CrossValidationReport cv)
        {
            // Two numbers, two questions. The one above is what these files now look like;
            // this is what the same procedure achieves on a run it was not fitted to.
            html.Append(" On data not used to fit, cross-validation puts it at ")
                .Append(Format(cv.OutOfFold.Mad)).Append(" Th (")
                .Append(cv.OutOfFold.MadReduction.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("%).");
        }

        html.Append("</div>");
    }

    /// <summary>
    /// What `mars qc` can honestly say: how big the error is, and how much of it is a plain
    /// offset. It cannot say how much is removable - only fitting a model answers that - so
    /// it does not pretend to.
    /// </summary>
    private static void AppendPreCalibrationVerdict(StringBuilder html, ErrorSummary? uncorrected)
    {
        if (uncorrected is not ErrorSummary summary || summary.Count == 0) return;

        html.Append("<div class=\"verdict\"><strong>")
            .Append(Format(summary.Mad)).Append(" Th</strong> median absolute error across ")
            .Append(summary.Count.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" matched fragments, with a median of ")
            .Append(Format(summary.Median)).Append(" Th. ");

        // A median well away from zero is a straight offset across the whole run, which is
        // the most obviously correctable thing there is.
        double bias = Math.Abs(summary.Median);
        html.Append(bias > summary.Mad * 0.5
            ? "The median is a long way from zero, so a systematic offset runs through the "
              + "whole cohort. That part is straightforwardly correctable."
            : "The median is close to zero, so there is no large constant offset. Whether the "
              + "spread is systematic enough to remove is what fitting a model would show.");

        html.Append(" Run <code>mars calibrate</code> to find out how much of this is "
                  + "removable.</div>");
    }

    /// <summary>
    /// Per-fold accuracy and the spread across folds.
    /// </summary>
    /// <remarks>
    /// The spread is the point. One held-out number says how the model did on one split;
    /// five say whether that number was luck. A tight spread means the estimate is stable,
    /// a wide one means the cohort has regions the model handles very differently and the
    /// headline figure is an average over them.
    /// </remarks>
    private static void AppendCrossValidation(StringBuilder html, CrossValidationReport? cv)
    {
        if (cv is null) return;

        html.Append("<h2>Cross-validation</h2>");
        html.Append("<p class=\"note\">")
            .Append(cv.Folds.ToString(CultureInfo.InvariantCulture))
            .Append(" folds split by peptide, over ")
            .Append(cv.Groups.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" peptides. Every row was scored by a model that never saw its peptide, so "
                  + "so these figures estimate what this correction would achieve on a run it was "
                  + "not fitted to, which is what mars apply does. The figures elsewhere describe "
                  + "these files, which the applied model was fitted to - as mass calibration "
                  + "normally is.</p>");

        html.Append("<figure><table class=\"folds\">");
        html.Append("<tr><th>fold</th><th class=\"num\">rows</th><th class=\"num\">MAD (Th)</th>"
                  + "<th class=\"num\">RMS (Th)</th><th class=\"num\">reduction</th>"
                  + "<th class=\"num\">Pearson r</th></tr>");

        for (int i = 0; i < cv.PerFold.Length; i++)
        {
            FoldMetrics fold = cv.PerFold[i];
            html.Append("<tr><td>").Append(i + 1).Append("</td>")
                .Append("<td class=\"num\">").Append(fold.Rows.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>")
                .Append("<td class=\"num\">").Append(Format(fold.Mad)).Append("</td>")
                .Append("<td class=\"num\">").Append(Format(fold.Rms)).Append("</td>")
                .Append("<td class=\"num\">").Append(fold.MadReduction.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</td>")
                .Append("<td class=\"num\">").Append(Format(fold.PearsonR)).Append("</td></tr>");
        }

        html.Append("<tr class=\"total\"><td>pooled</td>")
            .Append("<td class=\"num\">").Append(cv.OutOfFold.Rows.ToString("N0", CultureInfo.InvariantCulture)).Append("</td>")
            .Append("<td class=\"num\">").Append(Format(cv.OutOfFold.Mad)).Append("</td>")
            .Append("<td class=\"num\">").Append(Format(cv.OutOfFold.Rms)).Append("</td>")
            .Append("<td class=\"num\">").Append(cv.OutOfFold.MadReduction.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</td>")
            .Append("<td class=\"num\">").Append(Format(cv.OutOfFold.PearsonR)).Append("</td></tr>");

        html.Append("<tr class=\"spread\"><td>spread</td><td class=\"num\"></td>")
            .Append("<td class=\"num\">").Append(PlusMinus(cv.MadSpread)).Append("</td>")
            .Append("<td class=\"num\">").Append(PlusMinus(cv.RmsSpread)).Append("</td>")
            .Append("<td class=\"num\">").Append(PlusMinus(cv.MadReductionSpread)).Append("</td>")
            .Append("<td class=\"num\">").Append(PlusMinus(cv.PearsonRSpread)).Append("</td></tr>");

        html.Append("</table><figcaption>Spread is the standard deviation across folds.</figcaption>");
        html.Append("</figure>");

        var foldMad = new double[cv.PerFold.Length];
        var foldR = new double[cv.PerFold.Length];
        for (int i = 0; i < cv.PerFold.Length; i++)
        {
            foldMad[i] = cv.PerFold[i].Mad;
            foldR[i] = cv.PerFold[i].PearsonR;
        }

        Figure(html, null,
            "Each fold's accuracy against the pooled figure. Folds sitting close together mean "
            + "the estimate is stable and the pooled number can be read as-is; folds scattered "
            + "across the band mean the cohort has regions the model handles very differently, "
            + "and the pooled number is an average over them.",
            Charts.FoldSpread(foldMad, cv.OutOfFold.Mad, cv.MadSpread, "Th", "Median absolute residual"));

        Figure(html, null, null,
            Charts.FoldSpread(foldR, cv.OutOfFold.PearsonR, cv.PearsonRSpread, "r", "Pearson correlation"));

        html.Append("<div class=\"verdict\"><strong>Gap ")
            .Append(Format(cv.OptimismMad)).Append(" Th.</strong> The correction leaves ")
            .Append(Format(cv.InSample.Mad))
            .Append(" Th on the data it was fitted to and ")
            .Append(Format(cv.OutOfFold.Mad))
            .Append(" Th on peptides it had not seen. ")
            .Append(OptimismVerdict(cv))
            .Append("</div>");
    }

    private static string PlusMinus(double value) =>
        double.IsNaN(value) ? "n/a" : "+/-" + value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string OptimismVerdict(CrossValidationReport cv)
    {
        if (!(cv.OutOfFold.Mad > 0)) return "The gap cannot be assessed.";

        double relative = cv.OptimismMad / cv.OutOfFold.Mad;
        return relative switch
        {
            < 0.05 => "The gap is negligible, so the fit describes the instrument rather than "
                      + "the particular peptides in this run.",
            < 0.15 => "The gap is modest.",
            < 0.30 => "The gap is substantial - the fit leans on the particular peptides in this "
                      + "run, so reusing this model elsewhere would do less well.",
            _ => "The gap is large. The fit is thin, and this model should not be reused on "
                 + "other runs.",
        };
    }

    private static void AppendSummaryTables(
        StringBuilder html, TrainingStatistics? statistics, ErrorSummary? uncorrected,
        MatchStatistics matchStatistics, IReadOnlyList<string> inputFiles,
        string toleranceDescription)
    {
        html.Append("<div class=\"grid\">");

        html.Append("<section><h2>Matching</h2><table>");
        Row(html, "Spectra examined", matchStatistics.SpectraSeen.ToString("N0", CultureInfo.InvariantCulture));
        Row(html, "Fragments matched", matchStatistics.FragmentsMatched.ToString("N0", CultureInfo.InvariantCulture));
        Row(html, "Library precursors matched", matchStatistics.UniqueEntriesMatched.ToString("N0", CultureInfo.InvariantCulture));
        Row(html, "Tolerance", toleranceDescription);
        html.Append("</table></section>");

        if (statistics is not null)
        {
            html.Append("<section><h2>Model</h2><table>");
            Row(html, "Training rows", statistics.RowsTrain.ToString("N0", CultureInfo.InvariantCulture));
            Row(html, "Held out", statistics.RowsValidation.ToString("N0", CultureInfo.InvariantCulture));
            Row(html, "Train MAE", Format(statistics.TrainMae) + " Th");
            if (statistics.RowsValidation > 0)
                Row(html, "Validation MAE", Format(statistics.ValidationMae) + " Th");
            html.Append("</table></section>");

            html.Append("<section><h2>Mass error</h2><table>");
            html.Append("<tr><th></th><th>before</th><th>after</th></tr>");
            Row3(html, "Median absolute deviation", statistics.Before.Mad, statistics.After.Mad);
            Row3(html, "Standard deviation", statistics.Before.StdDev, statistics.After.StdDev);
            Row3(html, "Median", statistics.Before.Median, statistics.After.Median);
            html.Append("</table></section>");
        }
        else if (uncorrected is ErrorSummary summary)
        {
            html.Append("<section><h2>Mass error</h2><table>");
            Row(html, "Median absolute deviation", Format(summary.Mad) + " Th");
            Row(html, "Standard deviation", Format(summary.StdDev) + " Th");
            Row(html, "Median", Format(summary.Median) + " Th");
            Row(html, "Mean absolute error", Format(summary.Mae) + " Th");
            html.Append("</table></section>");
        }

        html.Append("<section><h2>Input files</h2><ul class=\"files\">");
        foreach (string file in inputFiles)
            html.Append("<li>").Append(Svg.Escape(Path.GetFileName(file))).Append("</li>");
        html.Append("</ul></section>");

        html.Append("</div>");
    }

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append("<tr><td>").Append(Svg.Escape(label)).Append("</td><td class=\"num\">")
            .Append(Svg.Escape(value)).Append("</td></tr>");

    private static void Row3(StringBuilder html, string label, double before, double after) =>
        html.Append("<tr><td>").Append(Svg.Escape(label)).Append("</td><td class=\"num\">")
            .Append(Format(before)).Append("</td><td class=\"num\">")
            .Append(Format(after)).Append("</td></tr>");

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static void Figure(StringBuilder html, string? title, string? caption, string svg)
    {
        html.Append("<figure>");
        if (title is not null) html.Append("<h3>").Append(Svg.Escape(title)).Append("</h3>");
        if (caption is not null) html.Append("<figcaption>").Append(Svg.Escape(caption)).Append("</figcaption>");
        html.Append(svg).Append("</figure>");
    }

    // Light and dark are both handled, because a report that is emailed gets opened in
    // whatever the recipient happens to use.
    // White, deliberately, rather than following the reader's system theme. A QC report gets
    // printed, pasted into a slide, and read next to figures from other tools, all of which
    // assume a white page - and the density rasters cannot follow a theme anyway, so a dark
    // surround would leave them sitting in a bright rectangle.
    private const string Style = """
        :root {
          --bg: #ffffff; --fg: #1a1d21; --muted: #5b6169; --grid: #e6e9ec;
          --axis: #8b939b; --card: #ffffff; --border: #d9dee3; --accent: #3f7fbf;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; background: var(--bg); color: var(--fg);
          font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
        }
        main { max-width: 940px; margin: 0 auto; padding: 32px 20px 64px; }
        h1 { font-size: 25px; margin: 0 0 4px; }
        h2 { font-size: 16px; margin: 30px 0 10px; letter-spacing: .01em; }
        h3 { font-size: 15px; margin: 0 0 4px; }
        .sub { color: var(--muted); margin: 0 0 20px; font-size: 13px; }
        .verdict {
          background: #f6f8fa; border: 1px solid var(--border); border-left: 3px solid var(--accent);
          border-radius: 4px; padding: 12px 14px; margin: 0 0 22px;
        }
        .note { color: var(--muted); font-size: 13px; margin: 0 0 16px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 18px; }
        section { background: #f9fafb; border: 1px solid var(--border); border-radius: 4px; padding: 12px 14px; }
        section h2 { margin-top: 0; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; }
        td, th { padding: 3px 0; text-align: left; vertical-align: top; }
        th { color: var(--muted); font-weight: 500; font-size: 12px; }
        .num { text-align: right; font-variant-numeric: tabular-nums; }
        .files { margin: 0; padding-left: 18px; font-size: 13px; word-break: break-all; }
        figure {
          margin: 20px 0 0; padding: 14px; background: #ffffff;
          border: 1px solid var(--border); border-radius: 4px;
        }
        figcaption { color: var(--muted); font-size: 13px; margin: 8px 0 0; }
        table.folds { font-size: 13px; }
        table.folds th, table.folds td { padding: 4px 10px 4px 0; }
        table.folds tr.total td { border-top: 1px solid var(--border); font-weight: 600; }
        table.folds tr.spread td { color: var(--muted); }
        svg { display: block; }
        """;
}
