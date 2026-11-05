// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>What the anchor concluded about a run, beyond the shift itself.</summary>
public enum AnchorOutcome
{
    /// <summary>The anchor does not run in this configuration. See <see cref="MonoisotopicAnchor"/>.</summary>
    NotAttempted,

    /// <summary>Too few fragment targets carried a usable peak to estimate a shift from.</summary>
    TooFewTargets,

    /// <summary>A shift was found but is small enough that the window already contains the peak.</summary>
    WithinTolerance,

    /// <summary>A shift large enough to matter was found and will be applied.</summary>
    Applied,
}

/// <summary>Tuning for <see cref="MonoisotopicAnchor"/>. The defaults are for ion-trap peaks.</summary>
public sealed class AnchorOptions
{
    /// <summary>Largest shift the grid scan will consider, in Th, either side of zero.</summary>
    /// <remarks>
    /// Past about 1.3 Th the scan would start to find the M+2 of one fragment where the M+0
    /// of the next lies, and an offset that large is a broken instrument rather than a
    /// miscalibrated one.
    /// </remarks>
    public double MaxShiftTh { get; set; } = 1.3;

    /// <summary>Grid resolution of the scan, in Th.</summary>
    public double GridStepTh { get; set; } = 0.01;

    /// <summary>How close a peak must sit to a shifted target to count as a hit, in Th.</summary>
    public double HitToleranceTh { get; set; } = 0.15;

    /// <summary>Hard cap on spectra read, however few targets they yield.</summary>
    /// <remarks>
    /// The sample comes off the head of the run, and the head of a run is the worst part of
    /// it to sample: the gradient has not developed, and the library's own RT windows rule
    /// out every peptide that has not started eluting. On the Stellar reference run the
    /// first 20,000 MS2 spectra span RT 0 to 3.8 minutes while the earliest library peptide
    /// elutes at 2.3, so five sixths of that budget bought nothing and the sample came to
    /// 2,143 targets - barely above <see cref="MinTargets"/>.
    /// <para>
    /// So the sample is sized by what it collects rather than by what it reads:
    /// <see cref="MaxTargets"/> normally ends it, and this is the backstop for a run sparse
    /// enough that it never would. Reading on costs a fraction of the matching pass that
    /// follows, and buys the margin that makes the answer trustworthy on a thin library.
    /// </para>
    /// </remarks>
    public int MaxSpectra { get; set; } = 60000;

    /// <summary>Fragment targets below which the anchor declines to answer.</summary>
    /// <remarks>
    /// On the reference cohort 2,143 targets yielded 1,581 hits and an offset good to
    /// 0.007 Th, so this is the floor at which the answer is still worth having rather than
    /// a comfortable sample size.
    /// </remarks>
    public int MinTargets { get; set; } = 2000;

    /// <summary>Targets after which the sample stops growing, and what normally ends it.</summary>
    public int MaxTargets { get; set; } = 20000;

    /// <summary>
    /// A shift is applied only when it exceeds this fraction of the matching half-window.
    /// </summary>
    /// <remarks>
    /// Below this the monoisotopic peak is already inside the window and the model has
    /// always handled the rest. Shifting anyway would move the window for no gain and would
    /// change the output of every well-calibrated run that MARS has ever been checked
    /// against. Half a window is the point past which the peak starts leaving.
    /// </remarks>
    public double MinShiftFraction { get; set; } = 0.5;

    /// <summary>Most isotope steps the monoisotopic check will take.</summary>
    public int MaxIsotopeSteps { get; set; } = 3;

    /// <summary>Targets of a charge needed before the step-down check will trust that charge.</summary>
    public int MinStepDownTargets { get; set; } = 200;
}

/// <summary>What the anchor found in one run.</summary>
public sealed class AnchorResult
{
    /// <summary>The shift to centre the matching window on, in Th. Zero when none is applied.</summary>
    public double ShiftTh { get; init; }

    /// <summary>What the scan found, before the <see cref="AnchorOptions.MinShiftFraction"/> gate.</summary>
    public double ScannedShiftTh { get; init; }

