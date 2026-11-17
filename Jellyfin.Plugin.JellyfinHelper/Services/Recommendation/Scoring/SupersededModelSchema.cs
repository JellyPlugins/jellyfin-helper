using System;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Recognizes an intact discarded schema for fresh fitting only; never constructs or converts a V5 predictor.</summary>
internal static class SupersededModelSchema
{
    private const int PreviousFeatureCount = 38;
    private const int PreviousHidden1Size = 76;

    internal static bool IsIntact(AcceptedModelBundle bundle, JsonElement root)
        => HasUniqueFields(root) && HasFields(root, "Version", "IsProvisional", "Revision", "TrainedAtUtc", "Learned", "Neural", "Mixture", "AlphaCap", "NeuralCap", "AcceptanceObservationIds", "AcceptanceLedgerRequired", "State", "Retired")
            && !bundle.Retired && bundle.Revision < long.MaxValue
            && HasFields(root.GetProperty("Mixture"), "Learned", "Neural")
            && HasFields(root.GetProperty("State"), "SchemaVersion", "Alpha", "NeuralBeta", "QualityGateFrozen", "ObservedTrainingIds", "TrainingExampleCount", "MetricsHistory")
            && bundle.State.ObservedTrainingIds is { } ids && ids.All(id => id != Guid.Empty)
            && ids.Distinct().Count() == ids.Length && bundle.State.TrainingExampleCount == ids.Length && ids.Length <= EnsembleScoringStrategy.MaxTrackedObservations
            && HasFields(root.GetProperty("Learned"), "Version", "Weights", "Bias", "TrainingGeneration")
            && IntactLearned(bundle.Learned)
            && (bundle.Neural is null || (HasFields(root.GetProperty("Neural"), "Version", "WeightsIH", "BiasH1", "WeightsH1H2", "BiasH2", "WeightsH2H3", "BiasH3", "WeightsH3H4", "BiasH4", "WeightsH4O", "BiasOutput", "TrainingGeneration")
                && IntactNeural(bundle.Neural) && IntactOptimizerFields(root.GetProperty("Neural"))))
            && (!bundle.IsProvisional || HasFields(root, "CalibrationLoss", "CalibrationTitleCount", "OperatingInputDigest", "Utility"))
            && (!bundle.IsProvisional || (bundle.OperatingInputDigest is { Length: 64 } digest && digest.All(Uri.IsHexDigit)));

    private static bool IntactLearned(LearnedScoringStrategy.WeightsData data)
        => data.Version == 5 && data.TrainingGeneration > 0 && FiniteArray(data.Weights, PreviousFeatureCount) && double.IsFinite(data.Bias)
            && TrainingMath.ValidNormalization(data.FeatureMeans, data.FeatureStdDevs, PreviousFeatureCount)
            && (data.ImputationMeans is null || FiniteArray(data.ImputationMeans, PreviousFeatureCount))
            && IntactLearnedLimits(data);

    private static bool IntactLearnedLimits(LearnedScoringStrategy.WeightsData data)
    {
        if (data.WeightLimits is null)
        {
            return data.BiasLimit is null;
        }

        return FiniteArray(data.WeightLimits, PreviousFeatureCount) && data.WeightLimits.All(value => value > 0)
            && data.BiasLimit is double limit && double.IsFinite(limit) && limit > 0 && limit >= Math.Abs(data.Bias)
            && data.Weights.Select((weight, index) => Math.Abs(weight) <= data.WeightLimits[index]).All(valid => valid);
    }

    private static bool IntactNeural(NeuralScoringStrategy.NeuralWeightsData data)
        => data.Version == 5 && data.TrainingGeneration > 0
            && FiniteArray(data.WeightsIH, PreviousFeatureCount * PreviousHidden1Size) && FiniteArray(data.BiasH1, PreviousHidden1Size)
            && FiniteArray(data.WeightsH1H2, PreviousHidden1Size * 96) && FiniteArray(data.BiasH2, 96)
            && FiniteArray(data.WeightsH2H3, 96 * 48) && FiniteArray(data.BiasH3, 48)
            && FiniteArray(data.WeightsH3H4, 48 * 24) && FiniteArray(data.BiasH4, 24)
            && FiniteArray(data.WeightsH4O, 24) && double.IsFinite(data.BiasOutput)
            && TrainingMath.ValidNormalization(data.FeatureMeans, data.FeatureStdDevs, PreviousFeatureCount)
            && (data.ImputationMeans is null || FiniteArray(data.ImputationMeans, PreviousFeatureCount))
            && (data.TrainingState is null || data.TrainingState.Valid(PreviousFeatureCount, PreviousHidden1Size));

    private static bool FiniteArray(double[]? values, int length) => values is not null && values.Length == length && values.All(double.IsFinite);

    private static bool IntactOptimizerFields(JsonElement neural)
        => !neural.TryGetProperty("TrainingState", out var state) || state.ValueKind == JsonValueKind.Null
            || HasFields(state, "Version", "Timestep", "FirstMoments", "SecondMoments", "OutputFirstMoment", "OutputSecondMoment", "InputWeightLimits", "InputBiasLimits");

    private static bool HasFields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var fields = element.EnumerateObject().Select(property => property.Name).ToArray();
        return fields.Distinct(StringComparer.Ordinal).Count() == fields.Length && names.All(name => element.TryGetProperty(name, out _));
    }

    /// <summary>Rejects ambiguous duplicate JSON fields instead of silently taking their last value.</summary>
    /// <param name="element">A bounded parsed receipt.</param>
    /// <returns>Whether nested metadata has exactly one meaning.</returns>
    internal static bool HasUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().All(HasUniqueFields);
        }

        return element.ValueKind != JsonValueKind.Object
            || (HasFields(element) && element.EnumerateObject().All(property => HasUniqueFields(property.Value)));
    }
}
