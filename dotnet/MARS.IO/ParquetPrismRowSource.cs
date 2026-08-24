// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using Parquet;
using MARS.Core;
using Parquet.Schema;

namespace MARS.IO;

/// <summary>
/// A Skyline PRISM report exported as parquet.
/// </summary>
/// <remarks>
/// <para>
/// Skyline picks parquet from a `.parquet` output extension, and PRISM asks for it because it
/// is roughly fifteen times smaller than the same report as CSV. The columns are the ones the
/// CSV has with the spaces removed - `ProductMz` for `Product Mz` - and the values arrive as
/// native doubles and int32s rather than as text, so nothing here parses a number out of a
/// string or has to think about the machine's locale.
/// </para>
/// <para>
/// Read one row group at a time. A real plate report is 32 million rows across sixteen groups;
/// materialising all of it to iterate it in order would cost gigabytes for no benefit, and the
/// order is the one thing that matters, because the loader turns consecutive rows with the same
/// peptide and charge into one library entry.
/// </para>
/// </remarks>
internal sealed class ParquetPrismRowSource : IPrismRowSource
{
    // The names Skyline writes into parquet. Matched loosely, so a report that has been
    // through a converter and arrived with the CSV's spacing still reads.
    public const string PeptideColumn = "PeptideModifiedSequenceUnimodIds";
    public const string PrecursorChargeColumn = "PrecursorCharge";
    public const string PrecursorMzColumn = "PrecursorMz";
    public const string FragmentIonColumn = "FragmentIon";
    public const string ProductChargeColumn = "ProductCharge";
    public const string ProductMzColumn = "ProductMz";
    public const string StartTimeColumn = "StartTime";
    public const string EndTimeColumn = "EndTime";
    public const string AreaColumn = "Area";
    public const string FileNameColumn = "FileName";
    public const string ReplicateNameColumn = "ReplicateName";

    /// <summary>The columns a PRISM report must have for this reader to be the right one.</summary>
    public static readonly string[] RequiredColumns =
    {
        PeptideColumn, PrecursorChargeColumn, PrecursorMzColumn,
        FragmentIonColumn, ProductChargeColumn, ProductMzColumn,
        StartTimeColumn, EndTimeColumn,
    };

    private readonly Stream _stream;
    private readonly ParquetReader _reader;
    private readonly DataField[] _fields;

    private string[] _peptide = Array.Empty<string>();
    private int[] _precursorCharge = Array.Empty<int>();
    private double[] _precursorMz = Array.Empty<double>();
    private string[] _fragmentIon = Array.Empty<string>();
    private int[] _productCharge = Array.Empty<int>();
    private double[] _productMz = Array.Empty<double>();
    private double[] _startTime = Array.Empty<double>();
    private double[] _endTime = Array.Empty<double>();
    private double[] _area = Array.Empty<double>();
    private string[] _runName = Array.Empty<string>();

    private int _group = -1;
    private int _row = -1;
    private int _rowsInGroup;

