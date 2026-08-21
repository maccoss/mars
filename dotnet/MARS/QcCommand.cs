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
                      --tolerance <Th>       Fragment tolerance in Th (default 0.3)
                      --tolerance-ppm <ppm>  Fragment tolerance in ppm
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
            MzToleranceTh = args.Double("tolerance") ?? 0.3,
            TolerancePpm = args.Double("tolerance-ppm") ?? 0,
            MinIntensity = args.Double("min-intensity") ?? 500.0,
            MaxIsolationWindowWidth = args.Double("max-isolation-window"),
        };

        string reportPath = args.String("output") ?? "mars_qc_summary.txt";
        bool byFile = args.Flag("by-file");
        bool noHtmlReport = args.Flag("no-html-report");
        string htmlReportPath = args.String("html-report") ?? DefaultHtmlPath(reportPath);
        string? temperatureDirectory = args.String("temperature-dir");

        var runNames = new List<string>();
        foreach (string file in mzmlFiles) runNames.Add(Path.GetFileName(file));

        SpectralLibrary library = LoadLibrary(args, runNames);
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
                MarsStatistics.Summarize(combined.DeltaMz.Items.AsSpan(0, combined.Count)));
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

        text.AppendLine($"  Matches: {summary.Count:N0}");
        text.AppendLine($"  Mean delta m/z:   {summary.Mean:F4} Th");
        text.AppendLine($"  Median delta m/z: {summary.Median:F4} Th");
        text.AppendLine($"  Std delta m/z:    {summary.StdDev:F4} Th");
        text.AppendLine($"  MAD delta m/z:    {summary.Mad:F4} Th");
        text.AppendLine($"  RMS delta m/z:    {summary.Rms:F4} Th");

        // ppm is the more portable scale, so report it alongside.
        double[] fragmentMz = table.Column(MarsFeature.FragmentMz).Items;
        var ppm = new double[count];
        for (var i = 0; i < count; i++)
        {
            double mz = fragmentMz[start + i];
            ppm[i] = mz > 0 ? delta[i] / mz * 1e6 : 0.0;
        }

        ErrorSummary ppmSummary = MarsStatistics.Summarize(ppm);
        text.AppendLine($"  Median delta ppm: {ppmSummary.Median:F2} ppm");
        text.AppendLine($"  Std delta ppm:    {ppmSummary.StdDev:F2} ppm");
    }

    private static SpectralLibrary LoadLibrary(CommandLineArgs args, List<string> runNames)
    {
        string? prismCsv = args.String("prism-csv");
        string? libraryPath = args.String("library");
        var options = new PrismLibraryOptions { RunNames = runNames };

        if (prismCsv is not null) return PrismCsvLibraryReader.Load(prismCsv, options, Log.Info);
        if (libraryPath is null)
            throw new FileNotFoundException("A library is required: --prism-csv or --library.");

        if (libraryPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return PrismCsvLibraryReader.Load(libraryPath, options, Log.Info);
        if (libraryPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            return DiannParquetLibraryReader.Load(libraryPath, args.String("diann-report"), runNames, Log.Info);
        if (libraryPath.EndsWith(".blib", StringComparison.OrdinalIgnoreCase))
            return BlibLibraryReader.Load(libraryPath, args.Double("rt-window") ?? 0.083, Log.Info);

        throw new InvalidDataException(
            $"Unrecognized library type '{Path.GetExtension(libraryPath)}'. Expected .blib, .parquet or .csv.");
    }
}
