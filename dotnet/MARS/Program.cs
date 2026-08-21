// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// MARS command line entry point.

using System;
using System.Globalization;
using System.IO;
using MARS.Core;

namespace MARS.Cli;

public static class Program
{
    /// <summary>Exit codes, as specified for the MARS CLI.</summary>
    public const int ExitSuccess = 0;

    public const int ExitInputError = 1;

    public const int ExitInsufficientTrainingData = 2;

    public const int ExitOutputValidationFailure = 3;

    public static int Main(string[] args)
    {
        // Every diagnostic goes to stderr so stdout stays clean for piping.
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? ExitInputError : ExitSuccess;
        }

        if (args[0] is "--version" or "-V")
        {
            Console.Out.WriteLine(MarsInfo.Version);
            return ExitSuccess;
        }

        CommandLineArgs parsed;
        try
        {
            parsed = CommandLineArgs.Parse(args);
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return ExitInputError;
        }

        try
        {
            int exit = parsed.Command.ToLowerInvariant() switch
            {
                "verify" => VerifyCommand.Run(parsed),
                "calibrate" => CalibrateCommand.Run(parsed),
                "apply" => ApplyCommand.Run(parsed),
                "qc" => QcCommand.Run(parsed),
                "compare" => CompareCommand.Run(parsed),
                _ => UnknownCommand(parsed.Command),
            };

            // Commands that finish their option reading call RejectUnknown() themselves,
            // before doing any work. This is the backstop for the ones that return early -
            // an option read after an early return was never queried, so it cannot be
            // distinguished from a typo here and stays a warning rather than an error.
            foreach (string unknown in parsed.UnknownOptions())
                Log.Warn($"Unrecognized option --{unknown} was ignored.");

            return exit;
        }
        catch (UnknownOptionException ex)
        {
            Log.Error(ex.Message);
            return ExitInputError;
        }
        catch (FileNotFoundException ex)
        {
            Log.Error(ex.Message);
            return ExitInputError;
        }
        catch (DirectoryNotFoundException ex)
        {
            Log.Error(ex.Message);
            return ExitInputError;
        }
        catch (InsufficientTrainingDataException ex)
        {
            Log.Error(ex.Message);
            return ExitInsufficientTrainingData;
        }
        catch (OutputValidationException ex)
        {
            Log.Error(ex.Message);
            return ExitOutputValidationFailure;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            if (Log.Verbose) Log.Error(ex.ToString());
            return ExitInputError;
        }
    }

    private static int UnknownCommand(string command)
    {
        Log.Error($"Unknown command '{command}'.");
        PrintUsage();
        return ExitInputError;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine($"""
            MARS {MarsInfo.Version} - Mass Accuracy Recalibration System

            Usage: mars <command> [options]

            Commands:
              calibrate   Learn an m/z calibration from spectral library matches and write
                          recalibrated mzML files.
              apply       Apply a previously trained model to more files.
              qc          Report current mass accuracy without training or writing.
              verify      Round-trip a file through the passthrough writer with a null
                          correction, then check the index, checksum and decoded arrays.
              compare     Compare two mzML files on decoded m/z and intensity values.

            Run 'mars <command> --help' for the options of a command.
            """);
    }
}

/// <summary>Fewer usable training rows than the run needs to fit anything meaningful.</summary>
public sealed class InsufficientTrainingDataException : Exception
{
    public InsufficientTrainingDataException(string message)
        : base(message)
    {
    }
}

/// <summary>A written file failed its structural checks.</summary>
public sealed class OutputValidationException : Exception
{
    public OutputValidationException(string message)
        : base(message)
    {
    }
}

internal static class Log
{
    public static bool Verbose { get; set; }

    public static void Info(string message) =>
        Console.Error.WriteLine($"{Timestamp()} INFO  {message}");

    public static void Warn(string message) =>
        Console.Error.WriteLine($"{Timestamp()} WARN  {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"{Timestamp()} ERROR {message}");

    public static void Debug(string message)
    {
        if (Verbose) Console.Error.WriteLine($"{Timestamp()} DEBUG {message}");
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
