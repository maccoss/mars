// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MARS.Core;
using MARS.IO;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace MARS.Test;

/// <summary>
/// Reading a Skyline PRISM report stored as parquet.
/// </summary>
/// <remarks>
/// Skyline picks parquet from a `.parquet` output extension and PRISM asks for it, so this is
/// how the report increasingly arrives. It carries the same columns as the CSV with the spaces
/// removed, and native types where the CSV has text, so the thing worth testing is not that
/// parquet can be read - Parquet.Net does that - but that the same report read either way
/// produces the same library.
/// </remarks>
public sealed class PrismParquetTest : IDisposable
{
    private readonly string _directory;

    public PrismParquetTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-prismpq-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file must not fail the suite.
        }
    }

    /// <summary>
    /// The claim the feature rests on: one report, two formats, one library.
    /// </summary>
    [Fact]
    public void TheSameReportReadsIdenticallyFromCsvAndParquet()
    {
        List<Row> rows = SampleRows();
        string csv = Path.Combine(_directory, "report.csv");
        string parquet = Path.Combine(_directory, "report.parquet");
        WriteCsv(csv, rows);
        WriteParquet(parquet, rows, rowsPerGroup: rows.Count);

        SpectralLibrary fromCsv = PrismCsvLibraryReader.Load(csv, Options());
        SpectralLibrary fromParquet = PrismParquetLibraryReader.Load(parquet, Options());

        Assert.Equal(fromCsv.EntryCount, fromParquet.EntryCount);
        Assert.Equal(fromCsv.FragmentCount, fromParquet.FragmentCount);
        Assert.Equal(fromCsv.PrecursorMz, fromParquet.PrecursorMz);
        Assert.Equal(fromCsv.PrecursorCharge, fromParquet.PrecursorCharge);
        Assert.Equal(fromCsv.FragmentMz, fromParquet.FragmentMz);
        Assert.Equal(fromCsv.FragmentCharge, fromParquet.FragmentCharge);
        Assert.Equal(fromCsv.FragmentIonType, fromParquet.FragmentIonType);
        Assert.Equal(fromCsv.FragmentIonNumber, fromParquet.FragmentIonNumber);
        Assert.Equal(fromCsv.PeptideGroup, fromParquet.PeptideGroup);
    }

    /// <summary>
    /// A precursor's transitions become one entry because they arrive together, so the reader
    /// has to hand rows on in file order. Reading column-wise per row group is where that would
    /// break, and a report large enough to span groups is the case that would expose it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void RowOrderSurvivesRowGroupBoundaries(int rowsPerGroup)
    {
        List<Row> rows = SampleRows();
        string parquet = Path.Combine(_directory, $"g{rowsPerGroup}.parquet");
        WriteParquet(parquet, rows, rowsPerGroup);

        string csv = Path.Combine(_directory, "reference.csv");
        WriteCsv(csv, rows);

        SpectralLibrary expected = PrismCsvLibraryReader.Load(csv, Options());
        SpectralLibrary actual = PrismParquetLibraryReader.Load(parquet, Options());

        Assert.Equal(expected.EntryCount, actual.EntryCount);
        Assert.Equal(expected.FragmentMz, actual.FragmentMz);
        Assert.Equal(expected.PeptideGroup, actual.PeptideGroup);
    }

    /// <summary>
    /// A DIA-NN library and a PRISM report are both `.parquet`. The extension cannot tell them
    /// apart, so the caller asks what the file holds.
    /// </summary>
    [Fact]
    public void APrismReportIsRecognisedAndADiannLibraryIsNot()
    {
        string prism = Path.Combine(_directory, "prism.parquet");
        WriteParquet(prism, SampleRows(), rowsPerGroup: 4);
        Assert.True(PrismParquetLibraryReader.Looks(prism));

        // DIA-NN's own column names, none of which a PRISM report has.
        string diann = Path.Combine(_directory, "diann.parquet");
        var fields = new List<DataField>
        {
            new DataField<string>("ModifiedPeptide"),
            new DataField<double>("PrecursorMz"),
            new DataField<double>("ProductMz"),
        };
        var columns = new List<Array>
        {
            new[] { "PEPTIDEK" },
            new[] { 500.0 },
            new[] { 600.0 },
        };
        WriteColumns(diann, fields, columns);
        Assert.False(PrismParquetLibraryReader.Looks(diann));
    }

    /// <summary>Something that is not parquet at all is not a PRISM report, and does not throw.</summary>
    [Fact]
    public void SomethingThatIsNotParquetIsNotMistakenForOne()
    {
        string junk = Path.Combine(_directory, "not.parquet");
        File.WriteAllText(junk, "Peptide Modified Sequence Unimod Ids,Product Mz\nPEPTIDEK,600.0\n");

        Assert.False(PrismParquetLibraryReader.Looks(junk));
    }

    /// <summary>
    /// Skyline writes the columns without spaces; a report that has been through a converter
    /// can arrive with the CSV's spacing. The distinction carries no meaning.
    /// </summary>
    [Fact]
    public void SpacedColumnNamesAreAccepted()
    {
        List<Row> rows = SampleRows();
        string spaced = Path.Combine(_directory, "spaced.parquet");
        WriteParquet(spaced, rows, rowsPerGroup: rows.Count, spacedNames: true);

        Assert.True(PrismParquetLibraryReader.Looks(spaced));
        SpectralLibrary library = PrismParquetLibraryReader.Load(spaced, Options());
        Assert.Equal(3, library.EntryCount);
    }

    /// <summary>A report missing a column MARS needs says which, rather than failing later.</summary>
    [Fact]
    public void AMissingColumnIsNamed()
    {
        string path = Path.Combine(_directory, "short.parquet");
        var fields = new List<DataField>
        {
            new DataField<string>("PeptideModifiedSequenceUnimodIds"),
            new DataField<int>("PrecursorCharge"),
        };
        var columns = new List<Array> { new[] { "PEPTIDEK" }, new[] { 2 } };
        WriteColumns(path, fields, columns);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => PrismParquetLibraryReader.Load(path, Options()));
        Assert.Contains("ProductMz", error.Message, StringComparison.Ordinal);
    }

    private static PrismLibraryOptions Options() => new()
    {
        RunNames = Array.Empty<string>(),
        KeepSequences = true,
        DedupeFragments = true,
    };

    /// <summary>
    /// Three precursors, transitions grouped as Skyline writes them, including a precursor row
    /// that must be skipped and a repeated peptide at a second charge.
    /// </summary>
    private static List<Row> SampleRows() => new()
    {
        new Row("SAMPLERPEPTIDEK", 2, 500.25, "precursor", 1, 500.25, 10.0, 11.0, 0, "run1"),
        new Row("SAMPLERPEPTIDEK", 2, 500.25, "y7", 1, 700.35, 10.0, 11.0, 1000, "run1"),
        new Row("SAMPLERPEPTIDEK", 2, 500.25, "y6", 1, 610.30, 10.0, 11.0, 2000, "run1"),
        new Row("SAMPLERPEPTIDEK", 2, 500.25, "b4", 2, 410.20, 10.0, 11.0, 1500, "run1"),
        new Row("SAMPLERPEPTIDEK", 3, 334.00, "y7", 1, 700.35, 10.2, 11.2, 900, "run1"),
        new Row("SAMPLERPEPTIDEK", 3, 334.00, "y5", 1, 520.28, 10.2, 11.2, 800, "run1"),
        new Row("OTHERPEPTIDER", 2, 620.80, "y8", 1, 880.44, 20.0, 21.0, 5000, "run1"),
        new Row("OTHERPEPTIDER", 2, 620.80, "y4", 1, 480.26, 20.0, 21.0, 4000, "run1"),
    };

    private static void WriteCsv(string path, List<Row> rows)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine(string.Join(",",
            PrismCsvLibraryReader.PeptideColumn, PrismCsvLibraryReader.PrecursorChargeColumn,
            PrismCsvLibraryReader.PrecursorMzColumn, PrismCsvLibraryReader.FragmentIonColumn,
            PrismCsvLibraryReader.ProductChargeColumn, PrismCsvLibraryReader.ProductMzColumn,
            PrismCsvLibraryReader.StartTimeColumn, PrismCsvLibraryReader.EndTimeColumn,
            PrismCsvLibraryReader.AreaColumn, PrismCsvLibraryReader.ReplicateNameColumn));

        foreach (Row r in rows)
        {
            writer.WriteLine(string.Join(",",
                r.Peptide, r.PrecursorCharge,
                r.PrecursorMz.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.FragmentIon, r.ProductCharge,
                r.ProductMz.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.StartTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.EndTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.Area.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                r.Replicate));
        }
    }

    private static void WriteParquet(string path, List<Row> rows, int rowsPerGroup, bool spacedNames = false)
    {
        string N(string unspaced, string spaced) => spacedNames ? spaced : unspaced;

        var fields = new List<DataField>
        {
            new DataField<string>(N("PeptideModifiedSequenceUnimodIds", "Peptide Modified Sequence Unimod Ids")),
            new DataField<int>(N("PrecursorCharge", "Precursor Charge")),
            new DataField<double>(N("PrecursorMz", "Precursor Mz")),
            new DataField<string>(N("FragmentIon", "Fragment Ion")),
            new DataField<int>(N("ProductCharge", "Product Charge")),
            new DataField<double>(N("ProductMz", "Product Mz")),
            new DataField<double>(N("StartTime", "Start Time")),
            new DataField<double>(N("EndTime", "End Time")),
            new DataField<double>("Area"),
            new DataField<string>(N("ReplicateName", "Replicate Name")),
        };

        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        using Stream stream = File.Create(path);
        using ParquetWriter writer = ParquetWriter.CreateAsync(schema, stream).GetAwaiter().GetResult();

        for (var start = 0; start < rows.Count; start += rowsPerGroup)
        {
            List<Row> slice = rows.Skip(start).Take(rowsPerGroup).ToList();
            using ParquetRowGroupWriter group = writer.CreateRowGroup();

            var columns = new List<Array>
            {
                slice.Select(r => r.Peptide).ToArray(),
                slice.Select(r => r.PrecursorCharge).ToArray(),
                slice.Select(r => r.PrecursorMz).ToArray(),
                slice.Select(r => r.FragmentIon).ToArray(),
                slice.Select(r => r.ProductCharge).ToArray(),
                slice.Select(r => r.ProductMz).ToArray(),
                slice.Select(r => r.StartTime).ToArray(),
                slice.Select(r => r.EndTime).ToArray(),
                slice.Select(r => r.Area).ToArray(),
                slice.Select(r => r.Replicate).ToArray(),
            };

            for (var i = 0; i < fields.Count; i++)
                group.WriteColumnAsync(new DataColumn(fields[i], columns[i])).GetAwaiter().GetResult();
        }
    }

    private static void WriteColumns(string path, List<DataField> fields, List<Array> columns)
    {
        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        using Stream stream = File.Create(path);
        using ParquetWriter writer = ParquetWriter.CreateAsync(schema, stream).GetAwaiter().GetResult();
        using ParquetRowGroupWriter group = writer.CreateRowGroup();

        for (var i = 0; i < fields.Count; i++)
            group.WriteColumnAsync(new DataColumn(fields[i], columns[i])).GetAwaiter().GetResult();
    }

    private sealed record Row(
        string Peptide,
        int PrecursorCharge,
        double PrecursorMz,
        string FragmentIon,
        int ProductCharge,
        double ProductMz,
        double StartTime,
        double EndTime,
        double Area,
        string Replicate);
}
