// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Collapses a set of fold models into one equivalent model.

using System;
using System.Collections.Generic;
using pwiz.Osprey.ML;

namespace MARS.Core;

/// <summary>
/// Merges cross-validation fold models into a single <see cref="GradientBoostedTrees"/>
/// that predicts exactly what averaging them would.
/// </summary>
/// <remarks>
/// <para>
/// Percolator's SVM path averages its fold <em>weight vectors</em> into one frozen model,
/// and applying that costs exactly what applying any single model costs: a linear model's
/// parameters superpose, so the average of K models is another model of the same size.
/// </para>
/// <para>
/// Trees do not superpose. But a boosted ensemble scores as
/// <c>baseScore + sum over trees of leaf(traverse(tree, x))</c>, which is linear in the
/// trees, so the average over K models can be written as one model:
/// </para>
/// <code>
/// (1/K) * sum_k [ base_k + sum_i tree_ki(x) ]
///     = mean(base_k) + sum over ALL trees of ( tree(x) / K )
/// </code>
/// <para>
/// So: take every tree from every fold model, divide each leaf value by K, and average the
/// base scores. That is one model, and it is not an approximation - the predictions are
/// identical to the last bit.
/// </para>
/// <para>
/// <b>What this does not do is make scoring cheaper.</b> The merged model holds K times as
/// many trees, and the cost of scoring is the number of trees traversed. Merging buys a
/// single model object, a simpler model file and a single scoring path; it does not buy
/// back the time. That asymmetry is the whole difference between averaging a linear model
/// and averaging a tree ensemble.
/// </para>
/// </remarks>
public static class EnsembleMerge
{
    public static GradientBoostedTrees Merge(IReadOnlyList<GradientBoostedTrees> models)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (models.Count == 0)
            throw new ArgumentException("Nothing to merge.", nameof(models));
        if (models.Count == 1) return models[0];

        var parts = new GbtModelData[models.Count];
        int nodeCount = 0, treeCount = 0;
        for (int k = 0; k < models.Count; k++)
        {
            parts[k] = models[k].ToModelData();
            nodeCount += parts[k].Feature.Length;
            treeCount += parts[k].TreeRoot.Length;

            if (parts[k].FeatureCount != parts[0].FeatureCount)
            {
                throw new ArgumentException(
                    $"Fold models disagree on feature count: {parts[0].FeatureCount} and " +
                    $"{parts[k].FeatureCount}.", nameof(models));
            }

            if (parts[k].Objective != parts[0].Objective)
            {
                throw new ArgumentException(
                    "Fold models disagree on objective; averaging them would be meaningless.",
                    nameof(models));
            }
        }

        var feature = new int[nodeCount];
        var threshold = new double[nodeCount];
        var left = new int[nodeCount];
        var right = new int[nodeCount];
        var leaf = new double[nodeCount];
        var treeRoot = new int[treeCount];

        double scale = 1.0 / models.Count;
        double baseScore = 0;
        int nodeAt = 0, treeAt = 0;

        foreach (GbtModelData part in parts)
        {
            baseScore += part.BaseScore * scale;

            // Left and Right are absolute node indices, so every child reference has to move
            // by the same amount the nodes did. A leaf is -1 in both and stays -1.
            int offset = nodeAt;
            for (int i = 0; i < part.Feature.Length; i++)
            {
                feature[nodeAt] = part.Feature[i];
                threshold[nodeAt] = part.Threshold[i];
                left[nodeAt] = part.Left[i] < 0 ? -1 : part.Left[i] + offset;
                right[nodeAt] = part.Right[i] < 0 ? -1 : part.Right[i] + offset;

                // Dividing the leaf values is what turns a sum over K models into their mean.
                leaf[nodeAt] = part.Leaf[i] * scale;
                nodeAt++;
            }

            foreach (int root in part.TreeRoot) treeRoot[treeAt++] = root + offset;
        }

        return GradientBoostedTrees.FromModelData(new GbtModelData
        {
            BaseScore = baseScore,
            FeatureCount = parts[0].FeatureCount,
            Objective = parts[0].Objective,
            Feature = feature,
            Threshold = threshold,
            Left = left,
            Right = right,
            Leaf = leaf,
            TreeRoot = treeRoot,
        });
    }
}
