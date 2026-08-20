// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using MARS.IO;
using Xunit;

namespace MARS.Test;

public sealed class CsvReaderTest
{
    private static CsvReader Reader(string text) => new(new StringReader(text));

    [Fact]
    public void ReadsHeaderAndRows()
    {
        using CsvReader csv = Reader("a,b,c\n1,2,3\n4,5,6\n");

        Assert.True(csv.ReadHeader());
        Assert.Equal(new[] { "a", "b", "c" }, csv.Header);
        Assert.Equal(1, csv.ColumnIndex("b"));
        Assert.Equal(-1, csv.ColumnIndex("missing"));

        Assert.True(csv.ReadRow());
        Assert.Equal("2", csv.Field(1));
        Assert.Equal(3, csv.IntField(2));

        Assert.True(csv.ReadRow());
        Assert.Equal("4", csv.Field(0));
        Assert.False(csv.ReadRow());
    }

    [Fact]
    public void HandlesQuotedFields()
    {
        // Skyline writes protein descriptions containing commas and quotes.
        using CsvReader csv = Reader("name,note\n\"Smith, John\",\"he said \"\"hi\"\"\"\n");

        Assert.True(csv.ReadHeader());
        Assert.True(csv.ReadRow());
        Assert.Equal("Smith, John", csv.Field(0));
        Assert.Equal("he said \"hi\"", csv.Field(1));
    }

    [Fact]
    public void HandlesCrLfAndAMissingFinalNewline()
    {
        using CsvReader csv = Reader("a,b\r\n1,2\r\n3,4");

        Assert.True(csv.ReadHeader());
        Assert.True(csv.ReadRow());
        Assert.Equal("2", csv.Field(1));
        Assert.True(csv.ReadRow());
        Assert.Equal("4", csv.Field(1));
        Assert.False(csv.ReadRow());
    }

    [Fact]
    public void EmptyAndUnparseableFieldsBecomeSentinels()
    {
        using CsvReader csv = Reader("a,b,c\n,#N/A,7\n");

        Assert.True(csv.ReadHeader());
        Assert.True(csv.ReadRow());
        Assert.Equal(string.Empty, csv.Field(0));

        // Skyline writes #N/A for a value it could not compute; it must not become 0.
        Assert.True(double.IsNaN(csv.DoubleField(0)));
        Assert.True(double.IsNaN(csv.DoubleField(1)));
        Assert.Equal(7, csv.IntField(2));
        Assert.Equal(5, csv.IntField(0, fallback: 5));
    }

    [Fact]
    public void ReportsMissingRequiredColumns()
    {
        using CsvReader csv = Reader("a,b\n1,2\n");
        Assert.True(csv.ReadHeader());

        Assert.Empty(csv.RequireColumns("a", "b"));
        Assert.Equal(new[] { "c" }, csv.RequireColumns("a", "c"));
    }
}

public sealed class RunNameFilterTest
{
    [Fact]
    public void MatchesOnBaseNameIgnoringExtensions()
    {
        var filter = new RunNameFilter(new[] { "Ste-2024-12-02_HeLa_GPFDIA_400-500_14.mzML" });

        Assert.True(filter.Matches("Ste-2024-12-02_HeLa_GPFDIA_400-500_14.raw"));
        Assert.True(filter.Matches("Ste-2024-12-02_HeLa_GPFDIA_400-500_14"));
        Assert.False(filter.Matches("Ste-2024-12-02_HeLa_GPFDIA_900-1000_22.raw"));
    }

    [Fact]
    public void MatchesShortReplicateNamesBySubstring()
    {
        // Skyline replicate names are often just the distinctive part of the file name.
        var filter = new RunNameFilter(new[] { "Ste-2024-12-02_HeLa_20msIIT_GPFDIA_400-500_14.mzML" });
        Assert.True(filter.Matches("400-500_14"));
    }

    [Fact]
    public void StripsCorrectedFileSuffixesSoOutputCanBeReQced()
    {
        // Re-running qc on {input}-mars.mzML has to match the same library rows.
        var filter = new RunNameFilter(new[] { "run_07-mars.mzML" });
        Assert.True(filter.Matches("run_07.raw"));
    }

    [Fact]
    public void AnEmptyFilterMatchesEverything()
    {
        var filter = new RunNameFilter(Array.Empty<string>());
        Assert.False(filter.Active);
        Assert.True(filter.Matches("anything at all"));
    }
}

public sealed class FragmentAnnotationTest
{
    [Theory]
    [InlineData("y7", 'y', 7)]
    [InlineData("b12", 'b', 12)]
    [InlineData("y5-H2O", 'y', 5)]
    [InlineData("precursor", '?', 0)]
    [InlineData("", '?', 0)]
    [InlineData("z3", 'z', 3)]
    public void ParsesSkylineFragmentAnnotations(string annotation, char expectedType, int expectedNumber)
    {
        (char ionType, int ionNumber) = PrismCsvLibraryReader.ParseFragmentIon(annotation);
        Assert.Equal(expectedType, ionType);
        Assert.Equal(expectedNumber, ionNumber);
    }
}