    /// <summary>Isotope steps the monoisotopic check took, in units of one spacing.</summary>
    public int IsotopeSteps { get; init; }

    /// <summary>Charge whose isotope spacing the step-down used, or 0 when it did not step.</summary>
    public int StepDownCharge { get; init; }

    public int Targets { get; init; }

    public int TargetsHit { get; init; }

    public int SpectraExamined { get; init; }

    public AnchorOutcome Outcome { get; init; }

    public bool Applied => Outcome == AnchorOutcome.Applied;

    public static AnchorResult None(AnchorOutcome outcome) => new() { Outcome = outcome };
}

/// <summary>
/// Finds the coarse m/z shift that puts a run's fragment peaks inside the matching window,
/// and checks that the shift landed on the monoisotopic peak rather than an isotope of it.
/// </summary>
/// <remarks>
/// <para>
/// The matcher centres its window on the library m/z and takes the most intense peak inside.
/// That is sound while the instrument's error is small against the window. Once the error
/// approaches the window width the monoisotopic peak leaves the window, and what is left
/// inside is either nothing, noise, or - at the wrong offset - an isotope of the fragment.
/// An isotope match is the dangerous one: its residual is every bit as tight as a real
/// match, so the model trains happily, the QC report looks ordinary, and the correction is
/// a whole isotope spacing wrong.
/// </para>
/// <para>
/// A 1+ fragment's isotopes sit 1.00335 Th apart, so a 0.3 Th window admits the M+1 only at
/// an offset near -1 Th. Multiply charged fragments are the near case: 2+ isotopes are
/// 0.5017 Th apart, which puts the M+1 inside a 0.3 Th window at an offset of only -0.2 Th.
/// On the Stellar reference library, which is 38% doubly and 16% triply charged products,
/// a -0.25 Th offset already sends 18.5% of the 2+ matches to the M+1.
/// </para>
/// <para>
/// The shift found here only moves the window. The training label stays the full error,
/// observed minus library m/z, so the shift travels inside the model and the correction
/// path needs to know nothing about it. The gradient boosted model still learns everything
/// it learned before, including whatever m/z dependence the offset has - this supplies the
/// anchor the model cannot, because the model never sees a peak the matcher did not hand it.
/// </para>
/// <para>
/// This runs only for absolute (Th) tolerances; automatic anchoring is not supported for a
/// ppm window. A ppm window is far narrower than any isotope spacing - 10 ppm at m/z 500 is
/// 0.005 Th against 0.33 Th for a 3+ fragment - so an offset that grows gradually costs the
/// matches outright rather than drifting onto an isotope. But an offset within the window's
/// half-width of a whole isotope spacing, -1.00335/z Th, puts a z+ fragment's M+1 exactly on
/// the library m/z while its monoisotope sits outside, and those matches are tight and
/// centred enough that <see cref="WindowPlacement"/> can pass them. Nothing in MARS catches
/// that case; docs/algorithm.md says what it looks like and how to set the offset by hand.
/// </para>
/// </remarks>
public static class MonoisotopicAnchor
{
    /// <summary>13C - 12C, which is what separates a fragment's isotopes in mass.</summary>
    /// <remarks>
    /// The M+1 of a peptide fragment is overwhelmingly the single-13C isotopologue. Not to
    /// be confused with the ~1.00048 Da spacing of the Mann peptide mass rule, which is the
    /// average mass defect per nominal mass unit and is what the Comet and SEQUEST forbidden
    /// zones are built on.
    /// </remarks>
    public const double Carbon13Spacing = 1.00335483778;

    /// <summary>Charges the step-down check will take its vote from, in order of preference.</summary>
    private static readonly int[] StepDownCharges = { 1, 2 };

