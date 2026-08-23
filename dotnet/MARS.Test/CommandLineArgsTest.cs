// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The refusal has to reach the user as an error, not as an unhandled exception. It is
    /// raised by throwing, so Program has to catch it - which it did not, briefly, and the
    /// only symptom was a stack trace where a one-line message belonged.
    /// </summary>
    [Fact]
    public void ATypoIsReportedAsAnInputErrorRatherThanACrash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-typo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string mzml = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(mzml, spectrumCount: 4, chromatogramCount: 0);

            int exit = Program.Main(new[]
            {
                "qc", "--mzml", mzml, "--prism-csv", "nonexistent.csv", "--tolernace", "0.3",
            });

            Assert.Equal(Program.ExitInputError, exit);
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

    /// <summary>
    /// Every option each command documents in its own <c>--help</c>, scraped from that help
    /// text rather than listed here.
    /// </summary>
    /// <remarks>
    /// The first version of this was a hand-written array per command, and it drifted: a
    /// `--resolution` option was added to the CLI and not to the list, so the check passed
    /// while that option was in fact being rejected as a typo. Reading the help text means the
    /// test cannot fall behind the thing it is testing - if an option is documented, it is
    /// checked.
    /// </remarks>
    public static TheoryData<string, string[]> DocumentedOptions()
    {
        var data = new TheoryData<string, string[]>();
        foreach (string command in new[] { "qc", "calibrate", "apply", "verify" })
            data.Add(command, OptionsFromHelp(command));
        return data;
    }

    /// <summary>Pulls the long option names out of a command's help text.</summary>
    private static string[] OptionsFromHelp(string command)
    {
        string help = CaptureHelp(command);
        var options = new List<string>();

        foreach (Match match in Regex.Matches(help, @"--([a-z0-9][a-z0-9-]*)"))
        {
            string name = match.Groups[1].Value;

            // "--help" would print and exit, and the file arguments are supplied by the caller.
            if (name is "help" or "mzml" or "input") continue;
            if (options.Contains("--" + name)) continue;

            options.Add("--" + name);

            // A value for anything that takes one. The help text shows a placeholder in angle
            // brackets after options that do; flags have nothing after them.
            if (Regex.IsMatch(help, Regex.Escape("--" + name) + @"[= ]<"))
                options.Add(ValueFor(name));
        }

        return options.ToArray();
    }

    /// <summary>A value each option will accept, so parsing gets far enough to matter.</summary>
    private static string ValueFor(string name) => name switch
    {
        "resolution" => "auto",
        "robust" => "trim",
        "on-reorder" => "clamp",
        "output-format" => "mzML",
        var n when n.Contains("dir") => ".",
        var n when n.Contains("csv") => "lib.csv",
        var n when n.Contains("parquet") || n.Contains("report") => "report.parquet",
        var n when n.Contains("library") => "lib.blib",
        var n when n.Contains("model") => "model.json",
        var n when n.Contains("path") || n.Contains("output") => "out.txt",
        _ => "1",
    };

    private static string CaptureHelp(string command)
    {
        TextWriter original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            Run(command, new[] { command, "--help" });
        }
        catch (Exception)
        {
            // A command that refuses to run without inputs has still printed its help.
        }
        finally
        {
            Console.SetError(original);
        }

        string help = captured.ToString();
        Assert.False(string.IsNullOrWhiteSpace(help), $"mars {command} --help printed nothing");
        return help;
    }
}