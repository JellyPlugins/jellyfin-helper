using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Fixed, fitting-only missing-credit coordinates; raw captured vectors and labels are never rewritten.</summary>
internal static class CreditFeatureImputation
{
    private static readonly FeatureIndex[] Coordinates = [FeatureIndex.PeopleSimilarity, FeatureIndex.WriterAffinity, FeatureIndex.BillingWeightedPeople];

    private static bool? Known(CandidateFeatures features, FeatureIndex coordinate) => coordinate switch
    {
        FeatureIndex.PeopleSimilarity => features.HasPeopleMetadata,
        FeatureIndex.WriterAffinity => features.HasWriterMetadata,
        _ => features.HasBillingMetadata
    };

    private static double Value(CandidateFeatures features, FeatureIndex coordinate) => coordinate switch
    {
        FeatureIndex.PeopleSimilarity => features.PeopleSimilarity,
        FeatureIndex.WriterAffinity => features.WriterAffinity,
        _ => features.BillingWeightedPeople
    };

    /// <summary>Writes explicitly unavailable fields at fixed model means, preserving all legacy null markers.</summary>
    /// <param name="features">Original raw signals.</param>
    /// <param name="vector">A caller-owned vector copy.</param>
    /// <param name="means">Fixed means belonging to this model.</param>
    internal static void Write(CandidateFeatures features, double[] vector, IReadOnlyList<double>? means)
    {
        foreach (var coordinate in Coordinates.Where(coordinate => Known(features, coordinate) == false))
        {
            var value = means?.Count == CandidateFeatures.FeatureCount ? means[(int)coordinate] : 0;
            vector[(int)coordinate] = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        }

        vector[(int)FeatureIndex.PeopleGenreInteraction] = features.GenreSimilarity * vector[(int)FeatureIndex.PeopleSimilarity];
    }

    /// <summary>Fits only evidenced fitting members, with no labels, held-out rows or fabricated metadata.</summary>
    /// <param name="examples">Detached examples.</param>
    /// <param name="fitting">Only the fitting partition.</param>
    /// <param name="prior">Previous fixed means; an all-legacy corpus keeps its prior unchanged.</param>
    /// <returns>New fixed coordinates, or the unchanged legacy prior.</returns>
    internal static double[]? Fit(IReadOnlyList<TrainingExample> examples, IReadOnlyList<int> fitting, double[]? prior)
    {
        if (!fitting.Any(index => examples[index].Features.Credits is not null
            || examples[index].Features.HasWriterMetadata.HasValue || examples[index].Features.HasBillingMetadata.HasValue))
        {
            return prior;
        }

        var means = prior is { Length: CandidateFeatures.FeatureCount } ? (double[])prior.Clone() : new CandidateFeatures().ToVector();
        foreach (var coordinate in Coordinates)
        {
            var values = fitting.Order().Select(index => examples[index].Features)
                .Where(features => Known(features, coordinate) == true && (!features.IsExternalCandidate || features.IsExternalFeatureKnown(coordinate)))
                .Select(features => Value(features, coordinate)).ToArray();
            var fallback = double.IsFinite(means[(int)coordinate]) ? Math.Clamp(means[(int)coordinate], 0, 1) : 0;
            means[(int)coordinate] = values.Length > 0 ? values.Average() : fallback;
        }

        return means;
    }

    /// <summary>Applies fitting-only means to owned copies, never original prediction snapshots.</summary>
    /// <param name="examples">Raw captured/example features.</param>
    /// <param name="vectors">Fold-owned vector copies.</param>
    /// <param name="means">Fixed means from fitting members only.</param>
    internal static void Apply(IReadOnlyList<TrainingExample> examples, double[][] vectors, double[] means)
    {
        for (var index = 0; index < examples.Count; index++)
        {
            Write(examples[index].Features, vectors[index], means);
        }
    }
}
