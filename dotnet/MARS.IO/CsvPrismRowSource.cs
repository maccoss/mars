// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;

namespace MARS.IO;

/// <summary>
/// A PRISM report exported as CSV.
/// </summary>
/// <remarks>
/// Every field arrives as text and is parsed per row. The column names carry spaces, which is
/// how Skyline writes a CSV header; its parquet export uses the same columns without them.
/// </remarks>
internal sealed class CsvPrismRowSource : IPrismRowSource
{
    private readonly CsvReader _csv;
    private readonly int _peptideAt;
    private readonly int _chargeAt;
    private readonly int _precursorMzAt;
    private readonly int _fragmentIonAt;
    private readonly int _productChargeAt;
    private readonly int _productMzAt;
    private readonly int _startTimeAt;
    private readonly int _endTimeAt;
    private readonly int _areaAt;
    private readonly int _runAt;

    public CsvPrismRowSource(string path)
    {
        Name = Path.GetFileName(path);
        _csv = new CsvReader(path);

        if (!_csv.ReadHeader()) throw new InvalidDataException($"PRISM CSV is empty: {path}");

        IReadOnlyList<string> missing = _csv.RequireColumns(
            PrismCsvLibraryReader.PeptideColumn,
            PrismCsvLibraryReader.PrecursorChargeColumn,
            PrismCsvLibraryReader.PrecursorMzColumn,
            PrismCsvLibraryReader.FragmentIonColumn,
            PrismCsvLibraryReader.ProductChargeColumn,
            PrismCsvLibraryReader.ProductMzColumn,
            PrismCsvLibraryReader.StartTimeColumn,
            PrismCsvLibraryReader.EndTimeColumn);
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"Missing required columns in {Name}: {string.Join(", ", missing)}");
        }

        _peptideAt = _csv.ColumnIndex(PrismCsvLibraryReader.PeptideColumn);
        _chargeAt = _csv.ColumnIndex(PrismCsvLibraryReader.PrecursorChargeColumn);
        _precursorMzAt = _csv.ColumnIndex(PrismCsvLibraryReader.PrecursorMzColumn);
        _fragmentIonAt = _csv.ColumnIndex(PrismCsvLibraryReader.FragmentIonColumn);
        _productChargeAt = _csv.ColumnIndex(PrismCsvLibraryReader.ProductChargeColumn);
        _productMzAt = _csv.ColumnIndex(PrismCsvLibraryReader.ProductMzColumn);
        _startTimeAt = _csv.ColumnIndex(PrismCsvLibraryReader.StartTimeColumn);
        _endTimeAt = _csv.ColumnIndex(PrismCsvLibraryReader.EndTimeColumn);
        _areaAt = _csv.ColumnIndex(PrismCsvLibraryReader.AreaColumn);

        // Skyline writes File Name when it has one, and Replicate Name always.
        if (_csv.HasColumn(PrismCsvLibraryReader.FileNameColumn))
        {
            RunColumn = PrismCsvLibraryReader.FileNameColumn;
            _runAt = _csv.ColumnIndex(PrismCsvLibraryReader.FileNameColumn);
        }
        else if (_csv.HasColumn(PrismCsvLibraryReader.ReplicateNameColumn))
        {
            RunColumn = PrismCsvLibraryReader.ReplicateNameColumn;
            _runAt = _csv.ColumnIndex(PrismCsvLibraryReader.ReplicateNameColumn);
        }
        else
        {
            RunColumn = null;
            _runAt = -1;
        }
    }

    public string Kind => "PRISM CSV";

    public string Name { get; }

    public string? RunColumn { get; }

    public bool ReadRow() => _csv.ReadRow();

    public string Peptide => _csv.Field(_peptideAt);

    public int PrecursorCharge => _csv.IntField(_chargeAt);

    public double PrecursorMz => _csv.DoubleField(_precursorMzAt);

    public string FragmentIon => _csv.Field(_fragmentIonAt);

    public int ProductCharge => _csv.IntField(_productChargeAt, 1);

    public double ProductMz => _csv.DoubleField(_productMzAt);

    public double StartTime => _csv.DoubleField(_startTimeAt);

    public double EndTime => _csv.DoubleField(_endTimeAt);

    public double Area => _areaAt >= 0 ? _csv.DoubleField(_areaAt) : 1.0;

    public string RunName => _runAt >= 0 ? _csv.Field(_runAt) : string.Empty;

    public void Dispose() => _csv.Dispose();
}
