// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Peptide-grouped k-fold cross-validation.

using System;
using System.Collections.Generic;
using pwiz.Osprey.ML;

namespace MARS.Core;

/// <summary>Accuracy of a set of predictions against their labels, all in Th.</summary>
public readonly struct FoldMetrics
{
    public required int Rows { get; init; }

    /// <summary>Median absolute residual.</summary>
    public required double Mad { get; init; }

    /// <summary>Root mean squared residual.</summary>
    public required double Rms { get; init; }

    /// <summary>Standard deviation of the residual.</summary>
    public required double StdDev { get; init; }

    /// <summary>Median residual: what is left of any constant bias.</summary>
    public required double Median { get; init; }

    /// <summary>Pearson correlation between predicted and observed error.</summary>
    public required double PearsonR { get; init; }

    /// <summary>Median absolute error before correction, for the same rows.</summary>
    public required double MadBefore { get; init; }

    /// <summary>Percent reduction in median absolute error.</summary>
    public double MadReduction => MadBefore > 0 ? 100.0 * (1.0 - (Mad / MadBefore)) : 0.0;
}

/// <summary>Cross-validation outcome: per-fold accuracy, and the spread across folds.</summary>
public sealed class CrossValidationReport
{
    public required int Folds { get; init; }

    public required int Groups { get; init; }

    public required FoldMetrics[] PerFold { get; init; }

    /// <summary>
    /// Metrics over every row's out-of-fold prediction pooled together. This is the honest
    /// headline number: every row was scored by a model that never saw its peptide.
    /// </summary>
    public required FoldMetrics OutOfFold { get; init; }

    /// <summary>
    /// The applied model scored on the rows it was fitted to. This is what the corrected
    /// files will look like when re-matched.
    /// </summary>
    public required FoldMetrics InSample { get; init; }

    /// <summary>Standard deviation across folds of the per-fold MAD.</summary>
    public double MadSpread => Spread(static m => m.Mad);

    public double RmsSpread => Spread(static m => m.Rms);

    public double PearsonRSpread => Spread(static m => m.PearsonR);

    public double MadReductionSpread => Spread(static m => m.MadReduction);

    /// <summary>
    /// How much better the correction is on the data it was fitted to than on data it was
    /// not.
    /// </summary>
    /// <remarks>
    /// Not a measure of cheating: calibrating a run from its own identified species is
    /// exactly how mass calibration works, and the correction moves a peak onto a fitted
    /// surface rather than onto its theoretical m/z, so there is little scope to memorize
    /// individual peaks. What a large gap does mean is that the surface is being driven by
    /// the particular peptides in this run rather than by the instrument, which says the fit
    /// is thin and that reusing this model elsewhere would disappoint.
    /// </remarks>
    public double OptimismMad => OutOfFold.Mad - InSample.Mad;

    private double Spread(Func<FoldMetrics, double> select)
    {
        if (PerFold.Length < 2) return double.NaN;

        double mean = 0;
        foreach (FoldMetrics fold in PerFold) mean += select(fold);
        mean /= PerFold.Length;

        double sumSquares = 0;
        foreach (FoldMetrics fold in PerFold)
        {
            double d = select(fold) - mean;
            sumSquares += d * d;
        }

        // Sample standard deviation: the folds are a sample of the splits that could have
        // been drawn, not the population of them.
        return Math.Sqrt(sumSquares / (PerFold.Length - 1));
    }
}

public static class PeptideFolds
{
    /// <summary>
    /// Assigns each row to a fold, keeping every row of a peptide in the same fold.
    /// </summary>
    /// <remarks>
    /// Grouping is the whole point. Fragments of one peptide recur across hundreds of
    /// spectra with the same theoretical m/z, and <c>fragment_mz</c> is a model feature, so
    /// splitting a peptide across the train/test boundary lets the model memorize that
    /// peptide's error rather than learn the instrument's. A row-random split reports a
    /// validation error that is far better than the model will achieve on a peptide it has
    /// never seen.
    /// <para>
    /// Groups are sorted and dealt round-robin rather than shuffled with a PRNG. That is
    /// what Osprey's Percolator implementation does
    /// (<c>PercolatorSampling.CreateStratifiedFoldsByPeptide</c>), it is deterministic
    /// without needing a seed, and it balances the number of groups per fold exactly.
    /// </para>
    /// </remarks>
    /// <param name="groupOfRow">Peptide group id per row.</param>
    /// <param name="folds">Number of folds. Must be at least 2.</param>
    /// <returns>Fold index per row, and the number of distinct groups.</returns>
    public static (int[] FoldOfRow, int GroupCount) AssignFolds(ReadOnlySpan<int> groupOfRow, int folds)
    {
        if (folds < 2) throw new ArgumentOutOfRangeException(nameof(folds), folds, "At least 2 folds are required.");

        var rowsByGroup = new Dictionary<int, List<int>>();
        for (int i = 0; i < groupOfRow.Length; i++)
        {
            if (!rowsByGroup.TryGetValue(groupOfRow[i], out List<int>? rows))
            {
                rows = new List<int>();
                rowsByGroup[groupOfRow[i]] = rows;
            }

            rows.Add(i);
        }

        var groups = new int[rowsByGroup.Count];
        rowsByGroup.Keys.CopyTo(groups, 0);
        Array.Sort(groups);

        var foldOfRow = new int[groupOfRow.Length];
        for (int g = 0; g < groups.Length; g++)
        {
            int fold = g % folds;
            foreach (int row in rowsByGroup[groups[g]]) foldOfRow[row] = fold;
        }

        return (foldOfRow, groups.Length);
    }

