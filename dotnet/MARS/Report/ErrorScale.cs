// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Globalization;
using MARS.Core;

namespace MARS.Report;

/// <summary>
/// The scale a QC report expresses mass error in, and how to render a number on it.
/// </summary>
/// <remarks>
/// Which one is right is a property of the instrument. A trap's error is roughly constant in
/// Th, so Th is the scale on which its figures are flat across the m/z range; a
/// high-resolution analyzer's error is roughly constant in ppm, and drawing it in Th produces
/// a fan that widens with m/z out of nothing but the choice of units. Four decimal places
/// suits Th, where the interesting digits are hundredths; two suit ppm, where they are ones.
/// </remarks>
public sealed class ErrorScale
{
    public static readonly ErrorScale Th = new("Th", "0.0000");

    public static readonly ErrorScale Ppm = new("ppm", "0.00");

    private ErrorScale(string unit, string numberFormat)
    {
        Unit = unit;
        NumberFormat = numberFormat;
    }

    public string Unit { get; }

    private string NumberFormat { get; }

    public bool IsPpm => ReferenceEquals(this, Ppm);

    public string Format(double value) => value.ToString(NumberFormat, CultureInfo.InvariantCulture);

    public string FormatSigned(double value) =>
        value.ToString("+" + NumberFormat + ";-" + NumberFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts per-row error to this scale, using each row's own m/z rather than an average
    /// - the fragments in one run span a wide enough m/z range that a single divisor would be
    /// wrong at both ends.
    /// </summary>
    public double[] Convert(double[] error, double[] mz)
    {
        if (!IsPpm || error.Length == 0) return error;

        var converted = new double[error.Length];
        for (int i = 0; i < error.Length; i++)
        {
            double denominator = i < mz.Length ? mz[i] : 0;
            converted[i] = denominator > 0 ? error[i] / denominator * 1e6 : 0;
        }

        return converted;
    }

    /// <summary>Picks whichever of the two summaries is on this scale.</summary>
    public ErrorSummary? Pick(ErrorSummary? th, ErrorSummary? ppm) => IsPpm ? ppm ?? th : th;

    /// <summary>Picks whichever of the two fold measurements is on this scale.</summary>
    public FoldMetrics Pick(FoldMetrics th, FoldMetrics? ppm) => IsPpm && ppm is FoldMetrics p ? p : th;
}
