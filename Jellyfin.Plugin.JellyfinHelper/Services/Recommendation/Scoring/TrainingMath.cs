using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Shared training eligibility, validation and normalization invariants.</summary>
internal static class TrainingMath
{
    /// <summary>Excludes examples that cannot produce an optimizer update before counting or normalizing them.</summary>
    /// <param name="examples">Candidate training examples.</param>
    /// <param name="referenceTime">The fixed time used for temporal weights.</param>
    /// <param name="minimumWeight">The optimizer's eligibility floor.</param>
    /// <returns>The eligible examples in their original order.</returns>
    internal static IReadOnlyList<TrainingExample> FilterEffectiveExamples(
        IReadOnlyList<TrainingExample> examples, DateTime referenceTime, double minimumWeight)
    {
        var eligible = new List<TrainingExample>(examples.Count);
        foreach (var example in examples)
        {
            if (example.ComputeEffectiveWeight(referenceTime) >= minimumWeight)
            {
                eligible.Add(example);
            }
        }

        return eligible;
    }

    /// <summary>An explicitly supplied holdout is authoritative, including when its weight is insufficient.</summary>
    /// <param name="holdout">Independent validation examples, or null when unavailable.</param>
    /// <param name="strategy">The fitted strategy.</param>
    /// <param name="referenceTime">The fixed time used for temporal weights.</param>
    /// <param name="minimumWeight">The eligibility floor.</param>
    /// <param name="fallback">The available internal validation estimate.</param>
    /// <returns>Weighted held-out MSE, an internal estimate, or NaN when validation is unavailable.</returns>
    internal static double ValidationLoss(
        IReadOnlyList<TrainingExample>? holdout,
        IScoringStrategy strategy,
        DateTime referenceTime,
        double minimumWeight,
        double fallback)
    {
        if (holdout is null)
        {
            return fallback;
        }

        var count = 0;
        var loss = 0.0;
        var totalWeight = 0.0;
        foreach (var example in holdout)
        {
            var weight = example.ComputeEffectiveWeight(referenceTime);
            if (weight < minimumWeight)
            {
                continue;
            }

            var error = strategy.Score(example.Features) - example.Label;
            loss += weight * error * error;
            totalWeight += weight;
            count++;
        }

        return count >= 2 && totalWeight > 0 ? loss / totalWeight : double.NaN;
    }

    /// <summary>Preserves a dense layer's preactivations when its input normalization changes.</summary>
    /// <param name="weights">Row-major input weights.</param>
    /// <param name="biases">One bias per output neuron.</param>
    /// <param name="oldMeans">Previous means, or null for raw inputs.</param>
    /// <param name="oldStdDevs">Previous standard deviations, or null for raw inputs.</param>
    /// <param name="newMeans">New means, or null for raw inputs.</param>
    /// <param name="newStdDevs">New standard deviations, or null for raw inputs.</param>
    /// <returns>Whether the input coordinate system changed.</returns>
    internal static bool RebaseInputLayer(
        double[] weights,
        double[] biases,
        double[]? oldMeans,
        double[]? oldStdDevs,
        double[]? newMeans,
        double[]? newStdDevs)
    {
        var changed = false;
        for (var f = 0; f < CandidateFeatures.FeatureCount; f++)
        {
            var oldScale = InputScale(oldStdDevs, f);
            var newScale = InputScale(newStdDevs, f);
            var coefficient = oldScale / newScale;
            var offset = InputOffset(oldMeans, oldStdDevs, f) -
                (coefficient * InputOffset(newMeans, newStdDevs, f));
            if (coefficient == 1.0 && offset == 0.0)
            {
                continue;
            }

            changed = true;
            for (var h = 0; h < biases.Length; h++)
            {
                var p = (h * CandidateFeatures.FeatureCount) + f;
                var weight = weights[p];
                biases[h] += weight * offset;
                weights[p] = weight * coefficient;
            }
        }

        return changed;
    }

    private static double InputScale(double[]? stdDevs, int feature) =>
        stdDevs is not null && stdDevs[feature] > 1e-8 ? 1.0 / stdDevs[feature] : 1.0;

    private static double InputOffset(double[]? means, double[]? stdDevs, int feature) =>
        means is not null && stdDevs is not null && stdDevs[feature] > 1e-8
            ? -means[feature] / stdDevs[feature]
            : 0.0;
}
