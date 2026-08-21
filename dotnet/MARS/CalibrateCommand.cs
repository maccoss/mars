// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the calibrate command in mars/cli.py.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MARS.Core;
using MARS.IO;
using MARS.Report;

namespace MARS.Cli;

public static class CalibrateCommand
{
    public static int Run(CommandLineArgs args)
    {
        if (args.Flag("help", "h"))
        {
            PrintHelp();
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

        string outputDirectory = args.String("output-dir") ?? ".";
        Directory.CreateDirectory(outputDirectory);

        string modelPath = args.String("model-path") ?? Path.Combine(outputDirectory, "mars_model.json");
        string reportPath = args.String("report") ?? Path.Combine(outputDirectory, "mars_qc_summary.txt");
        string? dumpMatchesPath = args.String("dump-matches");
        string? dumpPredictionsPath = args.String("dump-predictions");
        bool noHtmlReport = args.Flag("no-html-report");
        string htmlReportPath = args.String("html-report")
            ?? Path.Combine(outputDirectory, "mars_qc_report.html");
        bool keepDetail = dumpMatchesPath is not null || dumpPredictionsPath is not null || !noHtmlReport;

        var matchOptions = new MatchOptions
        {
            MzToleranceTh = args.Double("tolerance") ?? 0.3,
            TolerancePpm = args.Double("tolerance-ppm") ?? 0,
            MinIntensity = args.Double("min-intensity") ?? 500.0,
            MaxIsolationWindowWidth = args.Double("max-isolation-window"),
        };

        var calibrationOptions = new CalibrationOptions
        {
            NEstimators = args.Int("n-estimators") ?? 100,
            MaxDepth = args.Int("max-depth") ?? 6,
            LearningRate = args.Double("learning-rate") ?? 0.1,
            Seed = args.Int("seed") ?? 42,
            ValidationSplit = args.Double("validation-split") ?? 0.2,
            MaxTrainingRows = args.Int("max-training-rows") ?? 0,
            MaxDegreeOfParallelism = args.Int("threads") ?? -1,
        };

        var correctionOptions = new CorrectionOptions
        {
            MaxIsolationWindowWidth = matchOptions.MaxIsolationWindowWidth,
            PythonCompatibility = args.Flag("python-compat"),
            Monotonicity = ParseMonotonicity(args.String("on-reorder")),
        };

        int minTrainingRows = args.Int("min-training-rows") ?? 1000;
        bool noRecalibrate = args.Flag("no-recalibrate");
        string? temperatureDirectory = args.String("temperature-dir");

        Log.Info($"Found {mzmlFiles.Count} mzML file(s) to process");
        var stopwatch = Stopwatch.StartNew();

        // ---- Library ----------------------------------------------------------------
        var runNames = new List<string>();
        foreach (string file in mzmlFiles) runNames.Add(Path.GetFileName(file));

        SpectralLibrary library = LoadLibrary(args, runNames, keepSequences: keepDetail);

        // ---- Pass 1: match fragments across every input file -------------------------
        var temperatureByFile = new Dictionary<string, TemperatureSet>(StringComparer.OrdinalIgnoreCase);
        var infoByFile = new Dictionary<string, MzMLFileInfo>(StringComparer.OrdinalIgnoreCase);

        bool anyTemperature = false;
        bool anyRfa2 = false, anyRfc2 = false;
        foreach (string file in mzmlFiles)
        {
            MzMLFileInfo info = MzMLFile.Inspect(file);
            infoByFile[file] = info;

            if (temperatureDirectory is not null)
            {
                TemperatureSet temperatures = TemperatureCsvReader.Find(file, temperatureDirectory, Log.Info);
                temperatureByFile[file] = temperatures;
                anyTemperature |= !temperatures.IsEmpty;
                anyRfa2 |= temperatures.Rfa2 is not null;
                anyRfc2 |= temperatures.Rfc2 is not null;
            }
        }

        bool injectionTimeAvailable = ProbeInjectionTime(mzmlFiles[0], infoByFile[mzmlFiles[0]]);
        if (!injectionTimeAvailable)
            Log.Warn("No ion injection time in the first MS2 spectrum; the injection-time feature group is off.");

        MarsFeature[] collect = FragmentMatcher.CollectedFeatures(injectionTimeAvailable, anyRfa2, anyRfc2);
        var table = new MatchTable(collect, keepDetail: keepDetail);
        var matcher = new FragmentMatcher(library, matchOptions);

        foreach (string file in mzmlFiles)
        {
            Log.Info($"Matching: {Path.GetFileName(file)}");
            MzMLFileInfo info = infoByFile[file];
            temperatureByFile.TryGetValue(file, out TemperatureSet? temperatures);

            long before = table.Count;
            long spectra = 0;
            foreach (SpectrumRecord spectrum in MzMLFile.ReadSpectra(info, msLevel: 2))
            {
                matcher.MatchSpectrum(spectrum, temperatures, table);
                spectra++;
            }

            Log.Info($"  {spectra:N0} MS2 spectra, {table.Count - before:N0} fragment matches");
        }

        Log.Info($"Total matches: {table.Count:N0} from {matcher.Statistics.SpectraSeen:N0} spectra");
        Log.Info($"  unique library precursors matched: {matcher.Statistics.UniqueEntriesMatched:N0} " +
                 $"of {library.EntryCount:N0}");

        if (table.Count < minTrainingRows)
        {
            throw new InsufficientTrainingDataException(
                $"Only {table.Count:N0} fragment matches; at least {minTrainingRows:N0} are required. " +
                "Check that the library and the mzML files describe the same runs, and that the " +
                "tolerance is wide enough for the instrument.");
        }

        // Re-base acquisition time to the earliest matched spectrum, so the feature starts
        // near zero. The offset travels with the model and is subtracted again at
        // correction time; feeding raw Unix timestamps to a model trained on re-based ones
        // would push every inference row past the largest value it ever saw.
        double absoluteTimeOffset = 0;
        if (table.Has(MarsFeature.AbsoluteTime))
        {
            absoluteTimeOffset = table.MinOf(MarsFeature.AbsoluteTime);
            if (double.IsFinite(absoluteTimeOffset))
            {
                table.OffsetColumn(MarsFeature.AbsoluteTime, -absoluteTimeOffset);
                double span = table.MaxOf(MarsFeature.AbsoluteTime);
                Log.Info($"Acquisition time span: 0 to {span:F1} s ({span / 60:F1} min)");
            }
            else
            {
                absoluteTimeOffset = 0;
            }
        }

        if (dumpMatchesPath is not null)
        {
            MatchDumpWriter.Write(dumpMatchesPath, table, library);
            Log.Info($"Wrote {table.Count:N0} matches to {dumpMatchesPath}");
        }

        // ---- Train -------------------------------------------------------------------
        Log.Info("Training calibration model...");
        MzCalibrator calibrator = MzCalibrator.Fit(table, calibrationOptions, absoluteTimeOffset, Log.Info);
        TrainingStatistics stats = calibrator.Statistics!;

        Log.Info($"  train MAE {stats.TrainMae:F4} Th, RMSE {stats.TrainRmse:F4} Th");
        if (stats.RowsValidation > 0)
            Log.Info($"  val   MAE {stats.ValidationMae:F4} Th, RMSE {stats.ValidationRmse:F4} Th");
        Log.Info($"  delta m/z std {stats.Before.StdDev:F4} -> {stats.After.StdDev:F4} Th " +
                 $"({PercentReduction(stats.Before.StdDev, stats.After.StdDev):F1}% reduction)");
        Log.Info($"  delta m/z MAD {stats.Before.Mad:F4} -> {stats.After.Mad:F4} Th " +
                 $"({PercentReduction(stats.Before.Mad, stats.After.Mad):F1}% reduction)");

        if (dumpPredictionsPath is not null)
        {
            double[] predictions = calibrator.PredictAll(table);
            MatchDumpWriter.Write(dumpPredictionsPath, table, library, predictions);
            Log.Info($"Wrote {table.Count:N0} predictions to {dumpPredictionsPath}");
        }

        MarsModelIo.Save(calibrator, modelPath);
        Log.Info($"Saved model to {modelPath}");

        QcReport.Write(reportPath, calibrator, matcher.Statistics, mzmlFiles, matchOptions);
        Log.Info($"Wrote QC report to {reportPath}");

        if (!noHtmlReport)
        {
            QcHtmlReport.Write(
                htmlReportPath,
                BuildReportData(table, calibrator),
                stats,
                matcher.Statistics,
                mzmlFiles,
                DescribeTolerance(matchOptions),
                MarsInfo.Version);
            Log.Info($"Wrote QC figures to {htmlReportPath}");
        }

        // ---- Pass 2: write corrected files -------------------------------------------
        if (!noRecalibrate)
        {
            foreach (string file in mzmlFiles)
            {
                string outputFile = Path.Combine(outputDirectory,
                    Path.GetFileNameWithoutExtension(file) + "-mars.mzML");
                temperatureByFile.TryGetValue(file, out TemperatureSet? temperatures);

                Log.Info($"Writing: {Path.GetFileName(outputFile)}");
                MzMLWriteResult result = MzMLWriter.Write(
                    infoByFile[file],
                    outputFile,
                    () => new CalibratingTransform(calibrator, correctionOptions, temperatures),
                    new MzMLWriteOptions { MaxDegreeOfParallelism = args.Int("threads") ?? -1 },
                    Log.Warn);

                Log.Info($"  {result.SpectraCorrected:N0} of {result.SpectraSeen:N0} spectra corrected, " +
                         $"{result.OutputLength:N0} bytes");
                if (result.MonotonicityFixes > 0)
                {
                    Log.Warn($"  {result.MonotonicityFixes:N0} peaks would have broken ascending m/z order " +
                             $"and were adjusted ({correctionOptions.Monotonicity})");
                }
            }
        }

        Log.Info($"Done in {stopwatch.Elapsed.TotalSeconds:F1} s. Output directory: {outputDirectory}");
        return Program.ExitSuccess;
    }

    private static SpectralLibrary LoadLibrary(
        CommandLineArgs args, List<string> runNames, bool keepSequences)
    {
        string? prismCsv = args.String("prism-csv");
        string? libraryPath = args.String("library");

        var options = new PrismLibraryOptions
        {
            RunNames = runNames,
            DedupeFragments = !args.Flag("no-dedupe-library"),
            // Sequences are dropped by default because a plate-scale report carries tens
            // of millions of them. A dump is a diagnostic run, so the memory is worth the
            // peptide identity in the output.
            KeepSequences = keepSequences,
        };

        if (prismCsv is not null)
        {
            Log.Info($"Loading PRISM library: {prismCsv}");
            return PrismCsvLibraryReader.Load(prismCsv, options, Log.Info);
        }

        if (libraryPath is null)
            throw new FileNotFoundException("A library is required: --prism-csv or --library.");

        if (libraryPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Loading PRISM library: {libraryPath}");
            return PrismCsvLibraryReader.Load(libraryPath, options, Log.Info);
        }

        if (libraryPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Loading DIA-NN library: {libraryPath}");
            return DiannParquetLibraryReader.Load(libraryPath, args.String("diann-report"), runNames, Log.Info);
        }

        if (libraryPath.EndsWith(".blib", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Loading BiblioSpec library: {libraryPath}");
            return BlibLibraryReader.Load(libraryPath, args.Double("rt-window") ?? 0.083, Log.Info);
        }

        throw new InvalidDataException(
            $"Unrecognized library type '{Path.GetExtension(libraryPath)}'. Expected .blib, .parquet or .csv.");
    }

    /// <summary>
    /// Checks whether the run reports an ion injection time, by looking at its first MS2
    /// spectrum. Fourteen of the twenty-two features are undefined without one.
    /// </summary>
    private static bool ProbeInjectionTime(string path, MzMLFileInfo info)
    {
        foreach (SpectrumRecord spectrum in MzMLFile.ReadSpectra(info, msLevel: 2))
            return spectrum.InjectionTime.HasValue;
        return false;
    }

    private static MonotonicityPolicy ParseMonotonicity(string? value) => value?.ToLowerInvariant() switch
    {
        null or "clamp" => MonotonicityPolicy.ClampAscending,
        "revert" => MonotonicityPolicy.RevertSpectrum,
        "allow" => MonotonicityPolicy.Allow,
        _ => throw new FormatException($"--on-reorder expects clamp, revert or allow, got '{value}'."),
    };

    internal static double PercentReduction(double before, double after) =>
        before > 0 ? (before - after) / before * 100.0 : 0.0;

    /// <summary>
    /// Collects the per-row values the figures are drawn from.
    /// </summary>
    /// <remarks>
    /// The arrays are handed over rather than copied: the match table's backing store is
    /// already column-major in exactly the layout the charts want, and a cohort can carry
    /// millions of rows. <see cref="GrowableArray{T}.Items"/> can be longer than the row
    /// count, so every span is bounded by <c>table.Count</c>.
    /// </remarks>
    private static QcHtmlReport.Data BuildReportData(MatchTable table, MzCalibrator calibrator)
    {
        int rows = table.Count;
        double[] before = table.DeltaMz.Items[..rows];

        double[] predictions = calibrator.PredictAll(table);
        var after = new double[rows];
        for (int i = 0; i < rows; i++) after[i] = before[i] - predictions[i];

        var features = new List<(string Name, double[] Values)>(calibrator.Features.Count);
        foreach (MarsFeature feature in calibrator.Features.Features)
            features.Add((MarsFeatures.NameOf(feature), table.Column(feature).Items[..rows]));

        var importanceNames = new List<string>(calibrator.Features.Count);
        foreach (MarsFeature feature in calibrator.Features.Features)
            importanceNames.Add(MarsFeatures.NameOf(feature));

        return new QcHtmlReport.Data
        {
            ErrorBefore = before,
            ErrorAfter = after,
            RetentionTime = table.RetentionTime is null ? Array.Empty<double>() : table.RetentionTime.Items[..rows],
            FragmentMz = table.Has(MarsFeature.FragmentMz)
                ? table.Column(MarsFeature.FragmentMz).Items[..rows]
                : Array.Empty<double>(),
            Features = features,
            ImportanceNames = importanceNames,
            Importance = calibrator.Statistics?.PermutationImportance ?? Array.Empty<double>(),
        };
    }

    private static string DescribeTolerance(MatchOptions options) =>
        options.TolerancePpm > 0
            ? $"{options.TolerancePpm:0.##} ppm"
            : $"{options.MzToleranceTh:0.###} Th";

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
            Usage: mars calibrate [options] [<file.mzML> ...]

            Learns an m/z calibration from spectral library matches and writes recalibrated
            mzML files named {input}-mars.mzML.

            Input:
                  --mzml <path>          mzML file or glob (repeatable)
                  --mzml-dir <dir>       Directory of mzML files
                  --prism-csv <path>     Skyline PRISM report CSV (theoretical Product Mz)
                  --library <path>       .blib, DIA-NN report-lib.parquet, or PRISM .csv
                  --diann-report <path>  DIA-NN report.parquet, for per-run RT windows
                  --temperature-dir <d>  Directory of RFA2-/RFC2- temperature CSVs

            Matching:
                  --tolerance <Th>       Fragment tolerance in Th (default 0.3)
                  --tolerance-ppm <ppm>  Fragment tolerance in ppm; overrides --tolerance
                  --min-intensity <n>    Minimum peak intensity to match (default 500)
                  --max-isolation-window <Th>
                                         Skip spectra with wider isolation windows
                  --rt-window <min>      RT half-window for blib entries (default 0.083)
                  --no-dedupe-library    Keep transitions repeated across replicates

            Model:
                  --n-estimators <n>     Boosting rounds (default 100)
                  --max-depth <n>        Tree depth (default 6)
                  --learning-rate <x>    Shrinkage (default 0.1)
                  --validation-split <x> Held-out fraction (default 0.2)
                  --max-training-rows <n>
                                         Cap training rows by even stride (default no cap)
                  --min-training-rows <n>
                                         Refuse to fit below this many matches (default 1000)
                  --seed <n>             Random seed (default 42)

            Output:
                  --output-dir <dir>     Output directory (default .)
                  --model-path <path>    Where to save the model
                  --report <path>        Where to write the QC summary
                  --html-report <path>   Where to write the QC figures (default
                                         mars_qc_report.html in the output directory).
                                         One self-contained file, safe to email
                  --no-html-report       Skip the figures and write only the text summary
                  --dump-matches <path>  Write every matched fragment to CSV, one row per
                                         match, with all computed features. Diagnostic;
                                         a large cohort produces millions of rows
                  --dump-predictions <path>
                                         As --dump-matches, plus the model's predicted
                                         correction and the residual, written after
                                         training
                  --no-recalibrate       Train and report only; write no mzML
                  --on-reorder <mode>    clamp (default), revert, or allow, when a
                                         correction would break ascending m/z order
                  --python-compat        Reproduce two known inconsistencies in the Python
                                         implementation, for A/B comparison
                  --threads <n>          Worker threads
              -v, --verbose              Verbose output
            """);
    }
}

/// <summary>Adapts the calibrator to the writer's per-worker transform contract.</summary>
internal sealed class CalibratingTransform : IMzTransform
{
    private readonly SpectrumCorrector _corrector;
    private readonly TemperatureSet? _temperatures;
    private readonly CorrectionWorkspace _workspace = new();

    public CalibratingTransform(MzCalibrator calibrator, CorrectionOptions options, TemperatureSet? temperatures)
    {
        _corrector = new SpectrumCorrector(calibrator, options);
        _temperatures = temperatures;
    }

    public MzTransformResult Transform(SpectrumRecord spectrum, Span<double> corrected)
    {
        SpectrumCorrectionResult result = _corrector.Correct(spectrum, _temperatures, _workspace, corrected);
        return new MzTransformResult
        {
            Rewrite = result.Corrected,
            MonotonicityFixes = result.MonotonicityFixes,
            Reverted = result.Reverted,
        };
    }
}
