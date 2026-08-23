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
        // An empty error array is not a mismatch: `mars qc` draws the report with no
        // after-correction series, and passes one.
        if (!IsPpm || error.Length == 0) return error;

        // Beyond that the two arrays have to describe the same rows, because each is converted
        // by its own m/z. Filling a short one with zeros would put 0 ppm into a QC figure for
        // every row past the end of it - which reads as a perfectly calibrated fragment.
        if (error.Length != mz.Length)
        {
            throw new ArgumentException(
                $"{error.Length:N0} error values and {mz.Length:N0} m/z values: per-row ppm " +
                "conversion needs one m/z per error.",
                nameof(mz));
        }

        var converted = new double[error.Length];
        for (int i = 0; i < error.Length; i++)
        {
            // A non-positive m/z cannot be converted and is left at zero rather than made
            // infinite; no real fragment has one.
            converted[i] = mz[i] > 0 ? error[i] / mz[i] * 1e6 : 0;
        }

        return converted;
    }

    /// <summary>Picks whichever of the two summaries is on this scale.</summary>
    public ErrorSummary? Pick(ErrorSummary? th, ErrorSummary? ppm) => IsPpm ? ppm ?? th : th;

    /// <summary>Picks whichever of the two fold measurements is on this scale.</summary>
    public FoldMetrics Pick(FoldMetrics th, FoldMetrics? ppm) => IsPpm && ppm is FoldMetrics p ? p : th;
}
