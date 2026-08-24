// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;

namespace MARS.IO;

/// <summary>
/// One Skyline PRISM report, row by row, whatever it is stored in.
/// </summary>
/// <remarks>
/// Skyline exports the same report as CSV or as parquet - it picks parquet from a `.parquet`
/// output extension - and PRISM now asks for parquet, which is roughly fifteen times smaller.
/// The two carry the same columns under different names, spaced in the CSV and not in the
/// parquet, and the parquet carries native types where the CSV carries text. Nothing else
/// about them differs, so nothing above this interface needs to know which one it was handed.
/// </remarks>
internal interface IPrismRowSource : IDisposable
{
    /// <summary>What to call this in a log line: "PRISM CSV" or "PRISM parquet".</summary>
    string Kind { get; }

    /// <summary>File name, for messages.</summary>
    string Name { get; }

    /// <summary>
    /// The column rows are filtered by - File Name where the report has one, Replicate Name
    /// otherwise - or null when it has neither and every row is used.
    /// </summary>
    string? RunColumn { get; }

    /// <summary>Advances to the next row. False at the end.</summary>
    bool ReadRow();

    string Peptide { get; }

    int PrecursorCharge { get; }

    double PrecursorMz { get; }

    string FragmentIon { get; }

    int ProductCharge { get; }

    double ProductMz { get; }

    double StartTime { get; }

    double EndTime { get; }

    /// <summary>Peak area, or 1.0 where the report does not carry one.</summary>
    double Area { get; }

    /// <summary>The value of <see cref="RunColumn"/> on this row, or empty when there is none.</summary>
    string RunName { get; }
}

/// <summary>
/// Turns a PRISM report into a spectral library.
/// </summary>
/// <remarks>
/// Shared by the CSV and parquet readers, because the interesting part is not the parsing. It
/// is how rows become entries: a Skyline report lists one row per transition per replicate,
/// ordered so that a precursor's transitions sit together, and a new entry begins wherever the
/// peptide or the charge changes from the row before. That ordering assumption, the handling of
/// precursor rows, the deduplication across replicates and the counters that report all of it
/// are the behaviour worth having in one place rather than two.
/// </remarks>
internal static class PrismLibraryLoader
{
    public static SpectralLibrary Load(IPrismRowSource source, PrismLibraryOptions options, Action<string>? log)
    {
        var runFilter = new RunNameFilter(options.RunNames);
        bool filtering = runFilter.Active && source.RunColumn is not null;
        if (options.RunNames.Count > 0 && !filtering)
        {
            log?.Invoke(
                $"{source.Kind} has no {PrismCsvLibraryReader.FileNameColumn} or " +
                $"{PrismCsvLibraryReader.ReplicateNameColumn} column; using every row.");
        }

        var builder = new SpectralLibraryBuilder(options.KeepSequences, options.DedupeFragments);
        var seenKeys = new HashSet<(string, int)>();

        string currentPeptide = string.Empty;
        int currentCharge = int.MinValue;
        bool haveEntry = false;

        long rowsRead = 0, rowsFiltered = 0, precursorRows = 0, fragmentRows = 0, duplicateFragments = 0;
        long repeatedGroups = 0;
        var distinctFilterValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A plate-scale Skyline report runs to tens of millions of rows. Without a heartbeat
        // the load looks like a hang.
        const long ProgressInterval = 5_000_000;
        long nextProgress = ProgressInterval;

        while (source.ReadRow())
        {
            rowsRead++;

            if (rowsRead >= nextProgress)
            {
                log?.Invoke($"  {rowsRead:N0} rows read, {builder.EntryCount:N0} precursors so far...");
                nextProgress += ProgressInterval;
            }

            if (filtering)
            {
                string runValue = source.RunName;
                if (distinctFilterValues.Count < 64) distinctFilterValues.Add(runValue);
                if (!runFilter.Matches(runValue))
                {
                    rowsFiltered++;
                    continue;
                }
            }

            string fragmentIon = source.FragmentIon;
            if (string.Equals(fragmentIon, "precursor", StringComparison.OrdinalIgnoreCase))
            {
                precursorRows++;
                continue;
            }

            string peptide = source.Peptide;
            int charge = source.PrecursorCharge;

            if (!haveEntry || charge != currentCharge || !string.Equals(peptide, currentPeptide, StringComparison.Ordinal))
            {
                if (haveEntry) builder.EndEntry();

                if (!seenKeys.Add((peptide, charge))) repeatedGroups++;

                builder.BeginEntry(peptide, charge, source.PrecursorMz, source.StartTime, source.EndTime);

                currentPeptide = peptide;
                currentCharge = charge;
                haveEntry = true;
            }

            double productMz = source.ProductMz;
            if (double.IsNaN(productMz) || productMz <= 0) continue;

            double area = source.Area;
            if (double.IsNaN(area)) area = 1.0;

            (char ionType, int ionNumber) = PrismCsvLibraryReader.ParseFragmentIon(fragmentIon);

            if (builder.AddFragment(productMz, area, ionType, ionNumber, source.ProductCharge)) fragmentRows++;
            else duplicateFragments++;
        }

        if (haveEntry) builder.EndEntry();

        SpectralLibrary library = builder.Build();

        log?.Invoke($"{source.Kind}: {rowsRead:N0} rows read, {precursorRows:N0} precursor rows skipped");
        if (filtering)
        {
            log?.Invoke($"  {rowsFiltered:N0} rows skipped by {source.RunColumn} filter " +
                        $"({runFilter.Describe()})");
        }

        if (duplicateFragments > 0)
            log?.Invoke($"  {duplicateFragments:N0} duplicate transitions collapsed across replicates");

        if (repeatedGroups > 0)
        {
            log?.Invoke($"  WARNING: {repeatedGroups:N0} precursors appear in more than one block. " +
                        "Each block became its own library entry, which can duplicate matches.");
        }

        log?.Invoke($"  {library.EntryCount:N0} precursors, {library.FragmentCount:N0} fragments");

        if (library.EntryCount == 0)
        {
            string values = string.Join(", ", distinctFilterValues);
            throw new InvalidDataException(
                filtering
                    ? $"No {source.Kind} rows matched the input files. Column '{source.RunColumn}' holds: " +
                      $"{values}. Inputs normalize to: {runFilter.Describe()}."
                    : $"No usable fragment rows in {source.Name}.");
        }

        return library;
    }
}
