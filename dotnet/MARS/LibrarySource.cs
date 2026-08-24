// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;
using MARS.IO;

namespace MARS.Cli;

/// <summary>
/// Where the spectral library comes from, read from the command line but not yet opened.
/// </summary>
/// <remarks>
/// Separating "which options were given" from "load it" is what lets both commands read
/// every option they understand before they start work, so
/// <see cref="CommandLineArgs.RejectUnknown"/> can run against a complete picture. It also
/// puts the library-choosing rules in one place: `qc` and `calibrate` have to agree about
/// which file wins and how it is read, or the accuracy `qc` reports is not the accuracy
/// `calibrate` would have found.
/// </remarks>
public sealed class LibrarySource
{
    private LibrarySource()
    {
    }

    private string? PrismReport { get; init; }

    private string? LibraryPath { get; init; }

    private string? DiannReport { get; init; }

    private double RtWindow { get; init; }

    private bool Dedupe { get; init; }

    public static LibrarySource From(CommandLineArgs args) => new()
    {
        PrismReport = PrismReportPath(args),
        LibraryPath = args.String("library"),
        DiannReport = args.String("diann-report"),
        RtWindow = args.Double("rt-window") ?? 0.083,
        Dedupe = !args.Flag("no-dedupe-library"),
    };

    /// <param name="keepSequences">
    /// Sequences are dropped by default because a plate-scale report carries tens of millions
    /// of them. A dump is a diagnostic run, so the memory is worth the peptide identity in the
    /// output.
    /// </param>
    public SpectralLibrary Load(List<string> runNames, bool keepSequences, Action<string> log)
    {
        var options = new PrismLibraryOptions
        {
            RunNames = runNames,
            DedupeFragments = Dedupe,
            KeepSequences = keepSequences,
        };

        if (PrismReport is not null) return LoadPrismReport(PrismReport, options, log);

        if (LibraryPath is null)
        {
            throw new FileNotFoundException(
                "A library is required: --prism-report or --library.");
        }

        if (LibraryPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return LoadPrismReport(LibraryPath, options, log);

        if (LibraryPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            // Both a Skyline PRISM report and a DIA-NN library arrive as .parquet, so the
            // extension cannot choose between them. Decided by what the file holds: handing a
            // PRISM report to the DIA-NN reader produces a complaint about a missing DIA-NN
            // column, which tells the user nothing about what they actually did wrong.
            if (PrismParquetLibraryReader.Looks(LibraryPath))
            {
                log($"Loading Skyline PRISM report: {LibraryPath}");
                return PrismParquetLibraryReader.Load(LibraryPath, options, log);
            }

            log($"Loading DIA-NN library: {LibraryPath}");
            return DiannParquetLibraryReader.Load(LibraryPath, DiannReport, runNames, log);
        }

        if (LibraryPath.EndsWith(".blib", StringComparison.OrdinalIgnoreCase))
        {
            log($"Loading BiblioSpec library: {LibraryPath}");
            return BlibLibraryReader.Load(LibraryPath, RtWindow, log);
        }

        throw new InvalidDataException(
            $"Unrecognized library type '{Path.GetExtension(LibraryPath)}'. Expected .blib, .parquet or .csv.");
    }

    /// <summary>
    /// Reads both spellings, so that whichever was given is registered with the unknown-option
    /// check.
    /// </summary>
    /// <remarks>
    /// Not `report ?? csv`: that short-circuits, so with --prism-report given, --prism-csv is
    /// never read, and a check that learns an option is real by watching it be read reports the
    /// alias as a typo. That exact mistake cost --resolution a release.
    /// </remarks>
    private static string? PrismReportPath(CommandLineArgs args)
    {
        string? report = args.String("prism-report");
        string? csv = args.String("prism-csv");
        return report ?? csv;
    }

    /// <summary>
    /// Loads a Skyline PRISM report, whichever of the two formats Skyline wrote it in.
    /// </summary>
    private static SpectralLibrary LoadPrismReport(string path, PrismLibraryOptions options, Action<string> log)
    {
        log($"Loading Skyline PRISM report: {path}");

        // By content, not by extension: a report is a report whatever it has been named, and
        // the two formats hold the same columns.
        if (PrismParquetLibraryReader.Looks(path))
            return PrismParquetLibraryReader.Load(path, options, log);

        return PrismCsvLibraryReader.Load(path, options, log);
    }
}
