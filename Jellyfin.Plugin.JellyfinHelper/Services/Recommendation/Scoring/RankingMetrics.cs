using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Ranking-based evaluation metrics for recommendation quality.
///     Unlike MSE which penalizes inexact score predictions, these metrics measure
///     what actually matters: whether the items the user likes appear in the top K results.
///     <para>
///     Standard practice: train with MSE/BCE (differentiable), evaluate with ranking metrics
///     (non-differentiable but directly measure recommendation quality).
///     </para>
/// </summary>
internal static class RankingMetrics
{
    /// <summary>Default K for top-K metrics. The plugin recommends ~20 items; K=10 tests the top half.</summary>
    internal const int DefaultK = 10;

    /// <summary>Default relevance threshold. Training labels > this are considered "relevant" (user liked/watched).</summary>
    internal const double DefaultRelevanceThreshold = 0.5;

    /// <summary>
    ///     Computes Precision@K: the fraction of the top-K predicted items that are actually relevant.
    ///     <para>
    ///     Formula: |{relevant items in top K}| / K.
    ///     </para>
    ///     <para>
    ///     Interpretation: "Of the K items we'd recommend, how many does the user actually like?"
    ///     A P@10 of 0.8 means 8 out of 10 top-ranked items are relevant.
    ///     </para>
    /// </summary>
    /// <param name="predictedScores">Model-predicted scores for each example.</param>
    /// <param name="labels">Ground-truth labels for each example.</param>
    /// <param name="k">Number of top items to consider.</param>
    /// <param name="relevanceThreshold">Label threshold above which an item is considered relevant.</param>
    /// <returns>Precision@K in [0, 1], or 0 if K is 0 or no examples.</returns>
    internal static double ComputePrecisionAtK(
        double[] predictedScores,
        double[] labels,
        int k = DefaultK,
        double relevanceThreshold = DefaultRelevanceThreshold)
    {
        if (predictedScores.Length == 0 || k <= 0)
        {
            return 0.0;
        }

        ValidateArrayLengths(predictedScores, labels);

        var effectiveK = Math.Min(k, predictedScores.Length);
        var topKIndices = GetTopKIndices(predictedScores, effectiveK);

        var relevantInTopK = 0;
        foreach (var idx in topKIndices)
        {
            if (labels[idx] > relevanceThreshold)
            {
                relevantInTopK++;
            }
        }

        return (double)relevantInTopK / effectiveK;
    }

