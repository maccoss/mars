// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MARS.Report;
using Xunit;

namespace MARS.Test;

public sealed class PngTest
{
    [Fact]
    public void ProducesAStructurallyValidPng()
    {
        const int width = 7, height = 5;
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);

        byte[] png = Png.Encode(pixels, width, height);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);

        var chunks = new List<(string Type, byte[] Data)>();
        int position = 8;
        while (position < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            string type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            byte[] data = png[(position + 8)..(position + 8 + length)];

            // A wrong CRC is the failure mode that produces a file every viewer rejects
            // while the bytes look plausible, so check it rather than trusting the writer.
            uint recorded = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(position + 4, 4 + length)), recorded);

            chunks.Add((type, data));
            position += 12 + length;
        }

        Assert.Equal(png.Length, position);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.ConvertAll(c => c.Type).ToArray());

        byte[] header = chunks[0].Data;
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0)));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(8, header[8]);
        Assert.Equal(2, header[9]);

        using var input = new MemoryStream(chunks[1].Data);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        byte[] scanlines = output.ToArray();

        Assert.Equal(height * ((width * 3) + 1), scanlines.Length);
        for (int y = 0; y < height; y++)
        {
            int offset = y * ((width * 3) + 1);
            Assert.Equal(0, scanlines[offset]); // filter type "none"
            Assert.Equal(
                pixels[(y * width * 3)..((y + 1) * width * 3)],
                scanlines[(offset + 1)..(offset + 1 + (width * 3))]);
        }
    }

    [Fact]
    public void RejectsAMismatchedPixelBuffer()
    {
        Assert.Throws<ArgumentException>(() => Png.Encode(new byte[10], 4, 4));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFF;
    }
}

public sealed class QcHtmlReportTest
{
    private static QcHtmlReport.Data BuildData(int rows = 400)
    {
        var before = new double[rows];
        var after = new double[rows];
        var rt = new double[rows];
        var mz = new double[rows];
        var feature = new double[rows];

        for (int i = 0; i < rows; i++)
        {
            // A deterministic wobble, so the figures have structure to draw rather than a
            // flat line, without needing a random source.
            double t = i / (double)rows;
            before[i] = (0.08 * Math.Sin(t * 6.0)) + (((i * 37) % 19) - 9) * 0.004;
            after[i] = before[i] * 0.4;
            rt[i] = t * 30.0;
            mz[i] = 300 + (t * 900);
            feature[i] = Math.Log10(1000 + (i * 17));
        }

        return new QcHtmlReport.Data
        {
            ErrorBefore = before,
            ErrorAfter = after,
            RetentionTime = rt,
            FragmentMz = mz,
            Features = new[] { ("log_intensity", feature) },
            ImportanceNames = new[] { "log_intensity" },
            Importance = new[] { 1.0 },
        };
    }

    private static string WriteReport(QcHtmlReport.Data data)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        QcHtmlReport.Write(
            path, data, statistics: null,
            new MARS.Core.MatchStatistics { SpectraSeen = 10, FragmentsMatched = 400, UniqueEntriesMatched = 5 },
            new[] { "run.mzML" }, "0.3 Th", "26.1.0");
        return path;
    }

    [Fact]
    public void IsSelfContained()
    {
        string path = WriteReport(BuildData());
        try
        {
            string html = File.ReadAllText(path);

            // The report exists to be emailed. Anything fetched at open time would render
            // as a broken box for the recipient, and most mail clients block it outright.
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Regex.Matches(html, "(?:src|href)=\"(?!data:|#)"));
            Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DrawsAFigureForEveryFeaturePlusTheFixedOnes()
    {
        var data = BuildData();
        string path = WriteReport(data);
        try
        {
            string html = File.ReadAllText(path);

            // One panel per feature, plus histogram, two heatmaps and importance.
            int expected = data.Features.Count + 4;
            Assert.Equal(expected, Regex.Matches(html, "<svg ").Count);
            Assert.Contains("log_intensity", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutAModelReportsTheMeasuredErrorAndClaimsNothingMore()
    {
        var data = BuildData();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        try
        {
            // The `mars qc` shape: matches and features, but no corrected error, no
            // importance, and no training statistics.
            var preCalibration = new QcHtmlReport.Data
            {
                ErrorBefore = data.ErrorBefore,
                ErrorAfter = Array.Empty<double>(),
                RetentionTime = data.RetentionTime,
                FragmentMz = data.FragmentMz,
                Features = data.Features,
                ImportanceNames = Array.Empty<string>(),
                Importance = Array.Empty<double>(),
            };

            QcHtmlReport.Write(
                path, preCalibration, statistics: null,
                new MARS.Core.MatchStatistics { SpectraSeen = 10, FragmentsMatched = 400 },
                new[] { "run.mzML" }, "0.3 Th", "26.1.0",
                MARS.Core.MarsStatistics.Summarize(data.ErrorBefore));

            string html = File.ReadAllText(path);

            Assert.Contains("Pre-calibration", html, StringComparison.Ordinal);
            Assert.Contains("median absolute error", html, StringComparison.Ordinal);

            // Nothing may imply a correction that was never computed. An "after" panel or an
            // importance chart here would be reporting a model that does not exist.
            Assert.DoesNotContain("After correction", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Feature importance", html, StringComparison.Ordinal);
            Assert.Contains("As measured", html, StringComparison.Ordinal);

            // Histogram, one heatmap, one panel per feature - and no second heatmap.
            Assert.Equal(preCalibration.Features.Count + 2, Regex.Matches(html, "<svg ").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HandlesAnEmptyMatchSetWithoutThrowing()
    {
        var empty = new QcHtmlReport.Data
        {
            ErrorBefore = Array.Empty<double>(),
            ErrorAfter = Array.Empty<double>(),
            RetentionTime = Array.Empty<double>(),
            FragmentMz = Array.Empty<double>(),
            Features = Array.Empty<(string, double[])>(),
            ImportanceNames = Array.Empty<string>(),
            Importance = Array.Empty<double>(),
        };

        string path = WriteReport(empty);
        try
        {
            // A run that matched nothing still has to produce a readable report saying so,
            // rather than an exception on the way out.
            Assert.Contains("No matched fragments.", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EscapesFileNamesRatherThanInjectingThem()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        try
        {
            // No slashes: Path.GetFileName would truncate at one before escaping ever
            // applied, so a name containing "</script>" would not test what it looks like
            // it tests. These characters all survive to the escaper.
            QcHtmlReport.Write(
                path, BuildData(50), statistics: null,
                new MARS.Core.MatchStatistics(),
                new[] { "<img onerror=\"x\"> & more.mzML" }, "0.3 Th", "26.1.0");

            string html = File.ReadAllText(path);
            Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;img onerror=&quot;x&quot;&gt; &amp; more.mzML", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
