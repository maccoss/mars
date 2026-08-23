// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from the apply command in mars/cli.py.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MARS.Core;
using MARS.IO;
using MARS.Pwiz;

namespace MARS.Cli;

public static class ApplyCommand
{
    public static int Run(CommandLineArgs args)
    {
        if (args.Flag("help", "h"))
        {
            Console.Error.WriteLine("""
                Usage: mars apply --model <model.json> [options] [<file.mzML> ...]

                Applies a previously trained model to more files, without rematching or
                retraining.

                Options:
                      --model <path>         Trained model (required)
                      --mzml <path>          mzML file or glob (repeatable)
                      --mzml-dir <dir>       Directory of mzML files
                      --output-dir <dir>     Output directory (default .)
                      --temperature-dir <d>  Directory of RFA2-/RFC2- temperature CSVs
                      --max-isolation-window <Th>
                                             Leave wider isolation windows uncorrected
                      --on-reorder <mode>    clamp (default), revert, or allow
                      --python-compat        Reproduce the Python inconsistencies
                      --threads <n|auto>     Worker threads (default: auto, one per
                                             logical processor)
                      --output-format <fmt>  mzML (default), mzXML, mzMLb or mgf
                      --validate             Check the index and checksum of each output
                  -v, --verbose              Verbose output
                """);
            return Program.ExitSuccess;
        }

        Log.Verbose = args.Flag("verbose", "v");

        string? modelPath = args.String("model");
        if (modelPath is null)
        {
            Log.Error("--model is required.");
            return Program.ExitInputError;
        }

        if (!File.Exists(modelPath))
        {
            Log.Error($"Model not found: {modelPath}");
            return Program.ExitInputError;
        }

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

        var correctionOptions = new CorrectionOptions
        {
            MaxIsolationWindowWidth = args.Double("max-isolation-window"),
            PythonCompatibility = args.Flag("python-compat"),
            Monotonicity = args.String("on-reorder")?.ToLowerInvariant() switch
            {
                null or "clamp" => MonotonicityPolicy.ClampAscending,
                "revert" => MonotonicityPolicy.RevertSpectrum,
                "allow" => MonotonicityPolicy.Allow,
                var other => throw new FormatException($"--on-reorder expects clamp, revert or allow, got '{other}'."),
            },
        };

        string? temperatureDirectory = args.String("temperature-dir");
        bool validate = args.Flag("validate");
        int threads = ThreadCount.Resolve(args, Log.Info, Log.Warn);

        // Resolved before any file is opened, so an unwritable format fails immediately.
        MarsOutputFormat outputFormat = CorrectedFileWriter.ResolveFormat(args);

        // Every option this command reads has been read by now, so a typo can be named rather
        // than silently ignored. RejectUnknown only knows an option is real because something
        // asked for it.
        args.RejectUnknown();

        MzCalibrator calibrator = MarsModelIo.Load(modelPath);
        Log.Info($"Loaded model from {modelPath}");
        Log.Info($"  {calibrator.Features.Count} features: {string.Join(", ", calibrator.Features.Names())}");
        Log.Info($"  acquisition time offset: {calibrator.AbsoluteTimeOffset:F1} s");

        // A model trained with the RF temperature features expects them at correction time.
        // Absent, they are substituted the way training substitutes a missing one - the
        // boosting implementation maps a non-finite feature to 0.0 before binning - so the
        // run still completes and still corrects. It just does it with two features pinned
        // to a value the model saw for no real spectrum, and nothing about the output says
        // so, which is why it is worth a line here.
        bool modelWantsTemperature =
            calibrator.Features.Contains(MarsFeature.Rfa2Temp) ||
            calibrator.Features.Contains(MarsFeature.Rfc2Temp);

        if (modelWantsTemperature && temperatureDirectory is null)
        {
            Log.Warn(
                "This model was trained with the RF temperature features, but no " +
                "--temperature-dir was given. They will be treated as missing for every " +
                "spectrum. Pass the directory holding the RFA2-/RFC2- CSVs to use them.");
        }

        var stopwatch = Stopwatch.StartNew();
        var failures = 0;

        foreach (string file in mzmlFiles)
        {
            using ISpectrumSource source = SpectrumSources.Open(file);
            TemperatureSet? temperatures = temperatureDirectory is null
                ? null
                : TemperatureCsvReader.Find(file, temperatureDirectory, Log.Debug);

            if (modelWantsTemperature && temperatureDirectory is not null && temperatures is null)
            {
                Log.Warn(
                    $"No temperature CSV matched {Path.GetFileName(file)}; its RF temperature " +
                    "features will be treated as missing.");
            }

            string outputFile = CorrectedFileWriter.OutputPathFor(file, outputDirectory, outputFormat);

            Log.Info($"Calibrating: {Path.GetFileName(file)} -> {Path.GetFileName(outputFile)}");
            CorrectedFileWriter.Write(
                outputFormat, source, outputFile, calibrator, correctionOptions, temperatures, threads);

            if (!validate) continue;

            // The validator checks an mzML index and its SHA-1 footer, neither of which the
            // other formats have. Saying so beats silently reporting nothing.
            if (outputFormat != MarsOutputFormat.MzML)
            {
                Log.Info($"  --validate checks the mzML index and checksum; skipped for "
                         + $"{PwizOutput.Name(outputFormat)}");
                continue;
            }

            IndexValidationResult validation = MzMLValidator.Validate(outputFile);
            if (validation.IsValid)
            {
                Log.Info($"  index and checksum valid ({validation.SpectrumOffsets:N0} spectrum offsets)");
            }
            else
            {
                failures++;
                Log.Error($"  output validation FAILED for {Path.GetFileName(outputFile)}");
                foreach (string bad in validation.BadOffsets) Log.Error("    " + bad);
                if (validation.ChecksumPresent && !validation.ChecksumValid)
                    Log.Error($"    checksum {validation.RecordedChecksum} != {validation.ComputedChecksum}");
            }
        }

        Log.Info($"Done in {stopwatch.Elapsed.TotalSeconds:F1} s. Output directory: {outputDirectory}");
        return failures > 0 ? Program.ExitOutputValidationFailure : Program.ExitSuccess;
    }
}
