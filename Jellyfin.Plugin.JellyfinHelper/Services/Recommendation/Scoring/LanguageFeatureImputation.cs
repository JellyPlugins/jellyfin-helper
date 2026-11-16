using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Fits missing-language coordinates from evidenced fitting rows only, never labels or validation rows.</summary>
internal static class LanguageFeatureImputation
{
    /// <summary>Returns fixed model means; a fully legacy corpus keeps its original preprocessing exactly.</summary>
    /// <param name="examples">Detached training examples, excluding external acceptance blocks.</param>
    /// <param name="fittingIndices">Only the current fold's gradient-training members.</param>
    /// <param name="prior">Previous fixed model imputation means, or null for legacy constants.</param>
    /// <returns>A detached imputation vector, or the unchanged legacy prior.</returns>
    internal static double[]? Fit(IReadOnlyList<TrainingExample> examples, IReadOnlyList<int> fittingIndices, double[]? prior)
    {
        if (!fittingIndices.Any(index => examples[index].Features.HasAudioLanguageEvidence.HasValue
            || examples[index].Features.HasSubtitleLanguageEvidence.HasValue))
        {
            return prior;
        }

        var means = prior is { Length: CandidateFeatures.FeatureCount } ? (double[])prior.Clone() : new CandidateFeatures { IsExternalCandidate = true, HasPeopleMetadata = false }.ToModelVector(null);
        means[(int)FeatureIndex.LanguageAffinity] = Mean(examples, fittingIndices, audio: true);
        means[(int)FeatureIndex.SubtitleLanguageAffinity] = Mean(examples, fittingIndices, audio: false);
        return means;
    }

    private static double Mean(IReadOnlyList<TrainingExample> examples, IReadOnlyList<int> indices, bool audio)
    {
        var sum = 0.0;
        var count = 0;
        // Canonical order keeps fitting means independent of optimizer shuffling.
        foreach (var index in indices.Order())
        {
            var features = examples[index].Features;
            var known = audio ? features.HasAudioLanguageEvidence : features.HasSubtitleLanguageEvidence;
            var coordinate = audio ? FeatureIndex.LanguageAffinity : FeatureIndex.SubtitleLanguageAffinity;
            if (known != true || (features.IsExternalCandidate && !features.IsExternalFeatureKnown(coordinate)))
            {
                continue;
            }

            sum += audio ? features.LanguageAffinity : features.SubtitleLanguageAffinity;
            count++;
        }

        return count > 0 ? Math.Clamp(sum / count, 0, 1) : 0.5;
    }

    /// <summary>Rewrites only explicitly missing new language inputs in fold-owned vectors; snapshots stay untouched.</summary>
    /// <param name="examples">All rows in the internal fold universe.</param>
    /// <param name="vectors">Already model-imputed raw vector copies.</param>
    /// <param name="means">This fold's fitting-only fixed means.</param>
    internal static void Apply(IReadOnlyList<TrainingExample> examples, double[][] vectors, double[]? means)
    {
        if (means is not { Length: CandidateFeatures.FeatureCount })
        {
            return;
        }

        for (var index = 0; index < examples.Count; index++)
        {
            var features = examples[index].Features;
            if (features.HasAudioLanguageEvidence == false || (features.IsExternalCandidate && !features.IsExternalFeatureKnown(FeatureIndex.LanguageAffinity)))
            {
                vectors[index][(int)FeatureIndex.LanguageAffinity] = means[(int)FeatureIndex.LanguageAffinity];
            }

            if (features.HasSubtitleLanguageEvidence == false || (features.IsExternalCandidate && !features.IsExternalFeatureKnown(FeatureIndex.SubtitleLanguageAffinity)))
            {
                vectors[index][(int)FeatureIndex.SubtitleLanguageAffinity] = means[(int)FeatureIndex.SubtitleLanguageAffinity];
            }
        }
    }
}
