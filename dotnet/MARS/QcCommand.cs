// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the qc command in mars/cli.py: report current mass accuracy without
// training a model or writing any file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using MARS.Core;
using MARS.IO;
using MARS.Report;

namespace MARS.Cli;

public static class QcCommand
{
    public static int Run(CommandLineArgs args)
    {
        if (args.Flag("help", "h"))
        {
            Console.Error.WriteLine("""
                Usage: mars qc [options] [<file.mzML> ...]

                Matches library fragments and reports the mass accuracy already present in
                the files. Trains nothing and writes no mzML.

                Options:
                      --mzml <path>          mzML file or glob (repeatable)
                      --mzml-dir <dir>       Directory of mzML files
                      --prism-csv <path>     Skyline PRISM report CSV
                      --library <path>       .blib, report-lib.parquet, or PRISM .csv
                      --diann-report <path>  DIA-NN report.parquet
                      --resolution <mode>    unit, hram or auto (default auto: read the
                                             mass analyzer from the mzML and pick)
                      --tolerance <Th>       Fragment tolerance in Th (default 0.3)
                      --tolerance-ppm <ppm>  Fragment tolerance in ppm (default 10 on
                                             high-resolution data)
                      --min-intensity <n>    Minimum peak intensity (default 500)
                      --max-isolation-window <Th>
                                             Skip wider isolation windows
                      --temperature-dir <d>  Directory of RFA2-/RFC2- temperature CSVs
                      --output <path>        Report path (default mars_qc_summary.txt)
                      --html-report <path>   Where to write the figures (default
                                             mars_qc_report.html beside the summary).
                                             One self-contained file, safe to email
                      --no-html-report       Skip the figures and write only the summary
                      --by-file              Report each input file separately
                  -v, --verbose              Verbose output
                """);
            return Program.ExitSuccess;
        }

        Log.Verbose = args.Flag("verbose", "v");

        var patterns = new List<string>(args.Strings("mzml", "mzML"));
        patterns.AddRange(args.Positional);
        List<string> mzmlFiles = CommandLineArgs.ResolveMzMLFiles(patterns, args.String("mzml-dir"));
        if (mzmlFiles.Count == 0)
        {
            Log.Error("No mzML files found. Use --mzml, --mzml-dir, or pass files as arguments.");
            return Program.ExitInputError;
        }

        var matchOptions = new MatchOptions
        {
            MzToleranceTh = args.Double("tolerance") ?? ResolutionMode.DefaultToleranceTh,
            TolerancePpm = args.Double("tolerance-ppm") ?? 0,
            MinIntensity = args.Double("min-intensity") ?? 500.0,
            MaxIsolationWindowWidth = args.Double("max-isolation-window"),
        };

        ResolutionMode resolution = ResolutionMode.Resolve(args, mzmlFiles, matchOptions, Log.Info);

        string reportPath = args.String("output") ?? "mars_qc_summary.txt";
        bool byFile = args.Flag("by-file");
        bool noHtmlReport = args.Flag("no-html-report");
        string htmlReportPath = args.String("html-report") ?? DefaultHtmlPath(reportPath);
        string? temperatureDirectory = args.String("temperature-dir");

        // Read before the check below rather than inside LoadLibrary, so that every option
        // this command understands has been seen by the time the check runs.
        var librarySource = LibrarySource.From(args);

        // Everything is read; refuse a typo now rather than after minutes of work.
        args.RejectUnknown();

        var runNames = new List<string>();
        foreach (string file in mzmlFiles) runNames.Add(Path.GetFileName(file));

        SpectralLibrary library = librarySource.Load(runNames, keepSequences: false, Log.Info);
        var stopwatch = Stopwatch.StartNew();

        var text = new StringBuilder();
        text.AppendLine("Mars QC Report (pre-calibration)");
        text.AppendLine(new string('=', 40));
        text.AppendLine();
        text.AppendLine($"Files: {mzmlFiles.Count}");
        text.AppendLine($"Tolerance: {(matchOptions.TolerancePpm > 0 ? $"+/-{matchOptions.TolerancePpm:F1} ppm" : $"+/-{matchOptions.MzToleranceTh:F3} Th")}");
        text.AppendLine($"Minimum intensity: {matchOptions.MinIntensity:N0}");
        text.AppendLine();

        // Reporting accuracy needs only two features. The figures want every feature there
        // is, since a panel per feature is most of their value, and computing them costs one
        // pass over peaks MARS has already decoded. Collect the wider set only when the
        // figures are actually going to be drawn.
        var infoByFile = new Dictionary<string, MzMLFileInfo>(StringComparer.OrdinalIgnoreCase);
        var temperatureByFile = new Dictionary<string, TemperatureSet>(StringComparer.OrdinalIgnoreCase);
        bool anyRfa2 = false, anyRfc2 = false;

        foreach (string file in mzmlFiles)
        {
            infoByFile[file] = MzMLFile.Inspect(file);
            if (temperatureDirectory is null) continue;

            TemperatureSet temperatures = TemperatureCsvReader.Find(file, temperatureDirectory, Log.Info);
            temperatureByFile[file] = temperatures;
            anyRfa2 |= temperatures.Rfa2 is not null;
            anyRfc2 |= temperatures.Rfc2 is not null;
        }

        MarsFeature[] collect;
        if (noHtmlReport)
        {
            collect = new[] { MarsFeature.FragmentMz, MarsFeature.PrecursorMz };
        }
        else
        {
            bool injectionTimeAvailable = CalibrateCommand.ProbeInjectionTime(infoByFile[mzmlFiles[0]]);
            collect = FragmentMatcher.CollectedFeatures(injectionTimeAvailable, anyRfa2, anyRfc2);
        }

        var combined = new MatchTable(collect, keepDetail: !noHtmlReport);
        var matcher = new FragmentMatcher(library, matchOptions);

        foreach (string file in mzmlFiles)
        {
            MzMLFileInfo info = infoByFile[file];
            int rowsBefore = combined.Count;
            temperatureByFile.TryGetValue(file, out TemperatureSet? temperatures);

            Log.Info($"Matching: {Path.GetFileName(file)}");
            foreach (SpectrumRecord spectrum in MzMLFile.ReadSpectra(info, msLevel: 2))
                matcher.MatchSpectrum(spectrum, temperatures, combined);

            Log.Info($"  {combined.Count - rowsBefore:N0} fragment matches");

            // Per-file numbers come from this file's slice of the shared table, so each
            // spectrum is still matched exactly once.
            if (byFile && combined.Count > rowsBefore)
            {
                text.AppendLine(Path.GetFileName(file));
                AppendSummary(text, combined, rowsBefore, combined.Count - rowsBefore);
                text.AppendLine();
            }
        }

        if (combined.Count == 0)
        {
            Log.Error("No fragment matches found. Check that the library describes these runs.");
            return Program.ExitInsufficientTrainingData;
        }

        text.AppendLine("All files");
        AppendSummary(text, combined, 0, combined.Count);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(reportPath, text.ToString());

        Console.Out.Write(text.ToString());

        if (!noHtmlReport)
        {
            QcHtmlReport.Write(
                htmlReportPath,
                BuildReportData(combined, collect),
                statistics: null,
                matcher.Statistics,
                mzmlFiles,
                matchOptions.TolerancePpm > 0
                    ? $"{matchOptions.TolerancePpm:0.##} ppm"
                    : $"{matchOptions.MzToleranceTh:0.###} Th",
                MarsInfo.Version,
                Uncorrected(combined, resolution),
                resolution.ReportInPpm ? ErrorScale.Ppm : ErrorScale.Th);
            Log.Info($"Wrote QC figures to {htmlReportPath}");
        }

        Log.Info($"Wrote {reportPath} in {stopwatch.Elapsed.TotalSeconds:F1} s");
        return Program.ExitSuccess;
    }

