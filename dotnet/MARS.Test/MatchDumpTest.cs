// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

public sealed class MatchDumpTest
{
    private static SpectralLibrary BuildLibrary()
    {
        var builder = new SpectralLibraryBuilder(keepSequences: true);
        builder.BeginEntry("PEPTIDER", 2, 500.25, 10.0, 11.0);
        builder.AddFragment(600.30, 1000, 'y', 5, 1);
        builder.AddFragment(700.40, 2000, 'b', 3, 2);
        builder.BeginEntry("SEQ[+80.0]UENCE", 3, 400.10, 12.0, 13.0);
        builder.AddFragment(800.50, 3000, 'y', 7, 1);
        return builder.Build();
    }

    /// <summary>
    /// A predictions array that is not parallel to the table is refused before the file is
    /// opened, rather than throwing partway through millions of rows with a half-written dump
    /// on disk and nothing naming the cause.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]
    public void AMismatchedPredictionsArrayIsRefusedUpFront(int predictionCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "dump.csv");
            MatchTable table = BuildTable();
            Assert.NotEqual(predictionCount, table.Count);

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                MatchDumpWriter.Write(path, table, BuildLibrary(), new double[predictionCount]));

            Assert.Contains("parallel", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path), "no file should be left behind");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static MatchTable BuildTable()
    {
        MarsFeature[] collect = { MarsFeature.PrecursorMz, MarsFeature.FragmentMz, MarsFeature.LogIntensity };
        var table = new MatchTable(collect, keepDetail: true);

        AddRow(table, scan: 101, retentionTime: 10.5, entry: 0, fragment: 0,
            observedMz: 600.35, deltaMz: 0.05, observedIntensity: 1234.5,
            precursorMz: 500.25, fragmentMz: 600.30, logIntensity: 3.09);
        AddRow(table, scan: 102, retentionTime: 12.5, entry: 1, fragment: 2,
            observedMz: 800.44, deltaMz: -0.06, observedIntensity: 987.0,
            precursorMz: 400.10, fragmentMz: 800.50, logIntensity: double.NaN);

        return table;
    }

    private static void AddRow(
        MatchTable table, int scan, double retentionTime, int entry, int fragment,
        double observedMz, double deltaMz, double observedIntensity,
        double precursorMz, double fragmentMz, double logIntensity)
    {
        table.Set(MarsFeature.PrecursorMz, precursorMz);
        table.Set(MarsFeature.FragmentMz, fragmentMz);
        table.Set(MarsFeature.LogIntensity, logIntensity);
        table.DeltaMz.Add(deltaMz);
        table.ObservedIntensity.Add(observedIntensity);
        table.PeptideGroup.Add(entry);
        table.ScanNumber!.Add(scan);
        table.LibraryEntryIndex!.Add(entry);
        table.FragmentIndex!.Add(fragment);
        table.ObservedMz!.Add(observedMz);
        table.RetentionTime!.Add(retentionTime);
        table.CommitRow();
    }

    [Fact]
    public void WritesOneRowPerMatchWithIdentityAndFeatures()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            MatchDumpWriter.Write(path, BuildTable(), BuildLibrary());
            string[] lines = File.ReadAllLines(path);

            Assert.Equal(3, lines.Length);
            Assert.Equal(
                "scan_number,retention_time,entry_index,fragment_index,peptide_group,peptide,ion_annotation," +
                "expected_mz,observed_mz,delta_mz,observed_intensity," +
                "precursor_mz,fragment_mz,log_intensity",
                lines[0]);

            string[] first = lines[1].Split(',');
            Assert.Equal("101", first[0]);
            Assert.Equal("0", first[2]);
            Assert.Equal("0", first[3]);
            Assert.Equal("0", first[4]);
            Assert.Equal("\"PEPTIDER\"", first[5]);
            Assert.Equal("y5+1", first[6]);
            Assert.Equal(600.30, double.Parse(first[7], CultureInfo.InvariantCulture), 6);
            Assert.Equal(600.35, double.Parse(first[8], CultureInfo.InvariantCulture), 6);

            // The second row points at the second entry's only fragment, which is index 2 in
            // the flat arrays. Getting this wrong would silently label rows with another
            // peptide's identity, so it is asserted rather than assumed.
            string[] second = lines[2].Split(',');
            Assert.Equal("\"SEQ[+80.0]UENCE\"", second[5]);
            Assert.Equal("y7+1", second[6]);
            Assert.Equal(800.50, double.Parse(second[7], CultureInfo.InvariantCulture), 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RoundTripsDoublesWithoutLosingPrecision()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            MatchDumpWriter.Write(path, BuildTable(), BuildLibrary());
            string[] columns = File.ReadAllLines(path)[1].Split(',');

            // "R" formatting is what makes a dump usable as a comparison oracle: a value that
            // rounds on the way out cannot be differenced against another implementation at
            // the precision that matters for m/z.
            Assert.Equal(1234.5, double.Parse(columns[10], CultureInfo.InvariantCulture));
            Assert.Equal(500.25, double.Parse(columns[11], CultureInfo.InvariantCulture));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WritesNaNRatherThanBlankingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            MatchDumpWriter.Write(path, BuildTable(), BuildLibrary());
            string[] columns = File.ReadAllLines(path)[2].Split(',');

            // NaN is how an undefined feature reaches the model, and row selection drops on
            // it. A blank would read as "missing column" instead.
            Assert.Equal("NaN", columns[13]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RefusesATableWithoutDetailColumns()
    {
        var table = new MatchTable(new[] { MarsFeature.FragmentMz });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => MatchDumpWriter.Write("unused.csv", table, BuildLibrary()));
        Assert.Contains("keepDetail", error.Message, StringComparison.Ordinal);
    }
}
