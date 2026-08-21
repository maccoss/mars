// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.IO;
using System.Text.RegularExpressions;
using MARS.Core;
using MARS.Report;
using Xunit;

namespace MARS.Test;

public class ErrorScaleTest
{
    [Fact]
    public void PpmConversionUsesEachRowsOwnMz()
    {
        var error = new[] { 0.001, 0.001 };
        var mz = new[] { 500.0, 1000.0 };

        double[] ppm = ErrorScale.Ppm.Convert(error, mz);

        // The same absolute error is twice as many ppm at half the m/z. Dividing an aggregate
        // by one nominal mass would report these two rows as identical.
        Assert.Equal(2.0, ppm[0], 9);
        Assert.Equal(1.0, ppm[1], 9);
    }

    [Fact]
    public void AZeroMzDoesNotProduceInfinity()
    {
        double[] ppm = ErrorScale.Ppm.Convert(new[] { 0.001 }, new[] { 0.0 });

        Assert.Equal(0, ppm[0]);
        Assert.False(double.IsInfinity(ppm[0]) || double.IsNaN(ppm[0]));
    }

    [Fact]
    public void TheThScaleLeavesValuesAlone()
    {
        var error = new[] { 0.08, -0.04 };
        Assert.Same(error, ErrorScale.Th.Convert(error, new[] { 500.0, 500.0 }));
    }

    [Fact]
    public void EachScaleFormatsToThePrecisionItsNumbersLiveAt()
    {
        // 0.0445 Th is the interesting figure on trap data; 1.90 ppm on high-resolution data.
        // Two decimals would erase the first, four would pad the second with noise.
        Assert.Equal("0.0445", ErrorScale.Th.Format(0.04452));
        Assert.Equal("1.90", ErrorScale.Ppm.Format(1.9012));
    }

    /// <summary>
    /// The gap is out-of-fold minus in-sample: the correction does better on the data it was
    /// fitted to, so the figure reads positive. Written the other way round it renders with a
    /// minus sign and says the opposite of what it means.
    /// </summary>
    [Fact]
    public void TheGapIsPositiveWhenTheFitDoesBetterOnItsOwnData()
    {
        string html = WriteWithCrossValidation(ErrorScale.Th);

        Match gap = Regex.Match(html, @"Gap (-?[\d.]+) Th");
        Assert.True(gap.Success, "no gap figure in the report");
        Assert.Equal(0.0020, double.Parse(gap.Groups[1].Value), 4);
        Assert.Equal(
            CrossValidation().OptimismMad,
            double.Parse(gap.Groups[1].Value),
            4);
    }

    [Fact]
    public void AHighResolutionReportIsDrawnInPpmThroughout()
    {
        string html = WriteWithCrossValidation(ErrorScale.Ppm);

        Assert.Contains("mass error (ppm)", html, StringComparison.Ordinal);
        Assert.Contains("MAD (ppm)", html, StringComparison.Ordinal);
        Assert.Matches(@"Gap [\d.]+ ppm", html);

        // No stray Th axis or column left behind on a report that is meant to be in ppm.
        Assert.DoesNotContain("mass error (Th)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("MAD (Th)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ppm spread has to come from the per-fold ppm figures. Each fold converts at its own
    /// distribution of fragment m/z, so scaling the Th spread by any single factor is wrong.
    /// </summary>
    [Fact]
    public void ThePpmSpreadComesFromThePpmFolds()
    {
        string html = WriteWithCrossValidation(ErrorScale.Ppm);
        CrossValidationReport cv = CrossValidation();

        double expected = CrossValidationReport.Spread(cv.PerFoldPpm!, static f => f.Mad);
        Assert.Contains("+/-" + ErrorScale.Ppm.Format(expected), html, StringComparison.Ordinal);
        Assert.NotEqual(cv.MadSpread, expected, 6);
    }

    private static FoldMetrics Fold(double mad, double r) => new()
    {
        Rows = 100, Mad = mad, Rms = mad * 2, StdDev = mad * 2,
        Median = 0, PearsonR = r, MadBefore = mad * 2,
    };

    private static CrossValidationReport CrossValidation() => new()
    {
        Folds = 3,
        Groups = 60,
        PerFold = new[] { Fold(0.0440, 0.69), Fold(0.0450, 0.68), Fold(0.0460, 0.70) },
        OutOfFold = Fold(0.0450, 0.69),
        InSample = Fold(0.0430, 0.71),

        // Deliberately not a fixed multiple of the Th folds, so a report that derived ppm by
        // scaling the Th spread would disagree with these numbers.
        PerFoldPpm = new[] { Fold(1.80, 0.69), Fold(1.90, 0.68), Fold(2.30, 0.70) },
        OutOfFoldPpm = Fold(1.90, 0.69),
        InSamplePpm = Fold(1.80, 0.71),
    };

    private static string WriteWithCrossValidation(ErrorScale scale)
    {
        var rows = new double[600];
        var mz = new double[rows.Length];
        var after = new double[rows.Length];
        var rt = new double[rows.Length];
        var feature = new double[rows.Length];
        var random = new Random(7);
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = (random.NextDouble() - 0.5) * 0.3;
            after[i] = rows[i] * 0.5;
            mz[i] = 400 + (random.NextDouble() * 600);
            rt[i] = random.NextDouble() * 60;
            feature[i] = random.NextDouble() * 10;
        }

        var data = new QcHtmlReport.Data
        {
            ErrorBefore = rows,
            ErrorAfter = after,
            RetentionTime = rt,
            FragmentMz = mz,
            Features = new[] { ("log_intensity", feature) },
            ImportanceNames = new[] { "log_intensity" },
            Importance = new[] { 1.0 },
            CrossValidation = CrossValidation(),
        };

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".html");
        try
        {
            QcHtmlReport.Write(
                path, data, statistics: null,
                new MatchStatistics { SpectraSeen = 10, FragmentsMatched = 600 },
                new[] { "run.mzML" }, scale.IsPpm ? "10 ppm" : "0.3 Th", "26.1.0",
                uncorrected: null, scale);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
