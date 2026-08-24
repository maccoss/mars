// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MARS.Core;
using MARS.IO;
using MARS.IO.Sqlite;
using MARS.Report;
using Xunit;

namespace MARS.Test;

/// <summary>
/// MARS must produce the same bytes on a machine whose locale writes decimals with a comma.
/// </summary>
/// <remarks>
/// <para>
/// This used to be guaranteed by <c>InvariantGlobalization</c>, which forces the whole runtime
/// to the invariant culture. Builds that carry a vendor reader have to relax it - the Thermo
/// SDK constructs <c>CultureInfo("en-US")</c> and throws when cultures are unavailable - which
/// hands <c>CurrentCulture</c> back to the operating system. These tests run under a
/// comma-decimal culture on purpose, so the guarantee is checked rather than assumed.
/// </para>
/// <para>
/// The failure mode is not cosmetic. A German <c>CurrentCulture</c> turns 653.835516 into
/// "653,835516" in an SVG coordinate or a JSON number, and turns the string "653.835516" read
/// out of a BiblioSpec library into 653835516.
/// </para>
/// </remarks>
public class CultureTest : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public CultureTest()
    {
        // German: decimal comma, point as the group separator - the arrangement most likely
        // to turn a correct number into a different correct-looking number.
        var german = new CultureInfo("de-DE");
        CultureInfo.CurrentCulture = german;
        CultureInfo.CurrentUICulture = german;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _original;
        CultureInfo.CurrentUICulture = _original;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Confirms the test is actually testing something: under this culture, formatting without
    /// an explicit provider really does produce a comma.
    /// </summary>
    [Fact]
    public void TheCultureUnderTestWouldBreakNumbers()
    {
        Assert.Equal("653,84", 653.835516.ToString("0.00"));
        Assert.True(double.TryParse("653.835516", out double parsed));
        Assert.NotEqual(653.835516, parsed);
    }

    /// <summary>
    /// SVG coordinates are read by a browser, not a person. A comma where a point belongs
    /// makes the attribute invalid and the figure blank.
    /// </summary>
    [Fact]
    public void TheQcReportWritesPointsNotCommas()
    {
        var data = BuildReportData();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        try
        {
            QcHtmlReport.Write(
                path, data, statistics: null,
                new MatchStatistics { SpectraSeen = 10, FragmentsMatched = 400 },
                new[] { "run.mzML" }, "0.3 Th", "26.1.0",
                MarsStatistics.Summarize(data.ErrorBefore));

            string html = File.ReadAllText(path);

            // Any attribute value holding a comma-decimal number, e.g. width="12,5".
            Match bad = Regex.Match(html, "=\"[-0-9]+,[0-9]+\"");
            Assert.False(bad.Success, $"comma-decimal number in the report: {bad.Value}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A model saved on one machine has to load on another. JSON numbers are point-decimal by
    /// specification, so a comma is not merely unusual - it is a different document.
    /// </summary>
    [Fact]
    public void AModelRoundTripsUnderAForeignCulture()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            MzCalibrator calibrator = TrainTiny();
            MarsModelIo.Save(calibrator, path);

            string json = File.ReadAllText(path);
            Assert.DoesNotContain(",\"", json.Replace("\",\"", string.Empty), StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(json, @":\s*-?\d+,\d+"),
                "a JSON number was written with a decimal comma");

            MzCalibrator loaded = MarsModelIo.Load(path);
            Assert.Equal(calibrator.Features.Count, loaded.Features.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The bug this whole class exists for. A BiblioSpec library can store a number as SQLite
    /// text, and that text is whatever wrote the library - nothing to do with the locale of
    /// the machine reading it. Parsed under a German culture, "653.835516" becomes
    /// 653,835,516: a fragment m/z six orders of magnitude wrong, and no error to show for it.
    /// </summary>
    [Fact]
    public void ANumberStoredAsTextInALibraryParsesTheSameEverywhere()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("653.835516");
        SqliteValue value = SqliteValue.FromText(bytes, 0, bytes.Length, System.Text.Encoding.UTF8);

        Assert.Equal(653.835516, value.AsDouble(), 6);
    }

    /// <summary>Integers stored as text are the same story, with the group separator.</summary>
    [Fact]
    public void AnIntegerStoredAsTextParsesTheSameEverywhere()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("1234567");
        SqliteValue value = SqliteValue.FromText(bytes, 0, bytes.Length, System.Text.Encoding.UTF8);

        Assert.Equal(1234567L, value.AsInteger());
    }

    private static QcHtmlReport.Data BuildReportData()
    {
        var random = new Random(11);
        var before = new double[400];
        var rt = new double[before.Length];
        var mz = new double[before.Length];
        var feature = new double[before.Length];
        for (int i = 0; i < before.Length; i++)
        {
            before[i] = (random.NextDouble() - 0.5) * 0.3;
            rt[i] = random.NextDouble() * 60;
            mz[i] = 400 + (random.NextDouble() * 600);
            feature[i] = random.NextDouble() * 10;
        }

        return new QcHtmlReport.Data
        {
            ErrorBefore = before,
            ErrorAfter = Array.Empty<double>(),
            RetentionTime = rt,
            FragmentMz = mz,
            Features = new[] { ("log_intensity", feature) },
            ImportanceNames = Array.Empty<string>(),
            Importance = Array.Empty<double>(),
        };
    }

    private static MzCalibrator TrainTiny()
    {
        var features = new[] { MarsFeature.FragmentMz, MarsFeature.LogIntensity };
        var table = new MatchTable(features);
        var random = new Random(5);

        for (var i = 0; i < 400; i++)
        {
            double fragmentMz = 300 + (random.NextDouble() * 700);
            double intensity = 500 + (random.NextDouble() * 100000);

            table.Set(MarsFeature.FragmentMz, fragmentMz);
            table.Set(MarsFeature.LogIntensity, Math.Log10(intensity));
            table.DeltaMz.Add((fragmentMz * 2.0e-5) + ((random.NextDouble() - 0.5) * 0.01));
            table.ObservedIntensity.Add(intensity);
            table.PeptideGroup.Add(i / 8);
            table.CommitRow();
        }

        return MzCalibrator.Fit(table, new CalibrationOptions { CvFolds = 0 }, absoluteTimeOffset: 0);
    }
}
