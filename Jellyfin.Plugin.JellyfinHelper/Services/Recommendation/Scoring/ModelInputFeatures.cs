using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Shared v6 model inputs, names and fitting-only missing-feature coordinates.</summary>
internal static class ModelInputFeatures
{
    internal const int FeatureCount = CandidateFeatures.FeatureCount;

    internal static IReadOnlyList<string> Names(int count) => Array.AsReadOnly(Enumerable.Range(0, count).Select(Name).ToArray());

    internal static string Name(int index) => ScoringHelper.GetFeatureName((FeatureIndex)index);

    internal static void Write(CandidateFeatures features, double[] buffer, IReadOnlyList<double>? means, int count)
    {
        if (count != FeatureCount)
        {
            throw new ArgumentException("Model inputs require the exact v6 feature count.", nameof(count));
        }

        features.WriteForModel(buffer, means);
    }

    internal static void WriteContext(CandidateFeatures features, double[] buffer, IReadOnlyList<double>? means)
    {
        for (var index = (int)FeatureIndex.ContentFitAffinity; index < FeatureCount; index++)
        {
            buffer[index] = Value(features, index) ?? (means?.Count == FeatureCount && double.IsFinite(means[index]) ? Math.Clamp(means[index], 0, 1) : 0);
        }
    }

    internal static double[] Vector(CandidateFeatures features, IReadOnlyList<double>? means, int count)
    {
        var vector = new double[count];
        Write(features, vector, means, count);
        return vector;
    }

    internal static double[] FitImputation(IReadOnlyList<TrainingExample> examples, IReadOnlyList<int> fitting, double[]? prior)
    {
        var language = LanguageFeatureImputation.Fit(examples, fitting, prior) ?? prior;
        var means = new double[FeatureCount];
        var defaults = new CandidateFeatures { IsExternalCandidate = true, HasPeopleMetadata = false }.ToModelVector(null);
        Array.Copy(language ?? defaults, means, CandidateFeatures.FeatureCount);
        foreach (var index in Enumerable.Range((int)FeatureIndex.ContentFitAffinity, 3))
        {
            var values = fitting.Order().Select(row => Value(examples[row].Features, index)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            var fallback = prior is { Length: FeatureCount } ? prior[index] : 0;
            means[index] = values.Length > 0 ? values.Average() : fallback;
        }

        return means;
    }

    internal static void ApplyMissing(IReadOnlyList<TrainingExample> examples, double[][] vectors, double[] means)
    {
        LanguageFeatureImputation.Apply(examples, vectors, means);
        for (var index = 0; index < examples.Count; index++)
        {
            WriteContext(examples[index].Features, vectors[index], means);
        }
    }

    private static double? Value(CandidateFeatures features, int index) => index switch
    {
        38 => features.ContentFitAffinity,
        39 => features.HasContentMetadata == true ? features.ContentFitSupport ?? 0 : features.ContentFitSupport,
        40 => features.HasContentMetadata == true ? features.AudienceMismatch ?? 0 : features.AudienceMismatch,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}
