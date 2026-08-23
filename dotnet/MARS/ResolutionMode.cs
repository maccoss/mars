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
    /// Warns when the matching window turns out to be far wider than the error in the data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tolerance that is too wide fails silently: the window fills with peaks that are not
    /// the fragment, the run completes, and the report looks ordinary. A tolerance that is too
    /// narrow fails loudly, with too few matches to train on. So the dangerous direction is the
    /// one worth checking for after the fact, and the data says which happened even when the
    /// file's metadata does not.
    /// </para>
    /// <para>
    /// This exists because instrument detection can come up empty. A ZenoTOF 8600 is the case
    /// that prompted it: pwiz does not yet recognise the model, so it emits an instrument
    /// configuration with no analyzer component and no filter string, MARS cannot tell what
    /// recorded the run, and the fallback is a 0.3 Th window - about 760 ppm at m/z 400 on an
    /// instrument whose real error is a few ppm.
    /// </para>
    /// <param name="observedMad">Median absolute deviation of the matched error, in Th.</param>
    /// </remarks>
    public static void WarnIfToleranceLooksTooWide(
        MatchOptions options, double observedMad, double medianFragmentMz, Action<string> log)
    {
        if (!(observedMad > 0) || !(medianFragmentMz > 0)) return;

        // The window in Th at a representative m/z, so ppm and Th tolerances compare.
        double window = options.TolerancePpm > 0
            ? options.TolerancePpm * 1e-6 * medianFragmentMz
            : options.MzToleranceTh;
        if (!(window > 0)) return;

        // Fifty is loose on purpose. Trap data sits around 4 - a 0.08 Th spread inside a 0.3 Th
        // window - so this cannot fire on the case MARS was built for. High-resolution data
        // matched at a trap tolerance lands in the hundreds.
        const double suspicious = 50.0;
        double ratio = window / observedMad;
        if (ratio < suspicious) return;

        log($"  WARNING: the matching window is {ratio:N0}x the error actually in the data "
            + $"({window:0.####} Th against a median absolute deviation of {observedMad:0.####} Th). "
            + "That is the signature of a tolerance set for the wrong instrument: a window this "
            + "wide admits peaks that are not the fragment, and the run will complete and report "
            + "numbers regardless. If this is high-resolution data, re-run with --resolution hram "
            + "or --tolerance-ppm.");
    }

    /// <summary>
    /// Reads --resolution, detects when it says auto, and fills in whichever tolerance the
    /// user did not give.
    /// </summary>
    /// <param name="options">Mutated in place with the tolerance defaults for the mode.</param>
    /// <summary>
    /// Registers the options this class reads, without acting on them.
    /// </summary>
    /// <remarks>
    /// <see cref="CommandLineArgs.RejectUnknown"/> learns that an option is real by watching it
    /// be read, and it runs before any work so a typo costs a second. The resolution cannot be
    /// decided that early - it needs the readers open to say what analyzer they found - so its
    /// options are declared here instead. Without this, `--resolution` is rejected as a typo,
    /// which is what happened.
    /// </remarks>
    public static void Touch(CommandLineArgs args)
    {
        args.Has("resolution");
        args.Has("tolerance");
        args.Has("tolerance-ppm");
    }

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