    /// <summary>
    ///     Computes Recall@K: the fraction of all relevant items that appear in the top-K predictions.
    ///     <para>
    ///     Formula: |{relevant items in top K}| / |{all relevant items}|.
    ///     </para>
    ///     <para>
    ///     Interpretation: "Of all the items the user likes, how many did we surface in the top K?"
    ///     A R@10 of 0.65 means 65% of all relevant items appeared in the top 10.
    ///     </para>
    /// </summary>
    /// <param name="predictedScores">Model-predicted scores for each example.</param>
    /// <param name="labels">Ground-truth labels for each example.</param>
    /// <param name="k">Number of top items to consider.</param>
    /// <param name="relevanceThreshold">Label threshold above which an item is considered relevant.</param>
    /// <returns>Recall@K in [0, 1], or 0 if no relevant items exist.</returns>
    internal static double ComputeRecallAtK(
        double[] predictedScores,
        double[] labels,
        int k = DefaultK,
        double relevanceThreshold = DefaultRelevanceThreshold)
    {
        if (predictedScores.Length == 0 || k <= 0)
        {
            return 0.0;
        }

        ValidateArrayLengths(predictedScores, labels);

        var totalRelevant = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] > relevanceThreshold)
            {
                totalRelevant++;
            }
        }

        if (totalRelevant == 0)
        {
            return 0.0;
        }

        var effectiveK = Math.Min(k, predictedScores.Length);
        var topKIndices = GetTopKIndices(predictedScores, effectiveK);

        var relevantInTopK = 0;
        foreach (var idx in topKIndices)
        {
            if (labels[idx] > relevanceThreshold)
            {
                relevantInTopK++;
            }
        }

        return (double)relevantInTopK / totalRelevant;
    }

    /// <summary>
    ///     Computes NDCG@K (Normalized Discounted Cumulative Gain): measures ranking quality
    ///     by rewarding relevant items that appear earlier in the list.
    ///     <para>
    ///     Uses the label value directly as gain (not binary), so items with label 0.85
    ///     (fully watched) contribute more than items with label 0.5 (barely started).
    ///     </para>
    ///     <para>
    ///     Formula: DCG@K / IDCG@K where DCG = Σ (2^label - 1) / log₂(rank + 1).
    ///     </para>
    /// </summary>
    /// <param name="predictedScores">Model-predicted scores for each example.</param>
    /// <param name="labels">Ground-truth labels (gains) for each example.</param>
    /// <param name="k">Number of top items to consider.</param>
    /// <returns>NDCG@K in [0, 1], or 0 if no gain exists in the dataset.</returns>
    internal static double ComputeNdcgAtK(
        double[] predictedScores,
        double[] labels,
        int k = DefaultK)
    {
        if (predictedScores.Length == 0 || k <= 0)
        {
            return 0.0;
        }

        ValidateArrayLengths(predictedScores, labels);

        var effectiveK = Math.Min(k, predictedScores.Length);

        // DCG@K: sum of gains discounted by log position for the predicted ranking
        var topKIndices = GetTopKIndices(predictedScores, effectiveK);
        var dcg = 0.0;
        for (var rank = 0; rank < topKIndices.Length; rank++)
        {
            var gain = Math.Pow(2.0, labels[topKIndices[rank]]) - 1.0;
            dcg += gain / Math.Log2(rank + 2.0); // rank+2 because rank is 0-based, log₂(1) = 0
        }

        // IDCG@K: ideal DCG with labels sorted descending (best possible ranking)
        var sortedLabels = new double[labels.Length];
        Array.Copy(labels, sortedLabels, labels.Length);
        Array.Sort(sortedLabels);
        Array.Reverse(sortedLabels);

        var idcg = 0.0;
        for (var rank = 0; rank < effectiveK; rank++)
        {
            var gain = Math.Pow(2.0, sortedLabels[rank]) - 1.0;
            idcg += gain / Math.Log2(rank + 2.0);
        }

        return idcg > 0 ? dcg / idcg : 0.0;
    }

    /// <summary>
    ///     Computes all ranking metrics at once for a set of training examples scored by a strategy.
    ///     Convenience method that avoids recomputing predictions multiple times.
    /// </summary>
    /// <param name="examples">Training examples with features and labels.</param>
    /// <param name="strategy">The scoring strategy to evaluate.</param>
    /// <param name="k">Number of top items to consider.</param>
    /// <param name="relevanceThreshold">Label threshold for Precision/Recall.</param>
    /// <returns>A tuple of (Precision@K, Recall@K, NDCG@K).</returns>
    internal static (double PrecisionAtK, double RecallAtK, double NdcgAtK) ComputeAll(
        IReadOnlyList<TrainingExample> examples,
        IScoringStrategy strategy,
        int k = DefaultK,
        double relevanceThreshold = DefaultRelevanceThreshold)
    {
        if (examples.Count == 0)
        {
            return (0.0, 0.0, 0.0);
        }

        var predictions = new double[examples.Count];
        var labels = new double[examples.Count];

        for (var i = 0; i < examples.Count; i++)
        {
            predictions[i] = strategy.Score(examples[i].Features);
            labels[i] = examples[i].Label;
        }

        return (
            ComputePrecisionAtK(predictions, labels, k, relevanceThreshold),
            ComputeRecallAtK(predictions, labels, k, relevanceThreshold),
            ComputeNdcgAtK(predictions, labels, k));
    }

    /// <summary>
    ///     Computes all ranking metrics from pre-computed prediction and label arrays.
    ///     Used when predictions are already available (avoids re-scoring).
    /// </summary>
    /// <param name="predictions">Pre-computed predicted scores.</param>
    /// <param name="labels">Ground-truth labels.</param>
    /// <param name="k">Number of top items to consider.</param>
    /// <param name="relevanceThreshold">Label threshold for Precision/Recall.</param>
    /// <returns>A tuple of (Precision@K, Recall@K, NDCG@K).</returns>
    internal static (double PrecisionAtK, double RecallAtK, double NdcgAtK) ComputeAllFromArrays(
        double[] predictions,
        double[] labels,
        int k = DefaultK,
        double relevanceThreshold = DefaultRelevanceThreshold)
    {
        return (
            ComputePrecisionAtK(predictions, labels, k, relevanceThreshold),
            ComputeRecallAtK(predictions, labels, k, relevanceThreshold),
            ComputeNdcgAtK(predictions, labels, k));
    }

    /// <summary>
    ///     Validates that prediction and label arrays have matching lengths.
    /// </summary>
    private static void ValidateArrayLengths(double[] predictedScores, double[] labels)
    {
        if (labels.Length != predictedScores.Length)
        {
            throw new ArgumentException(
                $"labels length ({labels.Length}) must match predictedScores length ({predictedScores.Length}).",
                nameof(labels));
        }
    }

    /// <summary>
    ///     Returns the indices of the top-K elements by descending value.
    ///     Uses a full sort of all indices (O(N log N)); sufficient for typical
    ///     training set sizes. For very large N with K &lt;&lt; N, a min-heap
    ///     of size K would be more efficient.
    /// </summary>
    /// <param name="scores">The scores to rank.</param>
    /// <param name="k">Number of top indices to return.</param>
    /// <returns>Array of indices into <paramref name="scores"/> for the top-K elements, sorted by score descending.</returns>
    private static int[] GetTopKIndices(double[] scores, int k)
    {
        // For small arrays or large K relative to N, full sort is fine
        var indices = new int[scores.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        // Sort indices by score descending
        Array.Sort(indices, (a, b) => scores[b].CompareTo(scores[a]));

        // Return only top K
        var topK = new int[k];
        Array.Copy(indices, topK, k);
        return topK;
    }
}