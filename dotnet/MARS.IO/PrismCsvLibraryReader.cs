// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from load_prism_library in mars/library.py.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;

namespace MARS.IO;

public sealed class PrismLibraryOptions
{
    /// <summary>
    /// mzML file names being processed. Rows from other replicates are skipped. Empty means
    /// no filtering.
    /// </summary>
    public IReadOnlyList<string> RunNames { get; set; } = Array.Empty<string>();

    /// <summary>Keep modified sequences, for per-match reporting. Costs memory at plate scale.</summary>
    public bool KeepSequences { get; set; }

    /// <summary>
    /// Collapse transitions that repeat across replicates. A Skyline report lists every
    /// transition once per replicate with an identical theoretical Product Mz, so the copies
    /// are exact duplicates: they multiply matching work and training rows without adding
    /// information.
    /// </summary>
    public bool DedupeFragments { get; set; } = true;
}

public static class PrismCsvLibraryReader
{
    public const string PeptideColumn = "Peptide Modified Sequence Unimod Ids";
    public const string PrecursorChargeColumn = "Precursor Charge";
    public const string PrecursorMzColumn = "Precursor Mz";
    public const string FragmentIonColumn = "Fragment Ion";
    public const string ProductChargeColumn = "Product Charge";
    public const string ProductMzColumn = "Product Mz";
    public const string StartTimeColumn = "Start Time";
    public const string EndTimeColumn = "End Time";
    public const string AreaColumn = "Area";
    public const string FileNameColumn = "File Name";
    public const string ReplicateNameColumn = "Replicate Name";

    public static SpectralLibrary Load(string path, PrismLibraryOptions options, Action<string>? log = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PRISM CSV not found.", path);

        using var csv = new CsvReader(path);
        if (!csv.ReadHeader()) throw new InvalidDataException($"PRISM CSV is empty: {path}");

        IReadOnlyList<string> missing = csv.RequireColumns(
            PeptideColumn, PrecursorChargeColumn, PrecursorMzColumn,
            FragmentIonColumn, ProductChargeColumn, ProductMzColumn,
            StartTimeColumn, EndTimeColumn);
        if (missing.Count > 0)
            throw new InvalidDataException($"Missing required columns in {Path.GetFileName(path)}: {string.Join(", ", missing)}");

        int peptideAt = csv.ColumnIndex(PeptideColumn);
        int chargeAt = csv.ColumnIndex(PrecursorChargeColumn);
        int precursorMzAt = csv.ColumnIndex(PrecursorMzColumn);
        int fragmentIonAt = csv.ColumnIndex(FragmentIonColumn);
        int productChargeAt = csv.ColumnIndex(ProductChargeColumn);
        int productMzAt = csv.ColumnIndex(ProductMzColumn);
        int startTimeAt = csv.ColumnIndex(StartTimeColumn);
        int endTimeAt = csv.ColumnIndex(EndTimeColumn);
        int areaAt = csv.ColumnIndex(AreaColumn);

        // Skyline writes File Name when it has one, and Replicate Name always.
        int filterAt = csv.HasColumn(FileNameColumn) ? csv.ColumnIndex(FileNameColumn) : csv.ColumnIndex(ReplicateNameColumn);
        string filterColumn = csv.HasColumn(FileNameColumn) ? FileNameColumn : ReplicateNameColumn;

        var runFilter = new RunNameFilter(options.RunNames);
        bool filtering = runFilter.Active && filterAt >= 0;
        if (options.RunNames.Count > 0 && !filtering)
            log?.Invoke($"PRISM CSV has no {FileNameColumn} or {ReplicateNameColumn} column; using every row.");

        var builder = new SpectralLibraryBuilder(options.KeepSequences, options.DedupeFragments);
        var seenKeys = new HashSet<(string, int)>();

        string currentPeptide = string.Empty;
        int currentCharge = int.MinValue;
        bool haveEntry = false;

        long rowsRead = 0, rowsFiltered = 0, precursorRows = 0, fragmentRows = 0, duplicateFragments = 0;
        long repeatedGroups = 0;
        var distinctFilterValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A plate-scale Skyline report runs to tens of gigabytes and tens of millions of
        // rows. Without a heartbeat the load looks like a hang.
        const long ProgressInterval = 5_000_000;
        long nextProgress = ProgressInterval;

        while (csv.ReadRow())
        {
            rowsRead++;

            if (rowsRead >= nextProgress)
            {
                log?.Invoke($"  {rowsRead:N0} rows read, {builder.EntryCount:N0} precursors so far...");
                nextProgress += ProgressInterval;
            }

            if (filtering)
            {
                string runValue = csv.Field(filterAt);
                if (distinctFilterValues.Count < 64) distinctFilterValues.Add(runValue);
                if (!runFilter.Matches(runValue))
                {
                    rowsFiltered++;
                    continue;
                }
            }

            string fragmentIon = csv.Field(fragmentIonAt);
            if (string.Equals(fragmentIon, "precursor", StringComparison.OrdinalIgnoreCase))
            {
                precursorRows++;
                continue;
            }

            string peptide = csv.Field(peptideAt);
            int charge = csv.IntField(chargeAt);

            if (!haveEntry || charge != currentCharge || !string.Equals(peptide, currentPeptide, StringComparison.Ordinal))
            {
                if (haveEntry) builder.EndEntry();

                if (!seenKeys.Add((peptide, charge))) repeatedGroups++;

                builder.BeginEntry(
                    peptide,
                    charge,
                    csv.DoubleField(precursorMzAt),
                    csv.DoubleField(startTimeAt),
                    csv.DoubleField(endTimeAt));

                currentPeptide = peptide;
                currentCharge = charge;
                haveEntry = true;
            }

            double productMz = csv.DoubleField(productMzAt);
            if (double.IsNaN(productMz) || productMz <= 0) continue;

            double area = areaAt >= 0 ? csv.DoubleField(areaAt) : 1.0;
            if (double.IsNaN(area)) area = 1.0;

            (char ionType, int ionNumber) = ParseFragmentIon(fragmentIon);
            int productCharge = csv.IntField(productChargeAt, 1);

            if (builder.AddFragment(productMz, area, ionType, ionNumber, productCharge)) fragmentRows++;
            else duplicateFragments++;
        }

        if (haveEntry) builder.EndEntry();

        SpectralLibrary library = builder.Build();

        log?.Invoke($"PRISM CSV: {rowsRead:N0} rows read, {precursorRows:N0} precursor rows skipped");
        if (filtering)
        {
            log?.Invoke($"  {rowsFiltered:N0} rows skipped by {filterColumn} filter " +
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
                    ? $"No PRISM CSV rows matched the input files. Column '{filterColumn}' holds: {values}. " +
                      $"Inputs normalize to: {runFilter.Describe()}."
                    : $"No usable fragment rows in {Path.GetFileName(path)}.");
        }

        return library;
    }