    public ParquetPrismRowSource(string path)
    {
        Name = Path.GetFileName(path);
        _stream = File.OpenRead(path);
        _reader = ParquetReader.CreateAsync(_stream).GetAwaiter().GetResult();
        _fields = _reader.Schema.GetDataFields();

        var missing = new System.Collections.Generic.List<string>();
        foreach (string required in RequiredColumns)
        {
            if (!ParquetColumns.HasLoose(_fields, required)) missing.Add(required);
        }

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"Missing required columns in {Name}: {string.Join(", ", missing)}");
        }

        RunColumn = ParquetColumns.HasLoose(_fields, FileNameColumn) ? FileNameColumn
            : ParquetColumns.HasLoose(_fields, ReplicateNameColumn) ? ReplicateNameColumn
            : null;
    }

    public string Kind => "PRISM parquet";

    public string Name { get; }

    public string? RunColumn { get; }

    public bool ReadRow()
    {
        while (++_row >= _rowsInGroup)
        {
            if (!NextGroup()) return false;
        }

        return true;
    }

    public string Peptide => _peptide[_row];

    public int PrecursorCharge => _precursorCharge[_row];

    public double PrecursorMz => _precursorMz[_row];

    public string FragmentIon => _fragmentIon[_row];

    public int ProductCharge => _productCharge[_row];

    public double ProductMz => _productMz[_row];

    public double StartTime => _startTime[_row];

    public double EndTime => _endTime[_row];

    public double Area => _area.Length > 0 ? _area[_row] : 1.0;

    public string RunName => _runName.Length > 0 ? _runName[_row] : string.Empty;

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    private bool NextGroup()
    {
        while (++_group < _reader.RowGroupCount)
        {
            using ParquetRowGroupReader group = _reader.OpenRowGroupReader(_group);

            _peptide = Strings(group, PeptideColumn);
            _precursorCharge = Ints(group, PrecursorChargeColumn, fallback: 0);
            _precursorMz = Doubles(group, PrecursorMzColumn);
            _fragmentIon = Strings(group, FragmentIonColumn);

            // A missing product charge means singly charged, which is what the CSV reader
            // assumes for an empty field.
            _productCharge = Ints(group, ProductChargeColumn, fallback: 1);
            _productMz = Doubles(group, ProductMzColumn);
            _startTime = Doubles(group, StartTimeColumn);
            _endTime = Doubles(group, EndTimeColumn);

            _area = ParquetColumns.HasLoose(_fields, AreaColumn)
                ? Doubles(group, AreaColumn)
                : Array.Empty<double>();

            _runName = RunColumn is null ? Array.Empty<string>() : Strings(group, RunColumn);

            _rowsInGroup = _peptide.Length;

            // -1, not 0: the caller's loop increments before testing, so leaving this at 0
            // would step past the first row of every group. With one row group that is nearly
            // invisible - the first row of a Skyline report is usually a precursor row, which
            // is skipped anyway - and with several it silently drops a transition per group.
            _row = -1;

            // An empty row group is legal and carries nothing; skip to the next.
            if (_rowsInGroup > 0) return true;
        }

        return false;
    }

    private string[] Strings(ParquetRowGroupReader group, string name) =>
        ParquetColumns.ReadStrings(group, ParquetColumns.FindLoose(_fields, name)!);

    private double[] Doubles(ParquetRowGroupReader group, string name) =>
        ParquetColumns.ReadDoubles(group, ParquetColumns.FindLoose(_fields, name)!);

    private int[] Ints(ParquetRowGroupReader group, string name, int fallback) =>
        ParquetColumns.ReadInts(group, ParquetColumns.FindLoose(_fields, name)!, fallback);
}

/// <summary>Loads a Skyline PRISM report stored as parquet.</summary>
public static class PrismParquetLibraryReader
{
    public static SpectralLibrary Load(string path, PrismLibraryOptions options, Action<string>? log = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PRISM report not found.", path);

        using var source = new ParquetPrismRowSource(path);
        return PrismLibraryLoader.Load(source, options, log);
    }

    /// <summary>
    /// True when this parquet file is a Skyline PRISM report rather than a DIA-NN library.
    /// </summary>
    /// <remarks>
    /// Decided by what the file contains, not by what it is called. Both arrive as `.parquet`,
    /// and handing one to the other's reader produces either a confusing error about a missing
    /// DIA-NN column or, worse, a library built from columns that happened to parse.
    /// </remarks>
    public static bool Looks(string path)
    {
        try
        {
            using Stream stream = File.OpenRead(path);
            using ParquetReader reader = ParquetReader.CreateAsync(stream).GetAwaiter().GetResult();
            DataField[] fields = reader.Schema.GetDataFields();

            foreach (string required in ParquetPrismRowSource.RequiredColumns)
            {
                if (!ParquetColumns.HasLoose(fields, required)) return false;
            }

            return true;
        }
        catch (Exception)
        {
            // Unreadable, or not parquet at all. Saying "not a PRISM report" here lets the
            // caller fall through to the reader that will report the real problem.
            return false;
        }
    }
}