    /// <summary>
    /// Splits rows into train and held-out sets by peptide, for the single-fit path.
    /// Returns the held-out set as close to <paramref name="fraction"/> of rows as whole
    /// groups allow.
    /// </summary>
    public static (int[] Train, int[] Validation) SplitByGroup(
        ReadOnlySpan<int> groupOfRow, double fraction, int seed)
    {
        if (fraction <= 0)
        {
            var all = new int[groupOfRow.Length];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return (all, Array.Empty<int>());
        }

        var rowsByGroup = new Dictionary<int, List<int>>();
        for (int i = 0; i < groupOfRow.Length; i++)
        {
            if (!rowsByGroup.TryGetValue(groupOfRow[i], out List<int>? rows))
            {
                rows = new List<int>();
                rowsByGroup[groupOfRow[i]] = rows;
            }

            rows.Add(i);
        }

        var groups = new int[rowsByGroup.Count];
        rowsByGroup.Keys.CopyTo(groups, 0);
        Array.Sort(groups);

        // Shuffled rather than dealt, because unlike k-fold this takes a prefix, and a
        // sorted prefix would systematically select whichever peptides happen to sort first.
        var rng = new XorShift64((ulong)seed);
        for (int i = groups.Length - 1; i > 0; i--)
        {
            int j = (int)(rng.Next() % (ulong)(i + 1));
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }

        var validation = new List<int>();
        int target = (int)Math.Round(groupOfRow.Length * fraction);
        int taken = 0;
        int consumed = 0;
        while (consumed < groups.Length - 1 && taken < target)
        {
            List<int> rows = rowsByGroup[groups[consumed]];
            validation.AddRange(rows);
            taken += rows.Count;
            consumed++;
        }

        var train = new List<int>(groupOfRow.Length - taken);
        for (int g = consumed; g < groups.Length; g++) train.AddRange(rowsByGroup[groups[g]]);

        // Ascending row order on both sides, so downstream float accumulation depends on the
        // data rather than on the shuffle.
        int[] trainArray = train.ToArray();
        int[] validationArray = validation.ToArray();
        Array.Sort(trainArray);
        Array.Sort(validationArray);
        return (trainArray, validationArray);
    }

    /// <summary>Accuracy of <paramref name="predicted"/> against <paramref name="observed"/>.</summary>
    public static FoldMetrics Measure(ReadOnlySpan<double> observed, ReadOnlySpan<double> predicted)
    {
        int n = observed.Length;
        if (n == 0)
        {
            return new FoldMetrics
            {
                Rows = 0, Mad = double.NaN, Rms = double.NaN, StdDev = double.NaN,
                Median = double.NaN, PearsonR = double.NaN, MadBefore = double.NaN,
            };
        }

        var residual = new double[n];
        double sumSquares = 0;
        for (int i = 0; i < n; i++)
        {
            residual[i] = observed[i] - predicted[i];
            sumSquares += residual[i] * residual[i];
        }

        ErrorSummary after = MarsStatistics.Summarize(residual);
        ErrorSummary before = MarsStatistics.Summarize(observed);

        return new FoldMetrics
        {
            Rows = n,
            Mad = after.Mad,
            Rms = Math.Sqrt(sumSquares / n),
            StdDev = after.StdDev,
            Median = after.Median,
            PearsonR = Pearson(observed, predicted),
            MadBefore = before.Mad,
        };
    }

    private static double Pearson(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        int n = a.Length;
        if (n < 2) return double.NaN;

        double meanA = 0, meanB = 0;
        for (int i = 0; i < n; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }

        meanA /= n;
        meanB /= n;

        double covariance = 0, varianceA = 0, varianceB = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        double denominator = Math.Sqrt(varianceA * varianceB);
        // A constant prediction has no variance to correlate with. That is a real outcome -
        // a model that learned nothing - so report it as undefined rather than as zero.
        return denominator > 0 ? covariance / denominator : double.NaN;
    }
}