    /// <summary>
    /// Splits a Skyline fragment annotation such as "y7", "b3", "y5-H2O" into its ion type
    /// and series number. Anything unrecognized becomes '?' with number 0, which matches how
    /// the Python implementation fills unparsed annotations.
    /// </summary>
    public static (char IonType, int IonNumber) ParseFragmentIon(string annotation)
    {
        char ionType = '?';
        if (annotation.Length > 0)
        {
            char first = annotation[0];
            if (first is 'y' or 'b' or 'a' or 'z' or 'c' or 'x') ionType = first;
        }

        var ionNumber = 0;
        for (int i = 0; i < annotation.Length; i++)
        {
            if (!char.IsDigit(annotation[i])) continue;

            int j = i;
            while (j < annotation.Length && char.IsDigit(annotation[j]))
            {
                ionNumber = (ionNumber * 10) + (annotation[j] - '0');
                if (ionNumber > short.MaxValue) return (ionType, 0);
                j++;
            }

            break;
        }

        return (ionType, ionNumber);
    }
}

/// <summary>
/// Decides whether a replicate or file name in a report belongs to the run being processed.
/// Skyline reports name replicates in whatever way the analyst set up, so matching tries an
/// exact base-name match first and only then falls back to a substring test.
/// </summary>
public sealed class RunNameFilter
{
    private static readonly string[] InputSuffixes = { "_uncalibrated", "_calibrated", "-mars", ".mzML", ".mzml", ".raw" };
    private static readonly string[] ReportSuffixes = { ".mzML", ".mzml", ".raw", ".wiff", ".d", "-mars" };

    private readonly List<string> _baseNames = new();
    private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RunNameFilter(IReadOnlyList<string> runNames)
    {
        foreach (string name in runNames)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            foreach (string suffix in InputSuffixes)
                baseName = baseName.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
            if (baseName.Length > 0 && !_baseNames.Contains(baseName)) _baseNames.Add(baseName);
        }
    }

    public bool Active => _baseNames.Count > 0;

    public string Describe() => string.Join(", ", _baseNames);

    public bool Matches(string reportValue)
    {
        if (!Active) return true;
        if (_cache.TryGetValue(reportValue, out bool cached)) return cached;

        string normalized = reportValue;
        foreach (string suffix in ReportSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        var matched = false;
        foreach (string baseName in _baseNames)
        {
            if (string.Equals(normalized, baseName, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            foreach (string baseName in _baseNames)
            {
                if (normalized.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
                    baseName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }
        }

        if (_cache.Count < 4096) _cache[reportValue] = matched;
        return matched;
    }
}
