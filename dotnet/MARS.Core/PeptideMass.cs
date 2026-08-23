// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Monoisotopic peptide fragment masses.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MARS.Core;

/// <summary>
/// Theoretical fragment m/z for peptide backbone ions.
/// <para>
/// This matters for BiblioSpec libraries specifically. A blib stores the OBSERVED m/z of
/// each reference peak, which carries whatever miscalibration the reference run had -- the
/// very thing MARS exists to remove. Using it as ground truth would teach the model to
/// reproduce that error, so b and y fragment m/z are recomputed from the sequence instead.
/// </para>
/// </summary>
public static class PeptideMass
{
    public const double Proton = 1.007276466;

    public const double Water = 18.0105646863;

    public const double Ammonia = 17.0265491015;

    public const double CarbonMonoxide = 27.9949146221;

    private static readonly double[] ResidueMass = BuildResidueTable();

    /// <summary>Monoisotopic residue mass, or NaN for an unknown symbol.</summary>
    public static double Residue(char aminoAcid)
    {
        int index = aminoAcid - 'A';
        return index >= 0 && index < ResidueMass.Length ? ResidueMass[index] : double.NaN;
    }

    public static bool IsKnownResidue(char aminoAcid) => !double.IsNaN(Residue(aminoAcid));

    /// <summary>
    /// m/z of a backbone fragment.
    /// </summary>
    /// <param name="sequence">Unmodified sequence, one letter per residue.</param>
    /// <param name="ionType">'b', 'y', 'a', 'c', 'x' or 'z'.</param>
    /// <param name="ionNumber">Residue count in the fragment, 1-based.</param>
    /// <param name="charge">Fragment charge, at least 1.</param>
    /// <param name="modifications">
    /// Mass deltas by 1-based residue position across the whole peptide. Only the deltas
    /// falling inside the fragment are applied.
    /// </param>
    /// <returns>The m/z, or NaN when the fragment cannot be computed.</returns>
    public static double FragmentMz(
        string sequence,
        char ionType,
        int ionNumber,
        int charge,
        IReadOnlyList<(int Position, double Mass)>? modifications = null)
    {
        if (charge < 1 || ionNumber < 1 || ionNumber > sequence.Length) return double.NaN;

        bool nTerminal = ionType is 'a' or 'b' or 'c';
        bool cTerminal = ionType is 'x' or 'y' or 'z';
        if (!nTerminal && !cTerminal) return double.NaN;

        int from, to; // 0-based, [from, to)
        if (nTerminal)
        {
            from = 0;
            to = ionNumber;
        }
        else
        {
            from = sequence.Length - ionNumber;
            to = sequence.Length;
        }

        double residues = 0;
        for (int i = from; i < to; i++)
        {
            double mass = Residue(sequence[i]);
            if (double.IsNaN(mass)) return double.NaN;
            residues += mass;
        }

        if (modifications is not null)
        {
            foreach ((int position, double mass) in modifications)
            {
                int index = position - 1;
                if (index >= from && index < to) residues += mass;
            }
        }

        // b is the residue sum; y adds the C-terminal water. The rest are offsets from those.
        double neutral = ionType switch
        {
            'b' => residues,
            'a' => residues - CarbonMonoxide,
            'c' => residues + Ammonia,
            'y' => residues + Water,
            'x' => residues + Water + CarbonMonoxide - (2 * 1.00782503207),
            'z' => residues + Water - Ammonia,
            _ => double.NaN,
        };

        if (double.IsNaN(neutral)) return double.NaN;
        return (neutral + (charge * Proton)) / charge;
    }

    /// <summary>
    /// Splits a modified sequence into its bare residues and the mass delta at each
    /// 1-based position, and reports how many modifications it could not weigh.
    /// </summary>
    /// <remarks>
    /// Only a numeric body carries its own mass: <c>M[+15.9949]</c> is a delta this can use,
    /// where <c>C[Carbamidomethyl (C)]</c> and <c>M(unimod:35)</c> are names that need a
    /// table this does not have. Those are counted rather than ignored. A dropped
    /// modification does not produce a missing answer, it produces a confident wrong one -
    /// the residue keeps its unmodified mass and every fragment past it is off by the delta -
    /// so a caller computing theoretical m/z has to know the difference.
    /// </remarks>
    public static (string Stripped, List<(int Position, double Mass)> Modifications, int Unweighed)
        SplitModifiedSequence(string sequence)
    {
        var stripped = new StringBuilder(sequence.Length);
        var modifications = new List<(int, double)>();
        var unweighed = 0;

        for (var i = 0; i < sequence.Length; i++)
        {
            char c = sequence[i];

            if (c is '[' or '(')
            {
                char closing = c == '[' ? ']' : ')';
                int end = sequence.IndexOf(closing, i + 1);
                if (end < 0) break;

                string body = sequence[(i + 1)..end];
                if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
                {
                    if (stripped.Length > 0) modifications.Add((stripped.Length, delta));
                }
                else
                {
                    unweighed++;
                }

                i = end;
                continue;
            }

            if (char.IsLetter(c)) stripped.Append(char.ToUpperInvariant(c));
        }

        return (stripped.ToString(), modifications, unweighed);
    }

    private static double[] BuildResidueTable()
    {
        var table = new double[26];
        for (var i = 0; i < table.Length; i++) table[i] = double.NaN;

        table['G' - 'A'] = 57.02146372;
        table['A' - 'A'] = 71.03711378;
        table['S' - 'A'] = 87.03202840;
        table['P' - 'A'] = 97.05276384;
        table['V' - 'A'] = 99.06841390;
        table['T' - 'A'] = 101.04767846;
        table['C' - 'A'] = 103.00918447;
        table['L' - 'A'] = 113.08406396;
        table['I' - 'A'] = 113.08406396;
        table['J' - 'A'] = 113.08406396;
        table['N' - 'A'] = 114.04292744;
        table['D' - 'A'] = 115.02694302;
        table['Q' - 'A'] = 128.05857750;
        table['K' - 'A'] = 128.09496301;
        table['E' - 'A'] = 129.04259308;
        table['M' - 'A'] = 131.04048508;
        table['H' - 'A'] = 137.05891186;
        table['F' - 'A'] = 147.06841390;
        table['R' - 'A'] = 156.10111102;
        table['Y' - 'A'] = 163.06332852;
        table['W' - 'A'] = 186.07931294;
        table['U' - 'A'] = 150.95363000;
        table['O' - 'A'] = 237.14772677;
        return table;
    }
}
