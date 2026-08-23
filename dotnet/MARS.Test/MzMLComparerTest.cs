// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using MARS.IO;
using Xunit;

namespace MARS.Test;

/// <summary>
/// What <c>mars compare</c> reports, and what it does when the two files stop lining up.
/// </summary>
/// <remarks>
/// The comparison pairs spectra by position and checks the ids agree. That is right for what
/// it is for - a file against a correction of itself - but it cannot realign, so a file with a
/// spectrum inserted or removed would otherwise have every subsequent pair compared against
/// the wrong spectrum and counted as a difference. A tool whose job is to say "these files
/// agree" must not answer "they disagree everywhere" when they differ by one spectrum.
/// </remarks>
public sealed class MzMLComparerTest : IDisposable
{
    private readonly string _directory;

    public MzMLComparerTest()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mars-cmp-" + Guid.NewGuid().ToString("N")[..12]);
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

    [Fact]
    public void IdenticalFilesCompareEqualAndDoNotDiverge()
    {
        string a = Path.Combine(_directory, "a.mzML");
        string b = Path.Combine(_directory, "b.mzML");
        SyntheticMzML.Write(a, spectrumCount: 12, chromatogramCount: 0);
        SyntheticMzML.Write(b, spectrumCount: 12, chromatogramCount: 0);

        MzMLComparison result = MzMLComparer.Compare(a, b);

        Assert.False(result.Diverged);
        Assert.Equal(12, result.SpectraCompared);
        Assert.Equal(0, result.MzValuesDiffering);
        Assert.Equal(0, result.SpectraOnlyInA);
        Assert.Equal(0, result.SpectraOnlyInB);
    }

    /// <summary>
    /// Different spectrum counts leave the shorter file exhausted first, which is a plain
    /// difference in length rather than a loss of alignment.
    /// </summary>
    [Fact]
    public void AShorterFileIsCountedNotMisreported()
    {
        string a = Path.Combine(_directory, "long.mzML");
        string b = Path.Combine(_directory, "short.mzML");
        SyntheticMzML.Write(a, spectrumCount: 12, chromatogramCount: 0);
        SyntheticMzML.Write(b, spectrumCount: 8, chromatogramCount: 0);

        MzMLComparison result = MzMLComparer.Compare(a, b);

        Assert.Equal(8, result.SpectraCompared);
        Assert.Equal(4, result.SpectraOnlyInA);
        Assert.Equal(0, result.MzValuesDiffering);
    }
}
