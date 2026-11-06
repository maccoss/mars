// Copyright (c) University of Washington 2026. Licensed under the MIT License.
//
// TestData/osprey-result.blib is 24 spectra cut from a real Osprey result library (Stellar
// HeLa, three runs): peaks are the search library's theoretical fragment m/z, the
// RefSpectraPeakAnnotations table is empty, and RetentionTimes carries each run's boundaries.
// osprey-result-observed.blib is the same cut with every peak moved by +0.25 Th, standing in
// for a library of observed unit-resolution spectra. Both are written by
// scripts/make_blib_fixture.py.

using System;
using System.Collections.Generic;
using System.IO;
using MARS.Core;
using MARS.IO;
using MARS.IO.Sqlite;
using Xunit;

namespace MARS.Test;

public sealed class BlibLibraryReaderTest
{
    private const string Run1 = "20261009_Stellar_WWopt_4Th_400-900mz_24min_profile_TR01.demux.centrix.mzML";

    private static string Fixture(string name) =>
        Path.Combine(TestPaths.SolutionDirectory, "MARS.Test", "TestData", name);

    [Fact]
    public void NamesTheoreticalPeaksByMz()
    {
        const string sequence = "PEPTCIDEK";
        var modifications = new List<(int, double)> { (5, 57.021464) };
        double[] mz =
        {
            PeptideMass.FragmentMz(sequence, 'y', 3, 1, modifications),
            PeptideMass.FragmentMz(sequence, 'b', 5, 1, modifications),
            PeptideMass.FragmentMz(sequence, 'y', 6, 2, modifications),
            612.3456,
        };

        var ions = new (char, int, int)[mz.Length];
        int named = FragmentAnnotator.Annotate(sequence, modifications, 2, mz, 2.0, ions);

        Assert.Equal(3, named);
        Assert.Equal(('y', 3, 1), ions[0]);
        Assert.Equal(('b', 5, 1), ions[1]);
        Assert.Equal(('y', 6, 2), ions[2]);
        Assert.Equal(FragmentAnnotator.Unassigned, ions[3]);
    }

    [Fact]
    public void DoesNotNameAPeakOutsideTheTolerance()
    {
        const string sequence = "PEPTIDEK";
        double y4 = PeptideMass.FragmentMz(sequence, 'y', 4, 1);
        double[] mz = { y4 * (1 + 5e-6) };

        var ions = new (char, int, int)[1];
        Assert.Equal(0, FragmentAnnotator.Annotate(sequence, null, 1, mz, 2.0, ions));

        // Without the modification the library applied, every fragment past it is off by the
        // delta and must go unnamed rather than be named as something else.
        double[] modified = { PeptideMass.FragmentMz(sequence, 'y', 4, 1, new List<(int, double)> { (6, 15.994915) }) };
        Assert.Equal(0, FragmentAnnotator.Annotate(sequence, null, 1, modified, 2.0, ions));
    }

    [Fact]
    public void DoesNotNameANonFinitePeakOrCandidate()
    {
        // NaN fails every comparison, including the one that rejects a peak outside the
        // tolerance, so without a guard a library of NaN peaks would pass as theoretical.
        const string sequence = "PEPTIDEK";
        double y4 = PeptideMass.FragmentMz(sequence, 'y', 4, 1);
        double[] mz = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, y4 };

        var ions = new (char, int, int)[mz.Length];
        Assert.Equal(1, FragmentAnnotator.Annotate(sequence, null, 1, mz, 2.0, ions));
        Assert.Equal(FragmentAnnotator.Unassigned, ions[0]);
        Assert.Equal(FragmentAnnotator.Unassigned, ions[1]);
        Assert.Equal(FragmentAnnotator.Unassigned, ions[2]);
        Assert.Equal(('y', 4, 1), ions[3]);