    /// <summary>Puts the figures next to the text report rather than the working directory.</summary>
    private static string DefaultHtmlPath(string reportPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        return string.IsNullOrEmpty(directory)
            ? "mars_qc_report.html"
            : Path.Combine(directory, "mars_qc_report.html");
    }

    /// <summary>
    /// The measured error, summarized on the scale the report will be drawn in.
    /// </summary>
    private static ErrorSummary Uncorrected(MatchTable table, ResolutionMode resolution)
    {
        ReadOnlySpan<double> delta = table.DeltaMz.Items.AsSpan(0, table.Count);
        if (!resolution.ReportInPpm) return MarsStatistics.Summarize(delta);

        double[] fragmentMz = table.Column(MarsFeature.FragmentMz).Items;
        var ppm = new double[table.Count];
        for (int i = 0; i < ppm.Length; i++)
        {
            double mz = fragmentMz[i];
            ppm[i] = mz > 0 ? delta[i] / mz * 1e6 : 0.0;
        }

        return MarsStatistics.Summarize(ppm);
    }

    /// <summary>
    /// Collects the per-row values the figures are drawn from. There is no model here, so
    /// there is no corrected error and no importance; the report renders the measured error
    /// alone rather than pretending to a before-and-after.
    /// </summary>
    private static QcHtmlReport.Data BuildReportData(MatchTable table, MarsFeature[] collected)
    {
        int rows = table.Count;
        var features = new List<(string Name, double[] Values)>(collected.Length);
        foreach (MarsFeature feature in collected)
            features.Add((MarsFeatures.NameOf(feature), table.Column(feature).Items[..rows]));

        return new QcHtmlReport.Data
        {
            ErrorBefore = table.DeltaMz.Items[..rows],
            ErrorAfter = Array.Empty<double>(),
            RetentionTime = table.RetentionTime is null ? Array.Empty<double>() : table.RetentionTime.Items[..rows],
            FragmentMz = table.Has(MarsFeature.FragmentMz)
                ? table.Column(MarsFeature.FragmentMz).Items[..rows]
                : Array.Empty<double>(),
            Features = features,
            ImportanceNames = Array.Empty<string>(),
            Importance = Array.Empty<double>(),
        };
    }

