// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// The passthrough acceptance gate: a null correction must round-trip a file with
// bit-identical decoded arrays, a valid index and a valid checksum.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MARS.IO;

namespace MARS.Cli;

public static class VerifyCommand
{
    public static int Run(CommandLineArgs args)
    {
        if (args.Flag("help", "h"))
        {
            Console.Error.WriteLine("""
                Usage: mars verify <input.mzML> [options]

                Round-trips a file through the passthrough writer applying a null correction
                (decode and re-encode every m/z array without changing a value), then checks
                that the result is equivalent to the input.

                This isolates the file-format work from the science. Run it before trusting
                any calibrated output.

                Options:
                  -o, --output <path>   Where to write the round-tripped copy
                                        (default: alongside the input, -verify.mzML)
                      --keep            Keep the round-tripped file (default: delete it)
                      --threads N       Worker threads (default: processor count)
                      --check-offsets N Index offsets to spot check (default: all)
                  -v, --verbose         Verbose output
                """);
            return Program.ExitSuccess;
        }

        Log.Verbose = args.Flag("verbose", "v");

        string? inputPath = args.String("input", "i") ?? (args.Positional.Count > 0 ? args.Positional[0] : null);
        if (inputPath is null)
        {
            Log.Error("No input file. Usage: mars verify <input.mzML>");
            return Program.ExitInputError;
        }

        if (!File.Exists(inputPath))
        {
            Log.Error($"File not found: {inputPath}");
            return Program.ExitInputError;
        }

        bool keep = args.Flag("keep");
        string outputPath = args.String("output", "o")
                            ?? Path.Combine(
                                Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".",
                                Path.GetFileNameWithoutExtension(inputPath) + "-verify.mzML");

        // Refuse to write over the input. Verify deletes its output unless --keep, so
        // pointing --output at the input would round-trip the file onto itself and then
        // delete it - losing raw data to a command whose whole purpose is to prove nothing
        // was lost. Compared on full paths so that a relative path and an absolute one to
        // the same file are still caught.
        if (string.Equals(
                Path.GetFullPath(outputPath), Path.GetFullPath(inputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            Log.Error(
                "--output is the same file as the input. mars verify writes a round-tripped " +
                "copy and deletes it unless --keep is given, so this would destroy the input. " +
                "Choose a different --output.");
            return Program.ExitInputError;
        }

        int threads = args.Int("threads") ?? -1;
        int checkOffsets = args.Int("check-offsets") ?? 0;

        var stopwatch = Stopwatch.StartNew();
        MzMLFileInfo info = MzMLFile.Inspect(inputPath);
        Log.Info($"Input: {inputPath}");
        Log.Info($"  {info.Length:N0} bytes, indexed={info.WasIndexed}, indexedmzML={info.IsIndexedMzML}");
        Log.Info($"  acquisition start: {(info.AcquisitionStartTime is double t ? t.ToString("F3", CultureInfo.InvariantCulture) : "not recorded")}");

        MzMLWriteResult write = MzMLWriter.Write(
            info, outputPath, () => new NullMzTransform(),
            new MzMLWriteOptions { MaxDegreeOfParallelism = threads },
            Log.Warn);

        Log.Info($"Wrote {outputPath}");
        Log.Info($"  {write.OutputLength:N0} bytes, {write.SpectraSeen:N0} spectra, " +
                 $"{write.SpectraCorrected:N0} re-encoded, {write.ChromatogramsCopied:N0} chromatograms");
        Log.Info($"  elapsed {stopwatch.Elapsed.TotalSeconds:F1} s");

        var failures = 0;

        IndexValidationResult validation = MzMLValidator.Validate(outputPath, checkOffsets);
        if (!validation.IsIndexed)
        {
            Log.Warn("Output has no index.");
        }
        else
        {
            Log.Info($"Index: {validation.SpectrumOffsets:N0} spectrum offsets, " +
                     $"{validation.ChromatogramOffsets:N0} chromatogram offsets");
            if (validation.BadOffsets.Count > 0)
            {
                failures++;
                Log.Error($"{validation.BadOffsets.Count} index offsets do not land on their element:");
                foreach (string bad in validation.BadOffsets) Log.Error("  " + bad);
            }
            else
            {
                Log.Info("Index offsets: all land on the element they name");
            }

            if (validation.ChecksumPresent)
            {
                if (validation.ChecksumValid)
                {
                    Log.Info($"SHA-1 fileChecksum: valid ({validation.RecordedChecksum})");
                }
                else
                {
                    failures++;
                    Log.Error($"SHA-1 fileChecksum invalid: recorded {validation.RecordedChecksum}, " +
                              $"computed {validation.ComputedChecksum}");
                }
            }
        }

        Log.Info("Comparing decoded arrays against the input...");
        MzMLComparison comparison = MzMLComparer.Compare(inputPath, outputPath);
        Log.Info($"  {comparison.SpectraCompared:N0} spectra, {comparison.MzValuesCompared:N0} peaks compared");

        if (comparison.MzBitIdentical)
        {
            Log.Info("  m/z arrays: bit-identical");
        }
        else
        {
            failures++;
            Log.Error($"  m/z arrays differ in {comparison.MzValuesDiffering:N0} values " +
                      $"(max |delta| {comparison.MaxAbsoluteMzDifference:R})");
        }

        if (comparison.IntensityBitIdentical)
        {
            Log.Info("  intensity arrays: bit-identical");
        }
        else
        {
            failures++;
            Log.Error($"  intensity arrays differ in {comparison.IntensityValuesDiffering:N0} values");
        }

        if (comparison.SpectraOnlyInA != 0 || comparison.SpectraOnlyInB != 0)
        {
            failures++;
            Log.Error($"  spectrum count mismatch: {comparison.SpectraOnlyInA} only in input, " +
                      $"{comparison.SpectraOnlyInB} only in output");
        }

        foreach (string problem in comparison.Problems) Log.Error("  " + problem);

        if (!keep)
        {
            try
            {
                File.Delete(outputPath);
                Log.Debug($"Deleted {outputPath}");
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not delete {outputPath}: {ex.Message}");
            }
        }

        if (failures > 0)
        {
            Log.Error($"PASSTHROUGH VERIFICATION FAILED ({failures} checks)");
            return Program.ExitOutputValidationFailure;
        }

        Console.Out.WriteLine("passthrough verification passed");
        Log.Info($"Total elapsed {stopwatch.Elapsed.TotalSeconds:F1} s");
        return Program.ExitSuccess;
    }
}