        // A non-finite modification delta makes non-finite candidates; inf > inf is false too.
        double[] finite = { y4, 1000.0 };
        var infinite = new List<(int, double)> { (6, double.PositiveInfinity) };
        Assert.Equal(0, FragmentAnnotator.Annotate(sequence, infinite, 1, finite, 2.0, ions));
        var nan = new List<(int, double)> { (6, double.NaN) };
        Assert.Equal(0, FragmentAnnotator.Annotate(sequence, nan, 1, finite, 2.0, ions));
    }

    [Fact]
    public void ReadsAnOspreyResultWithoutPeakAnnotations()
    {
        var log = new List<string>();
        SpectralLibrary library = BlibLibraryReader.Load(Fixture("osprey-result.blib"), log: log.Add);

        Assert.Equal(24, library.EntryCount);
        Assert.Equal(CountPeaks(Fixture("osprey-result.blib")), library.FragmentCount);
        foreach (byte ionType in library.FragmentIonType) Assert.True(ionType is (byte)'b' or (byte)'y');
        Assert.Contains(log, line => line.Contains("named as b or y ions", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAnUnannotatedLibraryOfObservedMz()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => BlibLibraryReader.Load(Fixture("osprey-result-observed.blib")));
        Assert.Contains("not theoretical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTheUnitResolutionExampleLibrary()
    {
        if (!TestPaths.TryFindExampleFile(Path.Combine("example-data", "Stellar-HeLa-GPF.blib"), out string path)) return;

        Assert.Throws<InvalidDataException>(() => BlibLibraryReader.Load(path));
    }

    [Fact]
    public void TakesElutionWindowsFromTheRunsBeingProcessed()
    {
        string path = Fixture("osprey-result.blib");
        List<long> ids = EntryIds(path);
        Dictionary<(long, long), (double Start, double End)> boundaries = RunBoundaries(path);

        SpectralLibrary one = BlibLibraryReader.Load(path, runNames: new[] { Run1 });
        for (var i = 0; i < ids.Count; i++)
        {
            (double start, double end) = boundaries[(ids[i], 1)];
            Assert.Equal(start, one.RtStart[i]);
            Assert.Equal(end, one.RtEnd[i]);
        }

        // With no runs named, the window spans every run's boundaries.
        SpectralLibrary all = BlibLibraryReader.Load(path);
        for (var i = 0; i < ids.Count; i++)
        {
            double start = double.MaxValue, end = double.MinValue;
            for (long source = 1; source <= 3; source++)
            {
                start = Math.Min(start, boundaries[(ids[i], source)].Start);
                end = Math.Max(end, boundaries[(ids[i], source)].End);
            }

            Assert.Equal(start, all.RtStart[i]);
            Assert.Equal(end, all.RtEnd[i]);
        }
    }

    [Fact]
    public void FallsBackToTheLibraryBoundariesForAnUnknownRun()
    {
        string path = Fixture("osprey-result.blib");
        var log = new List<string>();

        SpectralLibrary library = BlibLibraryReader.Load(path, log: log.Add, runNames: new[] { "some-other-run.mzML" });

        Assert.Contains(log, line => line.Contains("none of the library's 3 source files", StringComparison.Ordinal));
        for (var i = 0; i < library.EntryCount; i++)
        {
            Assert.True(library.RtEnd[i] > library.RtStart[i]);
            Assert.True(library.RtEnd[i] - library.RtStart[i] < 2.0);
        }
    }

    private static int CountPeaks(string path)
    {
        using var database = new SqliteFile(path);
        SqliteTable spectra = database.Table("RefSpectra");
        int numPeaksAt = spectra.ColumnIndex("numPeaks");
        var total = 0;
        foreach (SqliteRow row in database.Scan(spectra)) total += (int)row[numPeaksAt].AsInteger();
        return total;
    }

    /// <summary>RefSpectra ids in the order the reader builds entries: the peaks table's.</summary>
    private static List<long> EntryIds(string path)
    {
        using var database = new SqliteFile(path);
        SqliteTable peaks = database.Table("RefSpectraPeaks");
        int idAt = peaks.ColumnIndex("RefSpectraID");
        var ids = new List<long>();
        foreach (SqliteRow row in database.Scan(peaks)) ids.Add(row[idAt].AsInteger());
        return ids;
    }

    private static Dictionary<(long, long), (double, double)> RunBoundaries(string path)
    {
        using var database = new SqliteFile(path);
        SqliteTable times = database.Table("RetentionTimes");
        int idAt = times.ColumnIndex("RefSpectraID");
        int sourceAt = times.ColumnIndex("SpectrumSourceID");
        int startAt = times.ColumnIndex("startTime");
        int endAt = times.ColumnIndex("endTime");

        var result = new Dictionary<(long, long), (double, double)>();
        foreach (SqliteRow row in database.Scan(times))
        {
            result[(row[idAt].AsInteger(), row[sourceAt].AsInteger())] =
                (row[startAt].AsDouble(), row[endAt].AsDouble());
        }

        return result;
    }
}
