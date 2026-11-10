using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Measures feature importance as the increase in weighted MSE after permuting a feature column.
/// </summary>
/// <remarks>
///     Complexity: O(FeatureCount × SampleSize) forward passes. At 38 features × 200 samples = 7,600 forward passes on a 4-layer MLP.
/// </remarks>
internal static class NeuralFeatureImportance
{
    /// <summary>Default number of samples to use for importance estimation.</summary>
    internal const int DefaultSampleSize = 200;

    /// <summary>
    ///     Computes permutation importance from effective examples, keeping their labels and weights fixed.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="examples">Training examples providing realistic feature distributions.</param>
    /// <param name="sampleSize">Maximum samples to use, capped at the number of effective examples.</param>
    /// <param name="referenceTimeUtc">Optional fixed reference time for temporal weighting.</param>
    /// <returns>Feature names mapped to weighted MSE increases; positive values indicate useful features.</returns>
    internal static Dictionary<string, double> ComputePermutationImportance(
        NeuralScoringStrategy strategy,
        IReadOnlyList<TrainingExample> examples,
        int sampleSize = DefaultSampleSize,
        DateTime? referenceTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);

        var referenceTime = referenceTimeUtc ?? DateTime.UtcNow;
        examples = TrainingMath.FilterEffectiveExamples(examples, referenceTime, NeuralScoringStrategy.MinSampleWeight).Where(e => e.IsObserved).ToArray();
        var featureCount = CandidateFeatures.FeatureCount;
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

        var samples = new ImportanceSample[actualSampleSize];
        var imputationMeans = strategy.GetImputationMeans();
        for (var i = 0; i < actualSampleSize; i++)
        {
            var example = examples[indices[i]];
            samples[i] = new ImportanceSample(example.Features.ToModelVector(imputationMeans), example.Label, example.ComputeEffectiveWeight(referenceTime));
        }

        var baselineLoss = ComputeBaselineLoss(strategy, samples);

        var importance = new Dictionary<string, double>(featureCount);
        var shuffledVector = new double[featureCount];

        for (var f = 0; f < featureCount; f++)
        {
            var shuffledLoss = ComputeShuffledFeatureLoss(strategy, samples, shuffledVector, f, rng);
            importance[ScoringHelper.GetFeatureName((FeatureIndex)f)] = shuffledLoss - baselineLoss;
        }

        return importance;
    }

    /// <summary>
    ///     Computes baseline weighted MSE without allowing score-time normalization to mutate samples.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="samples">The sampled features, labels and effective weights.</param>
    /// <returns>The baseline weighted MSE.</returns>
    private static double ComputeBaselineLoss(
        NeuralScoringStrategy strategy,
        ImportanceSample[] samples)
    {
        var loss = 0.0;
        var totalWeight = 0.0;
        foreach (var sample in samples)
        {
            var error = strategy.ScoreVector((double[])sample.Vector.Clone()) - sample.Label;
            loss += sample.Weight * error * error;
            totalWeight += sample.Weight;
        }

        return loss / totalWeight;
    }

    /// <summary>
    ///     Permutes one column while keeping the original labels and effective weights attached to each sample.
    /// </summary>
    /// <param name="strategy">The trained neural scoring strategy to evaluate.</param>
    /// <param name="samples">The sampled features, labels and effective weights (left intact).</param>
    /// <param name="shuffledVector">Reusable scratch buffer for the substituted vector.</param>
    /// <param name="f">The index of the feature column being permuted.</param>
    /// <param name="rng">The (seeded) RNG driving the column shuffle.</param>
    /// <returns>The weighted MSE after permuting feature <paramref name="f"/>.</returns>
    private static double ComputeShuffledFeatureLoss(
        NeuralScoringStrategy strategy,
        ImportanceSample[] samples,
        double[] shuffledVector,
        int f,
        Random rng)
    {
        // Extract feature f values across all samples
        var featureValues = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            featureValues[i] = samples[i].Vector[f];
        }

        // Fisher-Yates shuffle of the feature column
        for (var i = featureValues.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (featureValues[i], featureValues[j]) = (featureValues[j], featureValues[i]);
        }

        var loss = 0.0;
        var totalWeight = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            Array.Copy(sample.Vector, shuffledVector, shuffledVector.Length);
            shuffledVector[f] = featureValues[i];

            // Every iteration overwrites the whole scratch vector before score-time standardization.
            var error = strategy.ScoreVector(shuffledVector) - sample.Label;
            loss += sample.Weight * error * error;
            totalWeight += sample.Weight;
        }

        return loss / totalWeight;
    }

    private readonly record struct ImportanceSample(double[] Vector, double Label, double Weight);
}
