// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Compares two mzML files on decoded values, for cross-checking the port against the
// Python implementation's output.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MARS.Core;
using MARS.IO;

namespace MARS.Cli;

public static class CompareCommand
{
    public static int Run(CommandLineArgs args)
    {
        if (args.Flag("help", "h"))
        {
            Console.Error.WriteLine("""
                Usage: mars compare <a.mzML> <b.mzML> [options]

                Compares two mzML files on DECODED m/z and intensity values, and reports how
                the m/z arrays differ. Byte comparison would be meaningless: two zlib
                implementations produce different compressed bytes for identical data.

                Options:
                      --validate      Also check each file's index and checksum
                      --max-report N  Detail lines to print (default 10)
                  -v, --verbose       Verbose output
                """);
            return Program.ExitSuccess;
        }

        Log.Verbose = args.Flag("verbose", "v");

        if (args.Positional.Count < 2)
        {
            Log.Error("Two mzML files are required. Usage: mars compare <a.mzML> <b.mzML>");
            return Program.ExitInputError;
        }

        string pathA = args.Positional[0];
        string pathB = args.Positional[1];
        foreach (string path in new[] { pathA, pathB })
        {
            if (File.Exists(path)) continue;
            Log.Error($"File not found: {path}");
            return Program.ExitInputError;
        }

        bool validate = args.Flag("validate");
        int maxReport = args.Int("max-report") ?? 10;

        // Every option this command reads has been read by now, so a typo can be named rather
        // than silently ignored. RejectUnknown only knows an option is real because something
        // asked for it.
        args.RejectUnknown();

        var stopwatch = Stopwatch.StartNew();

        if (validate)
        {
            foreach (string path in new[] { pathA, pathB })
            {
                IndexValidationResult validation = MzMLValidator.Validate(path);
                Log.Info($"{Path.GetFileName(path)}: index {(validation.BadOffsets.Count == 0 ? "valid" : "INVALID")}" +
                         $", checksum {(validation.ChecksumPresent ? validation.ChecksumValid ? "valid" : "INVALID" : "absent")}" +
                         $", {validation.SpectrumOffsets:N0} spectrum offsets");
            }
        }

        Log.Info($"Comparing {Path.GetFileName(pathA)} against {Path.GetFileName(pathB)}...");
        MzMLComparison comparison = MzMLComparer.Compare(pathA, pathB, maxReport);

        Console.Out.WriteLine($"spectra compared      {comparison.SpectraCompared:N0}");
        Console.Out.WriteLine($"peaks compared        {comparison.MzValuesCompared:N0}");
        Console.Out.WriteLine($"m/z values differing  {comparison.MzValuesDiffering:N0}");
        Console.Out.WriteLine($"max |delta m/z|       {comparison.MaxAbsoluteMzDifference:R} Th");
        Console.Out.WriteLine($"intensity differing   {comparison.IntensityValuesDiffering:N0}");

        if (comparison.SpectraOnlyInA != 0 || comparison.SpectraOnlyInB != 0)
        {
            Console.Out.WriteLine($"spectra only in A     {comparison.SpectraOnlyInA:N0}");
            Console.Out.WriteLine($"spectra only in B     {comparison.SpectraOnlyInB:N0}");
        }

        if (comparison.Diverged)
        {
            Console.Out.WriteLine(
                "comparison stopped     the files stopped holding the same spectra in the same "
                + "order; the counts above cover only what came before that point");
        }

        foreach (string problem in comparison.Problems) Log.Info("  " + problem);

        Log.Info($"Compared in {stopwatch.Elapsed.TotalSeconds:F1} s");
        return Program.ExitSuccess;
    }
}
