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

    private string? PrismCsv { get; init; }

    private string? LibraryPath { get; init; }

    private string? DiannReport { get; init; }

    private double RtWindow { get; init; }

    private bool Dedupe { get; init; }

    public static LibrarySource From(CommandLineArgs args) => new()
    {
        PrismCsv = args.String("prism-csv"),
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

        if (PrismCsv is not null)
        {
            log($"Loading Skyline PRISM report: {PrismCsv}");
            return PrismCsvLibraryReader.Load(PrismCsv, options, log);
        }

        if (LibraryPath is null)
            throw new FileNotFoundException("A library is required: --prism-csv or --library.");

        if (LibraryPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            log($"Loading Skyline PRISM report: {LibraryPath}");
            return PrismCsvLibraryReader.Load(LibraryPath, options, log);
        }

        if (LibraryPath.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
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
}
