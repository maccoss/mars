// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using MARS.Core;
using MARS.IO;
using MARS.Pwiz;

namespace MARS.Cli;

/// <summary>
/// Writes one corrected file, in whichever format was asked for.
/// </summary>
/// <remarks>
/// Two writers sit behind this, and which one runs is decided by the format alone.
/// <list type="bullet">
/// <item>
/// <description>
/// <b>mzML</b> goes through MARS's own writer, which splices corrected bytes into a copy of
/// the input. Everything MARS did not change is identical to the input by construction rather
/// than by care - see docs/mzml-passthrough.md for why that matters and what broke without
/// it. This is the default and stays the default.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Everything else</b> goes through pwiz-sharp, because there is no input of that format
/// to splice into: the file has to be built. That also puts the format code with the people
/// who maintain the format.
/// </description>
/// </item>
/// </list>
/// Both paths run the same <see cref="SpectrumCorrector"/> over the same values, which is
/// checked rather than assumed - writing a file both ways and diffing with <c>mars compare</c>
/// finds no difference across 82 million peaks.
/// </remarks>
internal static class CorrectedFileWriter
{
    public static void Write(
        MarsOutputFormat format,
        ISpectrumSource source,
        string outputPath,
        MzCalibrator calibrator,
        CorrectionOptions correctionOptions,
        TemperatureSet? temperatures,
        int threads)
    {
        Log.Info($"Writing: {Path.GetFileName(outputPath)}");

        // Splice only when there is something to splice into. An mzML input can be copied and
        // patched; a vendor file cannot, so its mzML has to be built like any other format.
        if (format == MarsOutputFormat.MzML && source is MzMLSpectrumSource mzml)
        {
            MzMLWriteResult spliced = MzMLWriter.Write(
                mzml.Info,
                outputPath,
                () => new CalibratingTransform(calibrator, correctionOptions, temperatures),
                new MzMLWriteOptions { MaxDegreeOfParallelism = threads },
                Log.Warn);

            Report(spliced.SpectraCorrected, spliced.SpectraSeen, spliced.OutputLength,
                   spliced.MonotonicityFixes, correctionOptions);
            return;
        }

        PwizWriteResult written = PwizOutput.Write(new PwizWriteRequest
        {
            InputPath = source.Path,
            OutputPath = outputPath,
            Format = format,
            Calibrator = calibrator,
            Options = correctionOptions,
            AcquisitionStartTime = source.AcquisitionStartTime,
            Temperatures = temperatures,
            Threads = threads,

            // Match what the input used, when the input is an mzML that can be read for it.
            // pwiz's own default is 64-bit uncompressed, which makes the output substantially
            // larger than the file it came from. A vendor file has no encoding to copy, so it
            // takes the default, which is what msconvert would have written anyway.
            Encoding = SpectrumSources.IsNative(source.Path)
                ? MzMLEncoding.Sniff(source.Path)
                : SpectrumEncoding.Default,
        });

        Report(written.SpectraCorrected, written.SpectraSeen, written.OutputLength,
               written.MonotonicityFixes, correctionOptions);

        // Where the time went, so a slow conversion can be attributed rather than guessed at.
        // Everything not in these two is inside pwiz's encoder.
        Log.Debug($"  reader {written.ReaderTime.TotalSeconds:F1} s, "
                  + $"model {written.CorrectorTime.TotalSeconds:F1} s");

        if (written.SpectraReverted > 0)
        {
            Log.Warn($"  {written.SpectraReverted:N0} spectra were left uncorrected because the "
                     + "correction would have reordered their peaks");
        }
    }

    /// <summary>The output path for one input, in one format.</summary>
    public static string OutputPathFor(string inputFile, string outputDirectory, MarsOutputFormat format) =>
        Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(inputFile) + "-mars" + PwizOutput.Extension(format));

    /// <summary>
    /// Resolves --output-format, failing before any work rather than after.
    /// </summary>
    public static MarsOutputFormat ResolveFormat(CommandLineArgs args)
    {
        string? requested = args.String("output-format");
        if (!PwizOutput.TryParse(requested, out MarsOutputFormat format))
        {
            throw new FormatException(
                $"--output-format expects mzML, mzXML, mzMLb or mgf, got '{requested}'.");
        }

        if (format != MarsOutputFormat.MzML && !PwizOutput.Available)
        {
            throw new NotSupportedException(
                $"This build of MARS cannot write {PwizOutput.Name(format)}: it was built "
                + "without a pwiz-sharp checkout. Rebuild with "
                + "-p:PwizSharpDir=<path>/pwiz/pwiz-sharp, or write mzML.");
        }

        if (PwizOutput.LossWarning(format) is string warning) Log.Warn(warning);
        return format;
    }

    private static void Report(
        long corrected, long seen, long bytes, long monotonicityFixes, CorrectionOptions options)
    {
        Log.Info($"  {corrected:N0} of {seen:N0} spectra corrected, {bytes:N0} bytes");
        if (monotonicityFixes > 0)
        {
            Log.Warn($"  {monotonicityFixes:N0} peaks would have broken ascending m/z order "
                     + $"and were adjusted ({options.Monotonicity})");
        }
    }
}
