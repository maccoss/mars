// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using MARS.Core;

namespace MARS.Cli;

/// <summary>
/// Decides whether a run is high-resolution or unit-resolution, and what that implies for
/// the fragment tolerance and the units the QC report is written in.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary - <c>unit</c>, <c>hram</c>, <c>auto</c> - is Osprey's, so that someone
/// moving between the two tools does not have to learn a second word for the same idea.
/// Where MARS differs is what <c>auto</c> means: in Osprey it selects the configured
/// defaults, here it reads the instrument out of the mzML. The file already knows, and a
/// user who has to be told to pass a flag is a user who will eventually forget to.
/// </para>
/// <para>
/// Detection sets defaults only. An explicit --tolerance or --tolerance-ppm always wins,
/// because detection can be wrong on a file MARS has not seen the shape of before, and the
/// person at the terminal can be sure in a way a heuristic cannot.
/// </para>
/// </remarks>
public sealed class ResolutionMode
{
    public const double DefaultToleranceTh = 0.3;
    public const double DefaultTolerancePpm = 10.0;

    private ResolutionMode()
    {
    }

    public MassAnalyzerClass Analyzer { get; private init; }

    /// <summary>True when the QC report should express mass error in ppm rather than Th.</summary>
    public bool ReportInPpm => Analyzer == MassAnalyzerClass.HighResolution;

    /// <summary>
    /// Reads --resolution, detects when it says auto, and fills in whichever tolerance the
    /// user did not give.
    /// </summary>
    /// <param name="options">Mutated in place with the tolerance defaults for the mode.</param>
    /// <param name="detected">
    /// What the input said its MS2 analyzer was. Each reader works this out for its own
    /// format - an mzML from its instrumentConfiguration, a vendor file from the SDK - so this
    /// takes the answer rather than reaching back into the file for it. Reading a .raw as if
    /// it were mzML is how this silently fell back to a trap tolerance on Astral data.
    /// </param>
    public static ResolutionMode Resolve(
        CommandLineArgs args, MassAnalyzerClass detected, MatchOptions options, Action<string> log)
    {
        bool toleranceGiven = args.Has("tolerance");
        bool ppmGiven = args.Has("tolerance-ppm");
        string requested = args.String("resolution")?.ToLowerInvariant() ?? "auto";

        MassAnalyzerClass analyzer = requested switch
        {
            "unit" => MassAnalyzerClass.UnitResolution,
            "hram" => MassAnalyzerClass.HighResolution,
            "auto" => detected,
            _ => throw new FormatException(
                $"Option --resolution expects unit, hram or auto, got '{requested}'."),
        };

        if (toleranceGiven || ppmGiven)
        {
            // Say what was detected even when it changes nothing, so a mismatch between the
            // instrument and the tolerance is visible in the log rather than only in the
            // results.
            if (analyzer != MassAnalyzerClass.Unknown)
                log($"  {MassAnalyzers.Describe(analyzer)} data; using the tolerance given on the command line");
            return new ResolutionMode { Analyzer = analyzer };
        }

        switch (analyzer)
        {
            case MassAnalyzerClass.HighResolution:
                options.TolerancePpm = DefaultTolerancePpm;
                options.MzToleranceTh = 0;
                log($"  high-resolution data; fragment tolerance {DefaultTolerancePpm:0.#} ppm " +
                    "(--tolerance or --tolerance-ppm to override)");
                break;

            case MassAnalyzerClass.UnitResolution:
                options.MzToleranceTh = DefaultToleranceTh;
                options.TolerancePpm = 0;
                log($"  unit-resolution data; fragment tolerance {DefaultToleranceTh:0.###} Th " +
                    "(--tolerance or --tolerance-ppm to override)");
                break;

            default:
                options.MzToleranceTh = DefaultToleranceTh;
                options.TolerancePpm = 0;
                // A warning rather than a silent default: this is the case where MARS is
                // guessing, and a 0.3 Th window on high-resolution data is wide enough to
                // fill with wrong matches while still producing a report that looks fine.
                log($"  WARNING: could not tell the mass analyzer from the file; assuming " +
                    $"unit resolution and a {DefaultToleranceTh:0.###} Th tolerance. Pass " +
                    "--resolution hram or --tolerance-ppm if this is Orbitrap, TOF or Astral data.");
                break;
        }

        return new ResolutionMode { Analyzer = analyzer };
    }
}
