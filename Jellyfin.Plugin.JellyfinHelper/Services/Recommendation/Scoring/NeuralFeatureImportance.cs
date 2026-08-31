using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Computes permutation-based feature importance for the neural scoring strategy. Permutation importance measures the actual score impact of each feature by shuffling its values across samples and measuring the resulting score degradation.
/// </summary>
/// <remarks>
///     Complexity: O(FeatureCount × SampleSize) forward passes. At 38 features × 200 samples = 7,600 forward passes on a 4-layer MLP.
/// </remarks>
internal static class NeuralFeatureImportance
{
    /// <summary>Default number of samples to use for importance estimation.</summary>
    internal const int DefaultSampleSize = 200;

    /// <summary>
    ///     Computes permutation importance for all features of the neural strategy. For each feature, shuffles its values across the sample set and measures the mean score drop compared to baseline (unshuffled) scores.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="examples">Training examples providing realistic feature distributions.</param>
    /// <param name="sampleSize">Maximum samples to use (capped at examples.Count).</param>
    /// <returns>Dictionary mapping feature name to importance drop (positive = important).</returns>
    internal static Dictionary<string, double> ComputePermutationImportance(
        NeuralScoringStrategy strategy,
        IReadOnlyList<TrainingExample> examples,
        int sampleSize = DefaultSampleSize)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);

        var featureCount = CandidateFeatures.FeatureCount;
        var featureNames = Enum.GetValues<FeatureIndex>().Distinct().Select(v => v.ToString()).ToArray();
        var actualSampleSize = Math.Min(sampleSize, examples.Count);

        if (actualSampleSize < 2)
        {
            return new Dictionary<string, double>();
        }

        // Select random sample (deterministic seed for reproducibility across runs)
        var rng = new Random(42);
        var indices = new int[examples.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        // Fisher-Yates partial shuffle: only need first actualSampleSize elements randomized
        for (var i = 0; i < actualSampleSize && i < indices.Length - 1; i++)
        {
            var j = rng.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        // Pre-compute feature vectors for selected samples
        var vectors = new double[actualSampleSize][];
        for (var i = 0; i < actualSampleSize; i++)
        {
            vectors[i] = examples[indices[i]].Features.ToVector();
        }

        // Compute baseline scores (each vector is cloned internally by ScoreVector's
        // standardization, but we keep originals intact for per-feature permutation)
        var baselineMean = ComputeBaselineMean(strategy, vectors, actualSampleSize);

        // For each feature, shuffle its column and measure mean score impact
        var importance = new Dictionary<string, double>(featureCount);
        var shuffledVector = new double[featureCount];

        for (var f = 0; f < featureCount; f++)
        {
            var shuffledMean = ComputeShuffledFeatureMean(
                strategy, vectors, shuffledVector, actualSampleSize, featureCount, f, rng);
            var featureName = f < featureNames.Length ? featureNames[f] : $"Feature{f}";
            importance[featureName] = baselineMean - shuffledMean;
        }

        return importance;
    }

    /// <summary>
    ///     Computes the mean baseline score over the sample set. Extracted verbatim from the baseline-score block of ComputePermutationImportance; each vector is cloned before scoring because ScoreVector mutates its input via standardization.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="vectors">The pre-computed sample feature vectors.</param>
    /// <param name="actualSampleSize">The number of samples to score.</param>
    /// <returns>The mean baseline (unshuffled) score.</returns>
    private static double ComputeBaselineMean(
        NeuralScoringStrategy strategy,
        double[][] vectors,
        int actualSampleSize)
    {
        var baselineMean = 0.0;
        for (var i = 0; i < actualSampleSize; i++)
        {
            var copy = (double[])vectors[i].Clone();
            baselineMean += strategy.ScoreVector(copy);
        }

        return baselineMean / actualSampleSize;
    }

    /// <summary>
    ///     Shuffles a single feature column across the sample set and returns the resulting mean score.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="vectors">The pre-computed sample feature vectors (left intact).</param>
    /// <param name="shuffledVector">Reusable scratch buffer for the substituted vector.</param>
    /// <param name="actualSampleSize">The number of samples to score.</param>
    /// <param name="featureCount">The number of features per vector.</param>
    /// <param name="f">The index of the feature column being permuted.</param>
    /// <param name="rng">The (seeded) RNG driving the column shuffle.</param>
    /// <returns>The mean score after permuting feature <paramref name="f"/>.</returns>
    private static double ComputeShuffledFeatureMean(
        NeuralScoringStrategy strategy,
        double[][] vectors,
        double[] shuffledVector,
        int actualSampleSize,
        int featureCount,
        int f,
        Random rng)
    {
        // Extract feature f values across all samples
        var featureValues = new double[actualSampleSize];
        for (var i = 0; i < actualSampleSize; i++)
        {
            featureValues[i] = vectors[i][f];
        }

        // Fisher-Yates shuffle of the feature column
        for (var i = featureValues.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (featureValues[i], featureValues[j]) = (featureValues[j], featureValues[i]);
        }

        // Score each sample with shuffled feature f
        var shuffledScoreSum = 0.0;
        for (var i = 0; i < actualSampleSize; i++)
        {
            // Copy original vector, replace only feature f with shuffled value
            Array.Copy(vectors[i], shuffledVector, featureCount);
            shuffledVector[f] = featureValues[i];

            // ScoreVector mutates its input (standardization), so pass a copy
            var scoringCopy = (double[])shuffledVector.Clone();
            shuffledScoreSum += strategy.ScoreVector(scoringCopy);
        }

        return shuffledScoreSum / actualSampleSize;
    }
}