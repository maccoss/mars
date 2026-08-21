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
    }

    public static void Write(
        string path,
        Data data,
        TrainingStatistics? statistics,
        MatchStatistics matchStatistics,
        IReadOnlyList<string> inputFiles,
        string toleranceDescription,
        string version)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var html = new StringBuilder(1 << 20);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>MARS QC report</title>");
        html.Append("<style>").Append(Style).Append("</style></head><body><main>");

        html.Append("<h1>MARS QC report</h1>");
        html.Append("<p class=\"sub\">MARS ").Append(Svg.Escape(version)).Append(" &middot; ")
            .Append(inputFiles.Count.ToString("N0", CultureInfo.InvariantCulture))
            .Append(inputFiles.Count == 1 ? " input file" : " input files").Append("</p>");

        AppendVerdict(html, statistics);
        AppendSummaryTables(html, statistics, matchStatistics, inputFiles, toleranceDescription);

        Figure(html, "Mass error distribution",
            "The uncorrected error against what is left after the model's correction. If these "
            + "two distributions are not visibly different, the model found nothing to remove.",
            Charts.ErrorHistogram(data.ErrorBefore, data.ErrorAfter, "Th"));

        Figure(html, "Error across retention time and fragment m/z",
            "Median error per cell. Structure here is the systematic component MARS exists to "
            + "remove; a uniformly blank panel after correction is the goal.",
            Charts.ErrorHeatmap(data.RetentionTime, data.FragmentMz, data.ErrorBefore, "Th", "Before correction"));

        if (data.ErrorAfter.Length == data.ErrorBefore.Length)
        {
            Figure(html, null, null,
                Charts.ErrorHeatmap(data.RetentionTime, data.FragmentMz, data.ErrorAfter, "Th", "After correction"));
        }

        if (data.Importance.Count > 0)
        {
            Figure(html, "Feature importance",
                "Permutation importance: how much the validation error degrades when one feature "
                + "is shuffled. A feature near zero is carrying no weight and could be dropped.",
                Charts.FeatureImportance(data.ImportanceNames, data.Importance));
        }

        html.Append("<h2>Error against each feature</h2>");
        html.Append("<p class=\"note\">Binned density of the uncorrected error, with the median "
                  + "error per column drawn over it before and after correction. The trend line is "
                  + "the part to read: it is what the model has to capture, and how much of it is "
                  + "left afterwards.</p>");

        foreach ((string name, double[] values) in data.Features)
        {
            Figure(html, name, null,
                Charts.FeatureVersusError(values, data.ErrorBefore, data.ErrorAfter, name, "Th"));
        }

        html.Append("</main></body></html>");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
    }

    private static void AppendVerdict(StringBuilder html, TrainingStatistics? statistics)
    {
        if (statistics is null) return;

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
            .Append(verdict).Append("</div>");
    }

    private static void AppendSummaryTables(
        StringBuilder html, TrainingStatistics? statistics, MatchStatistics matchStatistics,
        IReadOnlyList<string> inputFiles, string toleranceDescription)
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
    private const string Style = """
        :root {
          --bg: #ffffff; --fg: #1a1d21; --muted: #61686f; --grid: #e8ebee;
          --axis: #9aa2aa; --card: #f7f8fa; --border: #dfe3e8; --accent: #3f7fbf;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #14171a; --fg: #e6e9ec; --muted: #98a1a9; --grid: #23282d;
            --axis: #555d65; --card: #1b1f23; --border: #2a2f35; --accent: #6ba4dd;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; background: var(--bg); color: var(--fg);
          font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif;
        }
        main { max-width: 900px; margin: 0 auto; padding: 32px 20px 64px; }
        h1 { font-size: 24px; margin: 0 0 4px; }
        h2 { font-size: 15px; margin: 28px 0 10px; letter-spacing: .01em; }
        h3 { font-size: 14px; margin: 0 0 4px; }
        .sub { color: var(--muted); margin: 0 0 20px; font-size: 13px; }
        .verdict {
          background: var(--card); border: 1px solid var(--border); border-left: 3px solid var(--accent);
          border-radius: 4px; padding: 12px 14px; margin: 0 0 22px;
        }
        .note { color: var(--muted); font-size: 13px; margin: 0 0 16px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 18px; }
        section { background: var(--card); border: 1px solid var(--border); border-radius: 4px; padding: 12px 14px; }
        section h2 { margin-top: 0; }
        table { width: 100%; border-collapse: collapse; font-size: 13px; }
        td, th { padding: 3px 0; text-align: left; vertical-align: top; }
        th { color: var(--muted); font-weight: 500; font-size: 12px; }
        .num { text-align: right; font-variant-numeric: tabular-nums; }
        .files { margin: 0; padding-left: 18px; font-size: 13px; word-break: break-all; }
        figure {
          margin: 20px 0 0; padding: 14px; background: var(--card);
          border: 1px solid var(--border); border-radius: 4px;
        }
        figcaption { color: var(--muted); font-size: 13px; margin: 0 0 8px; }
        svg { display: block; }
        """;
}
