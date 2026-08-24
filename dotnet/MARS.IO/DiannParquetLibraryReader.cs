// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Ported from load_diann_library in mars/library.py.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MARS.Core;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace MARS.IO;

public static class DiannParquetLibraryReader
{
    public const string PrecursorIdColumn = "Precursor.Id";
    public const string ModifiedSequenceColumn = "Modified.Sequence";
    public const string StrippedSequenceColumn = "Stripped.Sequence";
    public const string PrecursorChargeColumn = "Precursor.Charge";
    public const string PrecursorMzColumn = "Precursor.Mz";
    public const string ProductMzColumn = "Product.Mz";
    public const string RelativeIntensityColumn = "Relative.Intensity";
    public const string FragmentTypeColumn = "Fragment.Type";
    public const string FragmentChargeColumn = "Fragment.Charge";
    public const string FragmentSeriesNumberColumn = "Fragment.Series.Number";
    public const string RunColumn = "Run";
    public const string RtStartColumn = "RT.Start";
    public const string RtStopColumn = "RT.Stop";

    /// <summary>
    /// Loads a DIA-NN spectral library, taking per-run retention time windows from the
    /// companion report.
    /// </summary>
    /// <param name="libraryPath">report-lib.parquet, holding the fragments.</param>
    /// <param name="reportPath">
    /// report.parquet, holding RT.Start and RT.Stop. When null, a report.parquet beside the
    /// library is used.
    /// </param>
    /// <param name="runNames">mzML files being processed; other runs' RT windows are ignored.</param>
    public static SpectralLibrary Load(
        string libraryPath,
        string? reportPath,
        IReadOnlyList<string> runNames,
        Action<string>? log = null)
    {
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException("DIA-NN library parquet not found.", libraryPath);

        string report = reportPath ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(libraryPath)) ?? ".", "report.parquet");

        if (!File.Exists(report))
        {
            throw new FileNotFoundException(
                $"DIA-NN report.parquet not found next to {Path.GetFileName(libraryPath)}. It supplies the " +
                "RT.Start and RT.Stop windows MARS matches within; pass it with --diann-report.",
                report);
        }

        Dictionary<string, (double Start, double Stop)> rtWindows = ReadRtWindows(report, runNames, log);

        var builder = new SpectralLibraryBuilder(keepSequences: false, dedupeFragments: true);
        long fragmentRows = 0, withRtWindow = 0;

        string currentPrecursor = string.Empty;
        var haveEntry = false;

        foreach (LibraryRow row in ReadLibraryRows(libraryPath, log))
        {
            fragmentRows++;

            if (!haveEntry || !string.Equals(row.PrecursorId, currentPrecursor, StringComparison.Ordinal))
            {
                if (haveEntry) builder.EndEntry();

                double start = double.NaN, stop = double.NaN;
                if (rtWindows.TryGetValue(row.PrecursorId, out (double Start, double Stop) window))
                {
                    start = window.Start;
                    stop = window.Stop;
                    withRtWindow++;
                }

                builder.BeginEntry(row.ModifiedSequence, row.PrecursorCharge, row.PrecursorMz, start, stop);
                currentPrecursor = row.PrecursorId;
                haveEntry = true;
            }

            if (double.IsNaN(row.ProductMz) || row.ProductMz <= 0) continue;

            char ionType = row.FragmentType.Length > 0 ? char.ToLowerInvariant(row.FragmentType[0]) : '?';
            builder.AddFragment(row.ProductMz, row.RelativeIntensity, ionType, row.FragmentSeriesNumber, row.FragmentCharge);
        }

        if (haveEntry) builder.EndEntry();

        SpectralLibrary library = builder.Build();
        log?.Invoke($"DIA-NN library: {fragmentRows:N0} fragment rows, {library.EntryCount:N0} precursors, " +
                    $"{library.FragmentCount:N0} fragments");
        log?.Invoke($"  {withRtWindow:N0} precursors carry an RT window from the report");

        if (withRtWindow == 0)
        {
            log?.Invoke("  WARNING: no precursor matched a report RT window. Matching will consider every " +
                        "library precursor at every retention time, which is slow and adds false matches.");
        }

        return library;
    }

    private readonly struct LibraryRow
    {
        public LibraryRow(
            string precursorId, string modifiedSequence, int precursorCharge, double precursorMz,
            double productMz, double relativeIntensity, string fragmentType, int fragmentCharge,
            int fragmentSeriesNumber)
        {
            PrecursorId = precursorId;
            ModifiedSequence = modifiedSequence;
            PrecursorCharge = precursorCharge;
            PrecursorMz = precursorMz;
            ProductMz = productMz;
            RelativeIntensity = relativeIntensity;
            FragmentType = fragmentType;
            FragmentCharge = fragmentCharge;
            FragmentSeriesNumber = fragmentSeriesNumber;
        }

        public string PrecursorId { get; }

        public string ModifiedSequence { get; }

        public int PrecursorCharge { get; }

        public double PrecursorMz { get; }

        public double ProductMz { get; }

        public double RelativeIntensity { get; }

        public string FragmentType { get; }

        public int FragmentCharge { get; }

        public int FragmentSeriesNumber { get; }
    }

    private static IEnumerable<LibraryRow> ReadLibraryRows(string path, Action<string>? log)
    {
        using Stream stream = File.OpenRead(path);
        using ParquetReader reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

        DataField[] fields = reader.Schema.GetDataFields();
        RequireFields(path, fields, PrecursorIdColumn, ModifiedSequenceColumn, PrecursorChargeColumn,
            PrecursorMzColumn, ProductMzColumn, RelativeIntensityColumn, FragmentTypeColumn,
            FragmentChargeColumn, FragmentSeriesNumberColumn);

        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(group);

            string[] precursorId = ReadStrings(groupReader, fields, PrecursorIdColumn);
            string[] modifiedSequence = ReadStrings(groupReader, fields, ModifiedSequenceColumn);
            double[] precursorCharge = ReadDoubles(groupReader, fields, PrecursorChargeColumn);
            double[] precursorMz = ReadDoubles(groupReader, fields, PrecursorMzColumn);
            double[] productMz = ReadDoubles(groupReader, fields, ProductMzColumn);
            double[] relativeIntensity = ReadDoubles(groupReader, fields, RelativeIntensityColumn);
            string[] fragmentType = ReadStrings(groupReader, fields, FragmentTypeColumn);
            double[] fragmentCharge = ReadDoubles(groupReader, fields, FragmentChargeColumn);
            double[] seriesNumber = ReadDoubles(groupReader, fields, FragmentSeriesNumberColumn);

            int rows = precursorId.Length;
            for (var i = 0; i < rows; i++)
            {
                yield return new LibraryRow(
                    precursorId[i] ?? string.Empty,
                    modifiedSequence.Length > i ? modifiedSequence[i] ?? string.Empty : string.Empty,
                    (int)precursorCharge[i],
                    precursorMz[i],
                    productMz[i],
                    relativeIntensity[i],
                    fragmentType.Length > i ? fragmentType[i] ?? string.Empty : string.Empty,
                    (int)fragmentCharge[i],
                    (int)seriesNumber[i]);
            }
        }
    }

    private static Dictionary<string, (double Start, double Stop)> ReadRtWindows(
        string path, IReadOnlyList<string> runNames, Action<string>? log)
    {
        var windows = new Dictionary<string, (double Start, double Stop)>(StringComparer.Ordinal);
        var filter = new RunNameFilter(runNames);

        using Stream stream = File.OpenRead(path);
        using ParquetReader reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();

        DataField[] fields = reader.Schema.GetDataFields();
        RequireFields(path, fields, PrecursorIdColumn, RunColumn, RtStartColumn, RtStopColumn);

        long rows = 0, kept = 0;
        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using ParquetRowGroupReader groupReader = reader.OpenRowGroupReader(group);

            string[] precursorId = ReadStrings(groupReader, fields, PrecursorIdColumn);
            string[] run = ReadStrings(groupReader, fields, RunColumn);
            double[] start = ReadDoubles(groupReader, fields, RtStartColumn);
            double[] stop = ReadDoubles(groupReader, fields, RtStopColumn);

            for (var i = 0; i < precursorId.Length; i++)
            {
                rows++;
                if (filter.Active && !filter.Matches(run.Length > i ? run[i] ?? string.Empty : string.Empty))
                    continue;
                if (double.IsNaN(start[i]) || double.IsNaN(stop[i])) continue;

                string id = precursorId[i] ?? string.Empty;
                kept++;

                // Several runs can identify the same precursor; widen to cover them all, so
                // a spectrum from any of them still falls inside the window.
                if (windows.TryGetValue(id, out (double Start, double Stop) existing))
                {
                    windows[id] = (Math.Min(existing.Start, start[i]), Math.Max(existing.Stop, stop[i]));
                }
                else
                {
                    windows[id] = (start[i], stop[i]);
                }
            }
        }

        log?.Invoke($"DIA-NN report: {rows:N0} identifications, {kept:N0} kept, " +
                    $"{windows.Count:N0} precursors with RT windows");
        return windows;
    }

    private static void RequireFields(string path, DataField[] fields, params string[] required)
    {
        var present = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);
        var missing = required.Where(name => !present.Contains(name)).ToList();
        if (missing.Count == 0) return;

        // A report.parquet handed in where a report-lib.parquet belongs is the usual mistake.
        if (present.Contains(RtStartColumn) && present.Contains(RunColumn) && missing.Contains(ProductMzColumn))
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' looks like a DIA-NN report, not a spectral library. The library " +
                "is normally named report-lib.parquet and carries Product.Mz and Fragment.Type.");
        }

        throw new InvalidDataException(
            $"Missing required columns in {Path.GetFileName(path)}: {string.Join(", ", missing)}");
    }

    private static DataField Field(DataField[] fields, string name) =>
        fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"Column '{name}' is missing.");

    private static string[] ReadStrings(ParquetRowGroupReader reader, DataField[] fields, string name)
    {
        DataColumn column = reader.ReadColumnAsync(Field(fields, name)).GetAwaiter().GetResult();
        Array data = column.Data;
        var result = new string[data.Length];
        for (var i = 0; i < data.Length; i++) result[i] = data.GetValue(i)?.ToString() ?? string.Empty;
        return result;
    }

    /// <summary>
    /// Reads a numeric column regardless of the physical type DIA-NN chose for it. Column
    /// types drift between DIA-NN versions, so binding to one would break on upgrade.
    /// </summary>
    private static double[] ReadDoubles(ParquetRowGroupReader reader, DataField[] fields, string name)
    {
        DataColumn column = reader.ReadColumnAsync(Field(fields, name)).GetAwaiter().GetResult();
        Array data = column.Data;
        var result = new double[data.Length];

        for (var i = 0; i < data.Length; i++)
        {
            object? value = data.GetValue(i);
            result[i] = value switch
            {
                null => double.NaN,
                double d => d,
                float f => f,
                int n => n,
                long l => l,
                short s => s,
                byte b => b,
                decimal m => (double)m,
                string text => double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : double.NaN,
                _ => double.NaN,
            };
        }

        return result;
    }
}
