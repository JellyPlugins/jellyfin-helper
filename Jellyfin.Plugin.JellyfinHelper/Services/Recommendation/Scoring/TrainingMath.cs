using System;
using System.Collections.Generic;
using System.Linq;

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
        => examples.Where(example => (example.IsObserved || example.IsContrastSample)
            && example.ComputeEffectiveWeight(referenceTime) >= minimumWeight).ToArray();

    /// <summary>Tests whether an observed evaluation slice contains distinguishable relevance outcomes.</summary>
    /// <param name="examples">The evaluation observations.</param>
    /// <param name="referenceTime">The fixed temporal-weight reference.</param>
    /// <param name="minimumWeight">The eligibility floor.</param>
    /// <returns>True when at least two effective observed labels differ.</returns>
    internal static bool HasRankingEvidence(IReadOnlyList<TrainingExample>? examples, DateTime referenceTime, double minimumWeight)
    {
        if (examples is null)
        {
            return false;
        }

        var labels = examples.Where(e => e.IsObserved && e.ComputeEffectiveWeight(referenceTime) >= minimumWeight).Select(e => e.Label).ToArray();
        return labels.Length >= 2 && labels.Max() - labels.Min() > 1e-9;
    }

    /// <summary>Evaluates a constant predictor fitted only to training labels, never to held-out labels.</summary>
    /// <param name="training">The fit observations.</param>
    /// <param name="holdout">The observed evaluation slice.</param>
    /// <param name="referenceTime">The fixed temporal-weight reference.</param>
    /// <param name="minimumWeight">The eligibility floor.</param>
    /// <returns>Baseline held-out weighted MSE, or NaN without ranking evidence.</returns>
    internal static double BaselineValidationLoss(IReadOnlyList<TrainingExample> training, IReadOnlyList<TrainingExample>? holdout, DateTime referenceTime, double minimumWeight)
    {
        if (!HasRankingEvidence(holdout, referenceTime, minimumWeight))
        {
            return double.NaN;
        }

        var fit = training.Where(e => e.IsObserved && e.ComputeEffectiveWeight(referenceTime) >= minimumWeight).ToArray();
        var totalWeight = fit.Sum(e => e.ComputeEffectiveWeight(referenceTime));
        if (totalWeight <= 0)
        {
            return double.NaN;
        }

        var mean = fit.Sum(e => e.Label * e.ComputeEffectiveWeight(referenceTime)) / totalWeight;
        var loss = 0.0;
        var weightSum = 0.0;
        foreach (var example in holdout!)
        {
            var weight = example.ComputeEffectiveWeight(referenceTime);
            if (!example.IsObserved || weight < minimumWeight)
            {
                continue;
            }

            var error = mean - example.Label;
            loss += weight * error * error;
            weightSum += weight;
        }

        return loss / weightSum;
    }

    /// <summary>Returns positive trust only for a model that improves on an independent constant baseline.</summary>
    /// <param name="validationLoss">The model's observed held-out MSE.</param>
    /// <param name="baselineLoss">The training-fitted constant predictor's held-out MSE.</param>
    /// <returns>The fraction of baseline error removed, clamped to [0,1].</returns>
    internal static double QualityFactor(double validationLoss, double baselineLoss)
        => double.IsFinite(validationLoss) && validationLoss >= 0 && double.IsFinite(baselineLoss) && baselineLoss > 0
            ? Math.Clamp(1.0 - (validationLoss / baselineLoss), 0.0, 1.0)
            : 0.0;

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
            if (!example.IsObserved || weight < minimumWeight)
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
            // Exact identity detection keeps small coordinate changes from bypassing rebasing and retaining stale Adam moments.
            if (BitConverter.DoubleToInt64Bits(coefficient) == BitConverter.DoubleToInt64Bits(1.0) &&
                BitConverter.DoubleToInt64Bits(Math.Abs(offset)) == 0L)
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
