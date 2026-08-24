// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>How precisely the analyzer that recorded the MS2 spectra measures m/z.</summary>
public enum MassAnalyzerClass
{
    /// <summary>Nothing in the file said, so MARS should not assume.</summary>
    Unknown,

    /// <summary>An ion trap or quadrupole: error is roughly constant in Th.</summary>
    UnitResolution,

    /// <summary>Orbitrap, FT-ICR, TOF, Astral: error is roughly constant in ppm.</summary>
    HighResolution,
}

/// <summary>
/// Classifies the mass analyzer that produced a run's MS2 spectra, from the CV accessions
/// mzML records for it.
/// </summary>
/// <remarks>
/// <para>
/// This decides two things a user otherwise has to know and pass by hand: whether the
/// fragment tolerance should default to Th or ppm, and which of those the QC report should
/// be drawn in. Getting it wrong is not a cosmetic problem. A 0.3 Th window is about 430 ppm
/// at m/z 700, so running the trap default against Astral data widens the window by two
/// orders of magnitude, and the extra width fills with wrong matches. The run still
/// completes and still reports numbers, which is what makes it worth detecting rather than
/// documenting.
/// </para>
/// <para>
/// By accession, not by name: names are display strings that differ between writers, and new
/// analyzers arrive faster than the writers agree on what to call them.
/// </para>
/// </remarks>
public static class MassAnalyzers
{
    public const string Quadrupole = "MS:1000081";
    public const string IonTrap = "MS:1000264";
    public const string QuadrupoleIonTrap = "MS:1000082";
    public const string RadialEjectionLinearIonTrap = "MS:1000083";
    public const string AxialEjectionLinearIonTrap = "MS:1000078";
    public const string LinearIonTrap = "MS:1000291";

    public const string Orbitrap = "MS:1000484";
    public const string FourierTransformIonCyclotronResonance = "MS:1000079";
    public const string TimeOfFlight = "MS:1000084";

    /// <summary>The Astral analyzer, added to the CV in 2023.</summary>
    public const string AsymmetricTrackLosslessTimeOfFlight = "MS:1003379";

    private static readonly HashSet<string> Unit = new(StringComparer.Ordinal)
    {
        Quadrupole, IonTrap, QuadrupoleIonTrap, RadialEjectionLinearIonTrap,
        AxialEjectionLinearIonTrap, LinearIonTrap,
    };

    private static readonly HashSet<string> HighResolution = new(StringComparer.Ordinal)
    {
        Orbitrap, FourierTransformIonCyclotronResonance, TimeOfFlight,
        AsymmetricTrackLosslessTimeOfFlight,
    };

    public static MassAnalyzerClass Classify(string? accession)
    {
        if (accession is null) return MassAnalyzerClass.Unknown;
        if (HighResolution.Contains(accession)) return MassAnalyzerClass.HighResolution;
        if (Unit.Contains(accession)) return MassAnalyzerClass.UnitResolution;
        return MassAnalyzerClass.Unknown;
    }

    /// <summary>
    /// Classifies from a Thermo filter string, for files whose instrument configuration is
    /// missing or unrecognized. <c>ITMS</c>, <c>FTMS</c> and <c>ASTMS</c> are the analyzer
    /// tokens Thermo writes at the front of every filter.
    /// </summary>
    public static MassAnalyzerClass ClassifyFilterString(string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return MassAnalyzerClass.Unknown;

        if (filter.StartsWith("ITMS", StringComparison.Ordinal)) return MassAnalyzerClass.UnitResolution;
        if (filter.StartsWith("FTMS", StringComparison.Ordinal) ||
            filter.StartsWith("ASTMS", StringComparison.Ordinal) ||
            filter.StartsWith("TOFMS", StringComparison.Ordinal))
        {
            return MassAnalyzerClass.HighResolution;
        }

        return MassAnalyzerClass.Unknown;
    }

    /// <summary>
    /// The analyzer a configuration measures with: the highest-order component. A
    /// configuration lists its analyzers in beam order, so an Astral configuration is
    /// quadrupole at order 2 and the Astral analyzer at order 3, and it is the last one that
    /// determines the mass accuracy.
    /// </summary>
    public static string? MeasuringAnalyzer(IReadOnlyList<(int Order, string Accession)> analyzers)
    {
        // Highest order wins, but a quadrupole only wins if there is nothing else: in a
        // configuration like the Astral's it is the isolating element rather than the
        // measuring one, and classifying on it would call the run unit-resolution.
        string? best = null;
        int bestOrder = int.MinValue;
        foreach ((int order, string accession) in analyzers)
        {
            bool preferable = best is null ||
                              (best == Quadrupole && accession != Quadrupole) ||
                              (order > bestOrder && !(accession == Quadrupole && best != Quadrupole));
            if (preferable) (best, bestOrder) = (accession, order);
        }

        return best;
    }

    public static string Describe(MassAnalyzerClass analyzer) => analyzer switch
    {
        MassAnalyzerClass.HighResolution => "high-resolution",
        MassAnalyzerClass.UnitResolution => "unit-resolution",
        _ => "unknown",
    };
}
