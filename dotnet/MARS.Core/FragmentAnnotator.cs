// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace MARS.Core;

/// <summary>
/// Names the peaks of a library spectrum by m/z alone, for a library that stores theoretical
/// fragment m/z but no annotation saying which ion each one is.
/// </summary>
/// <remarks>
/// Osprey writes its result .blib this way: every peak is a fragment m/z copied from the
/// search library, and <c>RefSpectraPeakAnnotations</c> is left empty. Those m/z are
/// computed, not measured, so each lands on a b or y ion computed from the sequence to well
/// under a ppm, and the identity is not in doubt. The same test applied to a library of
/// OBSERVED spectra finds almost nothing - on the unit-resolution example library 0.02% of
/// peaks fall within 2 ppm - which is what lets a caller tell the two kinds apart rather than
/// trusting either blindly.
/// </remarks>
public static class FragmentAnnotator
{
    /// <summary>The ion a peak was identified as; '?' when it matched none.</summary>
    public static readonly (char IonType, int IonNumber, int Charge) Unassigned = ('?', 0, 1);

    /// <summary>
    /// Identifies each m/z as the nearest b or y ion within <paramref name="tolerancePpm"/>.
    /// </summary>
    /// <param name="sequence">Unmodified sequence, one letter per residue.</param>
    /// <param name="modifications">Mass deltas by 1-based residue position.</param>
    /// <param name="maxCharge">Highest fragment charge to consider, at least 1.</param>
    /// <param name="mz">Peak m/z values, in any order.</param>
    /// <param name="tolerancePpm">How far a peak may sit from the computed ion.</param>
    /// <param name="ions">Receives one entry per peak; <see cref="Unassigned"/> when none matched.</param>
    /// <returns>The number of peaks identified.</returns>
    public static int Annotate(
        string sequence,
        IReadOnlyList<(int Position, double Mass)>? modifications,
        int maxCharge,
        ReadOnlySpan<double> mz,
        double tolerancePpm,
        Span<(char IonType, int IonNumber, int Charge)> ions)
    {
        if (ions.Length < mz.Length) throw new ArgumentException("One ion slot is needed per peak.", nameof(ions));
        ions[..mz.Length].Fill(Unassigned);

        List<(double Mz, char IonType, int IonNumber, int Charge)>? candidates =
            Candidates(sequence, modifications, Math.Max(1, maxCharge));
        if (candidates is null || candidates.Count == 0) return 0;

        var assigned = 0;
        for (var i = 0; i < mz.Length; i++)
        {
            int nearest = Nearest(candidates, mz[i]);
            (double candidateMz, char ionType, int ionNumber, int charge) = candidates[nearest];
            if (Math.Abs(mz[i] - candidateMz) > candidateMz * tolerancePpm * 1e-6) continue;

            ions[i] = (ionType, ionNumber, charge);
            assigned++;
        }

        return assigned;
    }

    /// <summary>
    /// Every b and y ion of the peptide, sorted by m/z, or null when a residue has no known
    /// mass.
    /// </summary>
    private static List<(double, char, int, int)>? Candidates(
        string sequence,
        IReadOnlyList<(int Position, double Mass)>? modifications,
        int maxCharge)
    {
        int n = sequence.Length;
        if (n < 2) return null;

        var residues = new double[n];
        for (var i = 0; i < n; i++)
        {
            residues[i] = PeptideMass.Residue(sequence[i]);
            if (double.IsNaN(residues[i])) return null;
        }

        // Positions outside the sequence are ignored, as PeptideMass.FragmentMz ignores them.
        if (modifications is not null)
        {
            foreach ((int position, double mass) in modifications)
            {
                int index = position - 1;
                if (index >= 0 && index < n) residues[index] += mass;
            }
        }

        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + residues[i];

        var candidates = new List<(double, char, int, int)>(2 * (n - 1) * maxCharge);
        for (var ionNumber = 1; ionNumber < n; ionNumber++)
        {
            double b = prefix[ionNumber];
            double y = prefix[n] - prefix[n - ionNumber] + PeptideMass.Water;
            for (var charge = 1; charge <= maxCharge; charge++)
            {
                candidates.Add(((b + (charge * PeptideMass.Proton)) / charge, 'b', ionNumber, charge));
                candidates.Add(((y + (charge * PeptideMass.Proton)) / charge, 'y', ionNumber, charge));
            }
        }

        candidates.Sort((a, c) => a.Item1.CompareTo(c.Item1));
        return candidates;
    }

    private static int Nearest(List<(double Mz, char, int, int)> candidates, double mz)
    {
        int lo = 0, hi = candidates.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (candidates[mid].Mz < mz) lo = mid + 1;
            else hi = mid;
        }

        // lo is the first candidate at or above mz; the one below may be closer.
        if (lo > 0 && mz - candidates[lo - 1].Mz < candidates[lo].Mz - mz) return lo - 1;
        return lo;
    }
}
