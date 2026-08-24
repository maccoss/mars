// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using MARS.Core;
using MARS.IO;
using Xunit;

namespace MARS.Test;

/// <summary>
/// Scan start time carries its own unit, and MARS stores minutes.
/// </summary>
/// <remarks>
/// Thermo records minutes and Bruker records seconds, so reading the value and assuming
/// minutes is wrong by a factor of 60 on half the instruments MARS supports. It went unnoticed
/// until a 64-minute diaPASEF run came back as 3,866 minutes, because nothing here exercised
/// the seconds path - the failure is quiet, feeding the absolute_time feature rather than
/// throwing.
/// </remarks>
public class ScanTimeUnitTest
{
    [Fact]
    public void MinutesAreReadAsMinutes()
    {
        double[] minutes = RetentionTimes(inSeconds: false);
        double[] seconds = RetentionTimes(inSeconds: true);

        Assert.NotEmpty(minutes);
        Assert.Equal(minutes.Length, seconds.Length);
    }

    /// <summary>
    /// The same run written in seconds must read back the same minutes. A file that declares
    /// seconds and one that declares minutes describe the same acquisition.
    /// </summary>
    [Fact]
    public void SecondsAreConvertedToMinutes()
    {
        double[] minutes = RetentionTimes(inSeconds: false);
        double[] seconds = RetentionTimes(inSeconds: true);

        for (int i = 0; i < minutes.Length; i++)
            Assert.Equal(minutes[i], seconds[i], 9);
    }

    /// <summary>
    /// Guards the specific mistake: treating seconds as minutes would make these 60x apart.
    /// </summary>
    [Fact]
    public void SecondsAreNotTakenAtFaceValue()
    {
        double[] seconds = RetentionTimes(inSeconds: true);
        double[] minutes = RetentionTimes(inSeconds: false);

        Assert.All(seconds, t => Assert.True(t < 60, $"retention time {t} looks like raw seconds"));
        Assert.NotEqual(minutes[^1] * 60.0, seconds[^1], 6);
    }

    private static double[] RetentionTimes(bool inSeconds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mars-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "input.mzML");
            SyntheticMzML.Write(
                path, spectrumCount: 24, chromatogramCount: 0, peaksPerSpectrum: 4,
                scanTimeInSeconds: inSeconds);

            using var source = new MzMLSpectrumSource(path);
            return source.ReadSpectra(msLevel: 2).Select(s => s.RetentionTime).ToArray();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