    private static void AppendSummary(StringBuilder text, MatchTable table, int start, int count)
    {
        ReadOnlySpan<double> delta = table.DeltaMz.Items.AsSpan(start, count);
        ErrorSummary summary = MarsStatistics.Summarize(delta);

        // Converted per row from that fragment's own m/z, not by dividing the aggregate by a
        // nominal mass - the fragments here span most of a factor of four in m/z, so the
        // shortcut would be wrong by about that much.
        double[] fragmentMz = table.Column(MarsFeature.FragmentMz).Items;
        var ppm = new double[count];
        for (var i = 0; i < count; i++)
        {
            double mz = fragmentMz[start + i];
            ppm[i] = mz > 0 ? delta[i] / mz * 1e6 : 0.0;
        }

        ErrorSummary p = MarsStatistics.Summarize(ppm);

        // Both scales, in the same layout calibrate uses. Th is what an ion trap is specified
        // in; ppm is the scale a high-resolution instrument is specified in and the only one
        // that compares across instruments.
        text.AppendLine($"  Matches: {summary.Count:N0}");
        text.AppendLine($"  Mean delta:   {summary.Mean,9:F4} Th   {p.Mean,8:F2} ppm");
        text.AppendLine($"  Median delta: {summary.Median,9:F4} Th   {p.Median,8:F2} ppm");
        text.AppendLine($"  Std delta:    {summary.StdDev,9:F4} Th   {p.StdDev,8:F2} ppm");
        text.AppendLine($"  MAD delta:    {summary.Mad,9:F4} Th   {p.Mad,8:F2} ppm");
        text.AppendLine($"  RMS delta:    {summary.Rms,9:F4} Th   {p.Rms,8:F2} ppm");
        text.AppendLine($"  MAE delta:    {summary.Mae,9:F4} Th   {p.Mae,8:F2} ppm");
    }

}