    /// <summary>
    /// Estimates the shift for one run, reading a bounded sample of its spectra.
    /// </summary>
    public static AnchorResult Estimate(
        ISpectrumSource source,
        SpectralLibrary library,
        MatchOptions matchOptions,
        AnchorOptions? anchorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(matchOptions);

        var options = anchorOptions ?? new AnchorOptions();

        // Ppm windows cannot admit an isotope; see the remarks on this class.
        if (matchOptions.TolerancePpm > 0) return AnchorResult.None(AnchorOutcome.NotAttempted);
        if (!(matchOptions.MzToleranceTh > 0)) return AnchorResult.None(AnchorOutcome.NotAttempted);

        var sample = Collect(source, library, matchOptions, options);
        if (sample.TargetCount < options.MinTargets)
        {
            return new AnchorResult
            {
                Outcome = AnchorOutcome.TooFewTargets,
                Targets = sample.TargetCount,
                SpectraExamined = sample.SpectraExamined,
            };
        }

        double shift = ScanForShift(sample, options);
        int steps = 0;
        int stepCharge = 0;

        while (steps < options.MaxIsotopeSteps &&
               ShouldStepDown(sample, shift, options, out double spacing, out int charge))
        {
            shift -= spacing;
            steps++;
            stepCharge = charge;
        }

        shift = Refine(sample, shift, options);

        int hits = CountTargetsHit(sample, shift, options.HitToleranceTh);
        bool apply = Math.Abs(shift) > options.MinShiftFraction * matchOptions.MzToleranceTh;

        return new AnchorResult
        {
            ShiftTh = apply ? shift : 0,
            ScannedShiftTh = shift,
            IsotopeSteps = steps,
            StepDownCharge = stepCharge,
            Targets = sample.TargetCount,
            TargetsHit = hits,
            SpectraExamined = sample.SpectraExamined,
            Outcome = apply ? AnchorOutcome.Applied : AnchorOutcome.WithinTolerance,
        };
    }

    /// <summary>
    /// Peaks near each candidate fragment, as offsets from the library m/z. One "target" is
    /// one library fragment in one spectrum that had at least one usable peak near it.
    /// </summary>
    /// <remarks>
    /// Offsets rather than m/z because the scan only ever asks how far a peak sits from its
    /// target, and because it makes every target comparable without carrying its m/z. A trap
    /// MS2 holds a few hundred peaks over a thousand Th, so a 2.9 Th reach around a target
    /// collects about one peak: the sample is far smaller than the spectra it came from.
    /// </remarks>
    internal sealed class Sample
    {
        public readonly List<float> Offset = new();
        public readonly List<float> Intensity = new();

        /// <summary>Start of each target's run in <see cref="Offset"/>, with a trailing sentinel.</summary>
        public readonly List<int> TargetStart = new();

        public readonly List<byte> TargetCharge = new();

        public int SpectraExamined;

        public int TargetCount => TargetStart.Count - 1;
    }

    private static Sample Collect(
        ISpectrumSource source, SpectralLibrary library, MatchOptions match, AnchorOptions options)
    {
        var sample = new Sample();
        int[] order = library.OrderByPrecursorMz();
        var sortedPrecursorMz = new double[order.Length];
        for (int i = 0; i < order.Length; i++) sortedPrecursorMz[i] = library.PrecursorMz[order[i]];

        double reach = options.MaxShiftTh + options.HitToleranceTh;

        foreach (SpectrumRecord spectrum in source.ReadSpectra(msLevel: 2))
        {
            sample.SpectraExamined++;

            if (sample.SpectraExamined >= options.MaxSpectra) break;
            if (sample.TargetCount >= options.MaxTargets) break;

            if (match.MaxIsolationWindowWidth is double maxWidth &&
                spectrum.IsolationWindowWidth > maxWidth)
            {
                continue;
            }

            if (match.MinRetentionTime is double minRt && spectrum.RetentionTime < minRt) continue;
            if (match.MaxRetentionTime is double maxRt && spectrum.RetentionTime > maxRt) continue;

            ReadOnlySpan<double> mz = spectrum.Mz;
            ReadOnlySpan<double> intensity = spectrum.Intensity;
            if (mz.Length == 0) continue;

            int first = PeakSearch.LowerBound(sortedPrecursorMz, spectrum.PrecursorMzLow);
            int last = PeakSearch.UpperBound(sortedPrecursorMz, spectrum.PrecursorMzHigh);

            for (int k = first; k < last; k++)
            {
                int entry = order[k];

                double rtStart = library.RtStart[entry];
                double rtEnd = library.RtEnd[entry];
                if (!double.IsNaN(rtStart) && !double.IsNaN(rtEnd) &&
                    (spectrum.RetentionTime < rtStart || spectrum.RetentionTime > rtEnd))
                {
                    continue;
                }

                int fragmentEnd = library.FragmentStart[entry + 1];
                for (int f = library.FragmentStart[entry]; f < fragmentEnd; f++)
                {
                    double expected = library.FragmentMz[f];
                    if (expected <= 0) continue;

                    int lo = PeakSearch.LowerBound(mz, expected - reach);
                    int hi = PeakSearch.UpperBound(mz, expected + reach);
                    if (lo >= hi) continue;

                    int before = sample.Offset.Count;
                    for (int i = lo; i < hi; i++)
                    {
                        if (match.MinIntensity > 0 && intensity[i] < match.MinIntensity) continue;
                        sample.Offset.Add((float)(mz[i] - expected));
                        sample.Intensity.Add((float)intensity[i]);
                    }

                    if (sample.Offset.Count == before) continue;

                    sample.TargetStart.Add(before);
                    sample.TargetCharge.Add(library.FragmentCharge[f]);
                }
            }
        }

        sample.TargetStart.Add(sample.Offset.Count);
        return sample;
    }

