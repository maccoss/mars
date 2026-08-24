// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the calibrate command in mars/cli.py.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MARS.Core;
using MARS.IO;
using MARS.Pwiz;
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

        // Resolved once and used for both the training histograms and the write, so the run
        // reports a single number rather than settling on one per stage.
        int threads = ThreadCount.Resolve(args, Log.Info, Log.Warn);

        var matchOptions = new MatchOptions
        {
            MzToleranceTh = args.Double("tolerance") ?? ResolutionMode.DefaultToleranceTh,
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
            CvFolds = args.Int("cv-folds") ?? 5,
            Robust = ParseRobust(args.String("robust")),
            RobustSigma = args.Double("robust-sigma") ?? 3.0,
            MaxTrainingRows = args.Int("max-training-rows") ?? 0,
            MaxDegreeOfParallelism = threads,
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



        // Resolved before any work: an output format this build cannot write, or one that
        // does not exist, should cost a second rather than a full training run.
        MarsOutputFormat outputFormat = CorrectedFileWriter.ResolveFormat(args);

        // Read before the check below rather than inside LoadLibrary, so that every option
        // this command understands has been seen by the time the check runs.
        var librarySource = LibrarySource.From(args);

        // Everything is read; refuse a typo now rather than after minutes of work, or worse,
        // after writing corrected files from a run that silently used a default.
        // Read here, used further down once the readers are open and can say what analyzer
        // they saw. RejectUnknown only knows an option is real because something asked for it,
        // so an option resolved later has to be touched before the check or it is reported as
        // a typo.
        ResolutionMode.Touch(args);

        args.RejectUnknown();

        Log.Info($"Found {mzmlFiles.Count} mzML file(s) to process");
        var stopwatch = Stopwatch.StartNew();

        // ---- Library ----------------------------------------------------------------
        var runNames = new List<string>();
        foreach (string file in mzmlFiles) runNames.Add(Path.GetFileName(file));

        SpectralLibrary library = librarySource.Load(runNames, keepSequences: keepDetail, Log.Info);

        // ---- Pass 1: match fragments across every input file -------------------------
        var temperatureByFile = new Dictionary<string, TemperatureSet>(StringComparer.OrdinalIgnoreCase);

        // Opened once and held: a vendor reader keeps a handle on the file, and reopening it
        // per pass would pay the SDK's startup cost twice.
        var sourceByFile = new Dictionary<string, ISpectrumSource>(StringComparer.OrdinalIgnoreCase);

        bool anyTemperature = false;
        bool anyRfa2 = false, anyRfc2 = false;
        foreach (string file in mzmlFiles)
        {
            sourceByFile[file] = SpectrumSources.Open(file);

            if (temperatureDirectory is not null)
            {
                TemperatureSet temperatures = TemperatureCsvReader.Find(file, temperatureDirectory, Log.Info);
                temperatureByFile[file] = temperatures;
                anyTemperature |= !temperatures.IsEmpty;
                anyRfa2 |= temperatures.Rfa2 is not null;
                anyRfc2 |= temperatures.Rfc2 is not null;
            }
        }

        // Probed on every file, not just the first. One run in a cohort can record a varying
        // injection time where another does not, and the feature group is worth having if any
        // of them carries the information - a run without it contributes a constant column
        // for its own rows, which costs nothing.
        InjectionTimeUse injectionTimeUse = InjectionTimeUse.Absent;
        foreach (string file in mzmlFiles)
        {
            InjectionTimeUse use = ProbeInjectionTime(sourceByFile[file]);

            // One run recording a varying time settles it; there is nothing a later file can
            // say that would turn the feature group back off.
            if (use == InjectionTimeUse.Varying)
            {
                injectionTimeUse = use;
                break;
            }

            if (injectionTimeUse == InjectionTimeUse.Absent) injectionTimeUse = use;
        }

        ReportInjectionTime(injectionTimeUse);

        // Decided once the readers are open, from what they say their MS2 analyzer is. The
        // readers know their own formats; asking the file again from here would mean parsing
        // a .raw as if it were mzML, which is how this used to fall back to a trap tolerance
        // on Astral data without anyone noticing.
        MassAnalyzerClass analyzer = sourceByFile[mzmlFiles[0]].Analyzer;

        if (FirstAnalyzerDisagreement(mzmlFiles, f => sourceByFile[f].Analyzer, analyzer) is string odd)
        {
            Log.Warn(
                $"{Path.GetFileName(odd)} was recorded on a {Describe(sourceByFile[odd].Analyzer)} " +
                $"analyzer, but the fragment tolerance is being set from a {Describe(analyzer)} one. " +
                "One tolerance is used for the whole cohort; calibrate the instruments separately, " +
                "or set --resolution to choose deliberately.");
        }

        ResolutionMode resolution = ResolutionMode.Resolve(args, analyzer, matchOptions, Log.Info);

        MarsFeature[] collect = FragmentMatcher.CollectedFeatures(injectionTimeUse, anyRfa2, anyRfc2);
        var table = new MatchTable(collect, keepDetail: keepDetail);
        var matcher = new FragmentMatcher(library, matchOptions);

        foreach (string file in mzmlFiles)
        {
            Log.Info($"Matching: {Path.GetFileName(file)}");
            ISpectrumSource source = sourceByFile[file];
            temperatureByFile.TryGetValue(file, out TemperatureSet? temperatures);

            long before = table.Count;
            long spectra = 0;
            foreach (SpectrumRecord spectrum in source.ReadSpectra(msLevel: 2))
            {
                matcher.MatchSpectrum(spectrum, temperatures, table);
                spectra++;
            }

            Log.Info($"  {spectra:N0} MS2 spectra, {table.Count - before:N0} fragment matches");
        }

        Log.Info($"Total matches: {table.Count:N0} from {matcher.Statistics.SpectraSeen:N0} spectra");
        CheckTolerance(table, matchOptions);
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
                MarsInfo.Version,
                uncorrected: null,
                resolution.ReportInPpm ? ErrorScale.Ppm : ErrorScale.Th);
            Log.Info($"Wrote QC figures to {htmlReportPath}");
        }

        // ---- Pass 2: write corrected files -------------------------------------------
        if (!noRecalibrate)
        {
            foreach (string file in mzmlFiles)
            {
                temperatureByFile.TryGetValue(file, out TemperatureSet? temperatures);
                CorrectedFileWriter.Write(
                    outputFormat,
                    sourceByFile[file],
                    CorrectedFileWriter.OutputPathFor(file, outputDirectory, outputFormat),
                    calibrator,
                    correctionOptions,
                    temperatures,
                    threads);
            }
        }

        foreach (ISpectrumSource source in sourceByFile.Values) source.Dispose();

        Log.Info($"Done in {stopwatch.Elapsed.TotalSeconds:F1} s. Output directory: {outputDirectory}");
        return Program.ExitSuccess;
    }


    /// <summary>
    /// Checks the matched error against the window it was matched in, once there is data to
    /// check with. Detection can come up empty - a vendor model pwiz does not recognise leaves
    /// no analyzer term at all - and this catches the consequence rather than the cause.
    /// </summary>
    internal static void CheckTolerance(MatchTable table, MatchOptions options)
    {
        if (table.Count == 0) return;

        ReadOnlySpan<double> delta = table.DeltaMz.Items.AsSpan(0, table.Count);
        double mad = MarsStatistics.Summarize(delta).Mad;

        double[] fragmentMz = table.Column(MarsFeature.FragmentMz).Items;
        var sample = new double[table.Count];
        Array.Copy(fragmentMz, sample, table.Count);
        Array.Sort(sample);
        double median = sample[sample.Length / 2];

        ResolutionMode.WarnIfToleranceLooksTooWide(options, mad, median, Log.Warn);
    }
    /// <summary>
    /// Whether ion injection time is worth using as a feature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presence is not enough - it also has to vary. A trap sets injection time per spectrum
    /// from its automatic gain control, so it carries real information about how full the trap
    /// was. A Bruker or Sciex TOF accumulates for a fixed time, so the value is the same on
    /// every spectrum: <c>injection_time</c> is then a constant, which a tree can never split
    /// on, and <c>tic_injection_time</c> is TIC times that constant, which is
    /// <c>log_tic</c> rescaled. Two features carrying nothing, one of them a duplicate that
    /// splits permutation importance with the feature it duplicates.
    /// </para>
    /// <para>
    /// Sampled over the head of the run, which is enough to answer whether the run records an
    /// injection time at all - a format that carries none carries none anywhere. It is not
    /// enough to answer whether the value varies, and is no longer used for that: see
    /// <see cref="ReportInjectionTime"/>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The first file in a cohort not recorded on the same kind of analyzer as the rest, or
    /// null when they agree.
    /// </summary>
    /// <remarks>
    /// One tolerance is chosen for the whole cohort, so a folder holding both trap and
    /// high-resolution runs gets one of them matched at the wrong width. That is the quiet
    /// failure: matching Astral data at a trap tolerance opens a window hundreds of ppm wide,
    /// fills it with wrong assignments, and reports a full model trained on them. It warrants
    /// a warning rather than a refusal, because --resolution can be set deliberately and a
    /// mixed cohort is the user's call to make.
    ///
    /// A file whose analyzer could not be read is not a disagreement. It says nothing, and
    /// nothing is not a contradiction - it already falls back to the default tolerance.
    /// </remarks>
    internal static string? FirstAnalyzerDisagreement(
        IReadOnlyList<string> files,
        Func<string, MassAnalyzerClass> analyzerOf,
        MassAnalyzerClass chosen)
    {
        if (chosen == MassAnalyzerClass.Unknown) return null;

        foreach (string file in files)
        {
            MassAnalyzerClass other = analyzerOf(file);
            if (other != chosen && other != MassAnalyzerClass.Unknown) return file;
        }

        return null;
    }

    private static string Describe(MassAnalyzerClass analyzer) => analyzer switch
    {
        MassAnalyzerClass.HighResolution => "high-resolution",
        MassAnalyzerClass.UnitResolution => "unit-resolution",
        _ => "unrecognized",
    };

    internal static InjectionTimeUse ProbeInjectionTime(ISpectrumSource source)
    {
        const int sample = 500;

        // Loose enough to absorb float representation, orders of magnitude tighter than any
        // real gain control. A trap's injection times differ by whole milliseconds.
        const double constantWithin = 1e-6;

        int seen = 0;
        int withValue = 0;
        double low = double.MaxValue;
        double high = double.MinValue;

        foreach (SpectrumRecord spectrum in source.ReadSpectra(msLevel: 2))
        {
            if (spectrum.InjectionTime is double injection)
            {
                withValue++;
                low = Math.Min(low, injection);
                high = Math.Max(high, injection);
            }

            if (++seen >= sample) break;
        }

        if (withValue == 0) return InjectionTimeUse.Absent;

        double scale = Math.Abs(high) > 0 ? Math.Abs(high) : 1.0;
        return (high - low) / scale > constantWithin
            ? InjectionTimeUse.Varying
            : InjectionTimeUse.Constant;
    }

    /// <summary>
    /// Reports what the probe found. Only the absent case is acted on here.
    /// </summary>
    /// <remarks>
    /// Whether the injection time <em>varies</em> is not decided from this. The probe reads the
    /// head of the run, and an ion trap holds its injection time at the method's ceiling until
    /// the trap fills - which on a gradient is the whole void volume, tens of thousands of
    /// spectra. Every Stellar run tested reads as constant over its first few hundred MS2 and
    /// varies later, one of them across two thirds of its spectra. That call belongs where the
    /// whole column is available, which is
    /// <see cref="MzCalibrator"/>'s feature selection, and it is made there.
    /// </remarks>
    internal static InjectionTimeUse ReportInjectionTime(InjectionTimeUse use)
    {
        if (use == InjectionTimeUse.Absent)
        {
            Log.Warn("No ion injection time in this run; the ion-population features are off. "
                     + "They count the ions in a window, and without an injection time there "
                     + "is nothing to turn a rate into a count with.");
        }

        return use;
    }

    private static RobustFit ParseRobust(string? value) => value?.ToLowerInvariant() switch
    {
        null or "trim" => RobustFit.Trim,
        "huber" => RobustFit.Huber,
        "none" => RobustFit.None,
        _ => throw new FormatException($"--robust expects huber, trim or none, got '{value}'."),
    };

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
            CrossValidation = calibrator.CrossValidation,
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
                  --prism-report <path>  Skyline PRISM report, .csv or .parquet
                                         (theoretical Product Mz). The report Skyline
                                         exports for PRISM to read, not anything PRISM
                                         writes. --prism-csv also accepted
                  --library <path>       .blib, DIA-NN report-lib.parquet, or a Skyline
                                         PRISM report
                  --diann-report <path>  DIA-NN report.parquet, for per-run RT windows
                  --temperature-dir <d>  Directory of RFA2-/RFC2- temperature CSVs

            Matching:
                  --output-format <fmt>  mzML (default), mzXML, mzMLb or mgf. mzML is
                                         written by splicing the input; the rest are
                                         built through pwiz
                  --resolution <mode>    unit, hram or auto (default auto: read the mass
                                         analyzer from the mzML and pick the tolerance
                                         and the QC report's units to match)
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
                  --robust <mode>        Second pass over rows the first could not explain,
                                         usually mismatched peaks whose delta is not a mass
                                         error at all: trim (default) drops them, huber
                                         holds them down in proportion to how implausible
                                         they are, none fits once
                  --robust-sigma <x>     Residual threshold for --robust, in robust sigma
                                         (default 3). 0 disables the second pass
                  --cv-folds <n>         Cross-validation folds, split by peptide
                                         (default 5). Does not change what gets applied:
                                         the correction model is fitted to all rows either
                                         way. The folds estimate what the same correction
                                         would achieve on data it was not fitted to, which
                                         is reported alongside. 0 skips them
                  --validation-split <x> Held-out fraction for --cv-folds 0 (default 0.2)
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
                  --threads <n|auto>     Worker threads (default: auto, one per
                                         logical processor)
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
