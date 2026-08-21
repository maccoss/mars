// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Cli;
using Xunit;

namespace MARS.Test;

public class CommandLineArgsTest
{
    [Fact]
    public void AnOptionNoCommandAsksAboutIsUnknown()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--tolernace-ppm", "10" });
        args.Double("tolerance-ppm");

        var ex = Assert.Throws<UnknownOptionException>(() => args.RejectUnknown());
        Assert.Contains("--tolernace-ppm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANearMissSuggestsTheOptionThatWasMeant()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--tolernace-ppm", "10" });
        args.Double("tolerance-ppm");
        args.Double("tolerance");

        var ex = Assert.Throws<UnknownOptionException>(() => args.RejectUnknown());
        Assert.Contains("Did you mean --tolerance-ppm?", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingUnrelatedSuggestsNothing()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--wombat" });
        args.Double("tolerance-ppm");
        args.Int("threads");

        var ex = Assert.Throws<UnknownOptionException>(() => args.RejectUnknown());
        Assert.Contains("--wombat", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An option is recognized by being asked about, whether or not it was supplied - so
    /// reading an absent option still teaches the parser that the name is valid.
    /// </summary>
    [Fact]
    public void AskingAboutAnAbsentOptionStillRecognizesTheName()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "qc", "--threads", "4" });
        args.Int("threads");
        args.Double("tolerance-ppm");   // absent, but now a name the command understands

        args.RejectUnknown();

        CommandLineArgs supplied = CommandLineArgs.Parse(new[] { "qc", "--tolerance-ppm", "10" });
        supplied.Int("threads");
        supplied.Double("tolerance-ppm");
        supplied.RejectUnknown();
    }

    [Fact]
    public void AliasesAreAllRecognizedNotJustTheOneThatMatched()
    {
        CommandLineArgs args = CommandLineArgs.Parse(new[] { "verify", "-i", "a.mzML" });
        args.String("input", "i");
        args.RejectUnknown();
    }

    /// <summary>
    /// The check is only correct where it sits: an option a command reads after the check has
    /// not been queried yet, so a misplaced call would reject a perfectly valid option. This
    /// runs each command with every option its own help text documents and asserts none of
    /// them is rejected.
    /// </summary>
    /// <remarks>
    /// The commands fail afterwards on missing inputs, which is fine and is the point - the
    /// assertion is about which exception comes out, not about getting a successful run.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DocumentedOptions))]
    public void EveryDocumentedOptionSurvivesTheUnknownOptionCheck(string command, string[] options)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-opts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mzml = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(mzml, spectrumCount: 4, chromatogramCount: 0);

            // verify takes a file as --input; the rest take --mzml. Passing both would hand
            // each command an option it rightly does not know.
            var argv = command == "verify"
                ? new List<string> { command, "--input", mzml }
                : new List<string> { command, "--mzml", mzml };
            argv.AddRange(options);

            Exception? thrown = Record.Exception(() => Run(command, argv.ToArray()));

            Assert.False(
                thrown is UnknownOptionException,
                $"mars {command} rejected one of its own documented options: {thrown?.Message}");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void Run(string command, string[] argv)
    {
        CommandLineArgs args = CommandLineArgs.Parse(argv);
        switch (command)
        {
            case "qc": QcCommand.Run(args); break;
            case "calibrate": CalibrateCommand.Run(args); break;
            case "apply": ApplyCommand.Run(args); break;
            case "verify": VerifyCommand.Run(args); break;
            case "compare": CompareCommand.Run(args); break;
            default: throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown command.");
        }
    }

    public static TheoryData<string, string[]> DocumentedOptions()
    {
        var data = new TheoryData<string, string[]>();

        data.Add("qc", new[]
        {
            "--mzml-dir", ".", "--prism-csv", "lib.csv", "--library", "lib.blib",
            "--diann-report", "report.parquet", "--tolerance", "0.3", "--tolerance-ppm", "10",
            "--min-intensity", "500", "--max-isolation-window", "8",
            "--temperature-dir", ".", "--output", "out.txt", "--html-report", "out.html",
            "--no-html-report", "--by-file", "--verbose", "--rt-window", "0.083",
            "--no-dedupe-library",
        });

        data.Add("calibrate", new[]
        {
            "--mzml-dir", ".", "--prism-csv", "lib.csv", "--library", "lib.blib",
            "--diann-report", "report.parquet", "--rt-window", "0.083", "--no-dedupe-library",
            "--tolerance", "0.3", "--tolerance-ppm", "10", "--min-intensity", "500",
            "--max-isolation-window", "8", "--temperature-dir", ".",
            "--output-dir", ".", "--model-path", "m.json", "--report", "r.txt",
            "--dump-matches", "d.csv", "--dump-predictions", "p.csv",
            "--no-html-report", "--html-report", "out.html",
            "--n-estimators", "100", "--max-depth", "6", "--learning-rate", "0.1",
            "--seed", "42", "--validation-split", "0.2", "--cv-folds", "5",
            "--robust", "trim", "--robust-sigma", "3", "--max-training-rows", "0",
            "--threads", "4", "--min-training-rows", "1000", "--no-recalibrate",
            "--python-compat", "--on-reorder", "warn", "--verbose",
        });

        data.Add("apply", new[]
        {
            "--model", "m.json", "--mzml-dir", ".", "--output-dir", ".",
            "--max-isolation-window", "8", "--python-compat", "--on-reorder", "warn",
            "--temperature-dir", ".", "--validate", "--threads", "4", "--verbose",
        });

        data.Add("verify", new[]
        {
            "--keep", "--output", "out.mzML", "--threads", "4", "--check-offsets", "10",
            "--verbose",
        });

        return data;
    }
}
