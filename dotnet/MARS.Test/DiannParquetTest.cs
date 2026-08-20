// Copyright (c) University of Washington 2026. Licensed under the MIT License.
//
// No DIA-NN output is available in this repository, so the reader is exercised against
// parquet files written by the test. That covers the column handling, the RT-window join
// and the error paths; it does NOT prove agreement with a real DIA-NN release, which is
// still outstanding.

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

public sealed class DiannParquetTest : IDisposable
{
    private readonly string _directory;

    public DiannParquetTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-diann-" + Guid.NewGuid().ToString("N")[..12]);
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
        }
    }

    [Fact]
    public void ReadsLibraryAndJoinsRtWindows()
    {
        string libraryPath = Path.Combine(_directory, "report-lib.parquet");
        string reportPath = Path.Combine(_directory, "report.parquet");

        WriteLibrary(libraryPath, precursors: 3, fragmentsPerPrecursor: 4);
        WriteReport(reportPath, new[]
        {
            ("PEPTIDEA2", "run_01", 10.0, 10.5),
            ("PEPTIDEB2", "run_01", 20.0, 20.5),
            ("PEPTIDEC2", "other_run", 30.0, 30.5),
        });

        SpectralLibrary library = DiannParquetLibraryReader.Load(
            libraryPath, reportPath, new[] { "run_01.mzML" });

        Assert.Equal(3, library.EntryCount);
        Assert.Equal(12, library.FragmentCount);

        // The first two precursors are identified in run_01 and take its window; the third
        // is only in another run, so it has none and matches at any retention time.
        Assert.Equal(10.0, library.RtStart[0]);
        Assert.Equal(10.5, library.RtEnd[0]);
        Assert.Equal(20.0, library.RtStart[1]);
        Assert.True(double.IsNaN(library.RtStart[2]));

        Assert.Equal(4, library.FragmentStart[1] - library.FragmentStart[0]);
        Assert.All(library.FragmentMz, mz => Assert.True(mz > 0));
    }

    [Fact]
    public void WindowsWidenAcrossRuns()
    {
        string libraryPath = Path.Combine(_directory, "report-lib.parquet");
        string reportPath = Path.Combine(_directory, "report.parquet");

        WriteLibrary(libraryPath, precursors: 1, fragmentsPerPrecursor: 2);
        WriteReport(reportPath, new[]
        {
            ("PEPTIDEA2", "run_01", 10.0, 10.5),
            ("PEPTIDEA2", "run_02", 9.0, 11.5),
        });

        SpectralLibrary library = DiannParquetLibraryReader.Load(
            libraryPath, reportPath, new[] { "run_01.mzML", "run_02.mzML" });

        // A spectrum from either run has to fall inside the window, so it covers both.
        Assert.Equal(9.0, library.RtStart[0]);
        Assert.Equal(11.5, library.RtEnd[0]);
    }

    [Fact]
    public void AReportHandedInAsALibraryIsNamed()
    {
        string reportPath = Path.Combine(_directory, "report.parquet");
        WriteReport(reportPath, new[] { ("PEPTIDEA2", "run_01", 10.0, 10.5) });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => DiannParquetLibraryReader.Load(reportPath, reportPath, Array.Empty<string>()));

        // Passing report.parquet where report-lib.parquet belongs is the usual mistake, so
        // the message has to say so rather than list missing column names.
        Assert.Contains("report-lib.parquet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingReportIsExplained()
    {
        string libraryPath = Path.Combine(_directory, "report-lib.parquet");
        WriteLibrary(libraryPath, precursors: 1, fragmentsPerPrecursor: 1);

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => DiannParquetLibraryReader.Load(libraryPath, null, Array.Empty<string>()));

        Assert.Contains("--diann-report", error.Message, StringComparison.Ordinal);
    }

    private static void WriteLibrary(string path, int precursors, int fragmentsPerPrecursor)
    {
        var precursorId = new List<string>();
        var modifiedSequence = new List<string>();
        var precursorCharge = new List<int>();
        var precursorMz = new List<double>();
        var productMz = new List<double>();
        var relativeIntensity = new List<float>();   // DIA-NN writes this as float
        var fragmentType = new List<string>();
        var fragmentCharge = new List<int>();
        var seriesNumber = new List<int>();

        for (var p = 0; p < precursors; p++)
        {
            string id = "PEPTIDE" + (char)('A' + p) + "2";
            for (var f = 0; f < fragmentsPerPrecursor; f++)
            {
                precursorId.Add(id);
                modifiedSequence.Add("PEPTIDE" + (char)('A' + p));
                precursorCharge.Add(2);
                precursorMz.Add(500.0 + p);
                productMz.Add(300.0 + (p * 10) + f);
                relativeIntensity.Add(1.0f - (f * 0.1f));
                fragmentType.Add(f % 2 == 0 ? "y" : "b");
                fragmentCharge.Add(1);
                seriesNumber.Add(f + 3);
            }
        }

        var fields = new List<DataField>
        {
            new DataField<string>(DiannParquetLibraryReader.PrecursorIdColumn),
            new DataField<string>(DiannParquetLibraryReader.ModifiedSequenceColumn),
            new DataField<string>(DiannParquetLibraryReader.StrippedSequenceColumn),
            new DataField<int>(DiannParquetLibraryReader.PrecursorChargeColumn),
            new DataField<double>(DiannParquetLibraryReader.PrecursorMzColumn),
            new DataField<double>(DiannParquetLibraryReader.ProductMzColumn),
            new DataField<float>(DiannParquetLibraryReader.RelativeIntensityColumn),
            new DataField<string>(DiannParquetLibraryReader.FragmentTypeColumn),
            new DataField<int>(DiannParquetLibraryReader.FragmentChargeColumn),
            new DataField<int>(DiannParquetLibraryReader.FragmentSeriesNumberColumn),
        };

        var columns = new List<Array>
        {
            precursorId.ToArray(),
            modifiedSequence.ToArray(),
            modifiedSequence.ToArray(),
            precursorCharge.ToArray(),
            precursorMz.ToArray(),
            productMz.ToArray(),
            relativeIntensity.ToArray(),
            fragmentType.ToArray(),
            fragmentCharge.ToArray(),
            seriesNumber.ToArray(),
        };

        WriteParquet(path, fields, columns);
    }

    private static void WriteReport(string path, (string Id, string Run, double Start, double Stop)[] rows)
    {
        var fields = new List<DataField>
        {
            new DataField<string>(DiannParquetLibraryReader.PrecursorIdColumn),
            new DataField<string>(DiannParquetLibraryReader.RunColumn),
            new DataField<double>(DiannParquetLibraryReader.RtStartColumn),
            new DataField<double>(DiannParquetLibraryReader.RtStopColumn),
        };

        var columns = new List<Array>
        {
            rows.Select(r => r.Id).ToArray(),
            rows.Select(r => r.Run).ToArray(),
            rows.Select(r => r.Start).ToArray(),
            rows.Select(r => r.Stop).ToArray(),
        };

        WriteParquet(path, fields, columns);
    }

    private static void WriteParquet(string path, List<DataField> fields, List<Array> columns)
    {
        var schema = new ParquetSchema(fields.Cast<Field>().ToArray());
        using Stream stream = File.Create(path);
        using ParquetWriter writer = ParquetWriter.CreateAsync(schema, stream).GetAwaiter().GetResult();
        using ParquetRowGroupWriter group = writer.CreateRowGroup();

        for (var i = 0; i < fields.Count; i++)
            group.WriteColumnAsync(new DataColumn(fields[i], columns[i])).GetAwaiter().GetResult();
    }
}