    /// <summary>
    /// The shift that puts the most fragment targets within <see cref="AnchorOptions.HitToleranceTh"/>
    /// of a peak. Ties go to the middle of the longest run, so a plateau answers with its centre.
    /// </summary>
    internal static double ScanForShift(Sample sample, AnchorOptions options)
    {
        int steps = (int)Math.Round(2 * options.MaxShiftTh / options.GridStepTh) + 1;
        var counts = new int[steps];
        for (int g = 0; g < steps; g++)
        {
            double shift = -options.MaxShiftTh + (g * options.GridStepTh);
            counts[g] = CountTargetsHit(sample, shift, options.HitToleranceTh);
        }

        int best = 0;
        for (int g = 1; g < steps; g++)
        {
            if (counts[g] > counts[best]) best = g;
        }

        // The longest contiguous run at the maximum, not merely the first. A flat top is the
        // normal shape when the hit tolerance is wider than the error, and its centre is the
        // better estimate than either edge.
        int bestStart = 0, bestLength = 0;
        int runStart = -1;
        for (int g = 0; g < steps; g++)
        {
            if (counts[g] == counts[best])
            {
                if (runStart < 0) runStart = g;
                int length = g - runStart + 1;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        double middle = bestStart + ((bestLength - 1) / 2.0);
        return -options.MaxShiftTh + (middle * options.GridStepTh);
    }

    /// <summary>
    /// Replaces a grid estimate with the median offset of the peaks it found.
    /// </summary>
    /// <remarks>
    /// The scan answers on a 0.01 Th grid, and its hit tolerance is wider than the error it
    /// is looking for, so the count has a flat top and the grid can only answer to the
    /// middle of it. That left the raw estimate about 0.02 Th low on the reference cohort -
    /// harmless inside a 0.3 Th window, but there is no reason to keep it. The peaks the
    /// scan found say where the population actually is; their median says it robustly,
    /// without the targets that hit on noise moving it.
    /// </remarks>
    internal static double Refine(Sample sample, double shift, AnchorOptions options)
    {
        var offsets = new List<double>();
        int targets = sample.TargetCount;
        for (int t = 0; t < targets; t++)
        {
            double best = double.NaN;
            float bestIntensity = float.NegativeInfinity;
            int end = sample.TargetStart[t + 1];
            for (int i = sample.TargetStart[t]; i < end; i++)
            {
                if (Math.Abs(sample.Offset[i] - shift) > options.HitToleranceTh) continue;
                if (sample.Intensity[i] <= bestIntensity) continue;
                bestIntensity = sample.Intensity[i];
                best = sample.Offset[i];
            }

            if (!double.IsNaN(best)) offsets.Add(best);
        }

        // Below a few hundred the median is noisier than the grid estimate it would replace,
        // so the grid estimate stands.
        const int enoughToRefine = 200;
        if (offsets.Count < enoughToRefine) return shift;

        offsets.Sort();
        return offsets[offsets.Count / 2];
    }

    internal static int CountTargetsHit(Sample sample, double shift, double hitTolerance)
    {
        int count = 0;
        int targets = sample.TargetCount;
        for (int t = 0; t < targets; t++)
        {
            int end = sample.TargetStart[t + 1];
            for (int i = sample.TargetStart[t]; i < end; i++)
            {
                if (Math.Abs(sample.Offset[i] - shift) <= hitTolerance)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// True when the shift looks like it landed on the M+1: most fragments that hit at the
    /// shift show a larger peak one isotope spacing below it.
    /// </summary>
    /// <remarks>
    /// Singly charged fragments decide it where there are enough of them. Their isotopes are
    /// a full 1.00335 Th apart, which a unit-resolution trap separates cleanly; a 2+
    /// fragment's 0.5017 Th spacing it often does not, so a 2+ vote is the fallback rather
    /// than the default. The test is on intensity, not presence, because the M+0 of a
    /// peptide fragment outweighs its M+1 over most of the mass range - and "most", not
    /// "all", because above roughly 1500 Da it stops doing so.
    /// </remarks>
    internal static bool ShouldStepDown(
        Sample sample, double shift, AnchorOptions options, out double spacing, out int charge)
    {
        foreach (int candidate in StepDownCharges)
        {
            spacing = Carbon13Spacing / candidate;
            int withHit = 0;
            int largerBelow = 0;

            int targets = sample.TargetCount;
            for (int t = 0; t < targets; t++)
            {
                if (sample.TargetCharge[t] != candidate) continue;

                float here = BestIntensityNear(sample, t, shift, options.HitToleranceTh);
                if (float.IsNaN(here)) continue;
                withHit++;

                float below = BestIntensityNear(sample, t, shift - spacing, options.HitToleranceTh);
                if (!float.IsNaN(below) && below > here) largerBelow++;
            }

            if (withHit < options.MinStepDownTargets) continue;

            charge = candidate;
            return largerBelow * 2 > withHit;
        }

        spacing = 0;
        charge = 0;
        return false;
    }

    /// <summary>Intensity of the most intense peak near an offset, or NaN when there is none.</summary>
    private static float BestIntensityNear(Sample sample, int target, double offset, double tolerance)
    {
        float best = float.NaN;
        int end = sample.TargetStart[target + 1];
        for (int i = sample.TargetStart[target]; i < end; i++)
        {
            if (Math.Abs(sample.Offset[i] - offset) > tolerance) continue;
            if (float.IsNaN(best) || sample.Intensity[i] > best) best = sample.Intensity[i];
        }

        return best;
    }

    /// <summary>One line for the run log.</summary>
    public static string Describe(AnchorResult result, MatchOptions options) => result.Outcome switch
    {
        AnchorOutcome.NotAttempted =>
            "  m/z anchor: not run (only absolute tolerances can admit an isotope)",
        AnchorOutcome.TooFewTargets =>
            $"  m/z anchor: only {result.Targets:N0} fragment targets in " +
            $"{result.SpectraExamined:N0} spectra; too few to estimate a shift",
        AnchorOutcome.WithinTolerance =>
            $"  m/z anchor: offset {result.ScannedShiftTh:+0.000;-0.000} Th, inside half of the " +
            $"{options.MzToleranceTh:0.###} Th window; matching unchanged",
        _ =>
            $"  m/z anchor: offset {result.ScannedShiftTh:+0.000;-0.000} Th" +
            (result.IsotopeSteps > 0
                ? $" (stepped down {result.IsotopeSteps} isotope{(result.IsotopeSteps > 1 ? "s" : "")} " +
                  $"from the {result.StepDownCharge}+ spacing)"
                : string.Empty) +
            $"; centring the window there, {result.TargetsHit:N0} of {result.Targets:N0} fragment " +
            $"targets hit in {result.SpectraExamined:N0} spectra",
    };
}
