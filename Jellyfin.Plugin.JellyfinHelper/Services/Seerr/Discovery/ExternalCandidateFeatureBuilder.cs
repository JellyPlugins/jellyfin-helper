using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Builds a <see cref="CandidateFeatures"/> vector from external TMDb metadata.
///     Features that require library-side data (collaborative filtering, content nearest
///     neighbor, user interaction history) are set to neutral values (0.5 or 0.0) to avoid
///     biasing the score. The dominant signals are GenreSimilarity, PeopleSimilarity,
///     RecencyScore, CombinedCriticScore, and YearProximityScore.
/// </summary>
internal static class ExternalCandidateFeatureBuilder
{
    /// <summary>
    ///     Minimum number of preferred people required for full similarity score (1.0).
    ///     With fewer than this many matching people, a single match yields a proportionally
    ///     higher score (e.g. 1/3 = 0.33 if user only has 3 preferred people).
    ///     Set to 5 because a typical engaged user accumulates 5-20 preferred people,
    ///     and we want 1 match out of 5+ to yield ~0.2 (a meaningful but not dominant signal).
    /// </summary>
    private const int MinPeopleForFullScore = 5;

    /// <summary>
    ///     Normalization cap for TMDb popularity values. Top trending TMDb items typically
    ///     peak around 100-200 popularity; values above this cap saturate at 1.0.
    /// </summary>
    private const double PopularityNormalizationCap = 200.0;

    /// <summary>
    ///     Builds a feature vector from a TMDb discover item and user preferences.
    /// </summary>
    /// <param name="candidate">The TMDb discover item to score.</param>
    /// <param name="genrePreferences">The user's genre preference vector (genre name to weight).</param>
    /// <param name="preferredPeople">The user's preferred people set (actors/directors).</param>
    /// <param name="avgYear">The user's average watched production year.</param>
    /// <param name="genreExposure">
    ///     The user's pre-built genre exposure analysis (from
    ///     <see cref="PreferenceBuilder.BuildGenreExposureAnalysis"/>). Required so that the
    ///     GenreUnderexposure / GenreDominanceRatio / GenreAffinityGap features are computed
    ///     identically to the discovery training pipeline
    ///     (<c>DiscoveryFeedbackExampleBuilder</c>). Passing a matching analysis eliminates the
    ///     train/serve skew that would otherwise leave these three features at 0.0 during
    ///     inference while the model was trained on their real values.
    /// </param>
    /// <returns>A populated <see cref="CandidateFeatures"/> instance.</returns>
    internal static CandidateFeatures Build(
        TmdbDiscoverItem candidate,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(genrePreferences);
        ArgumentNullException.ThrowIfNull(preferredPeople);
        ArgumentNullException.ThrowIfNull(genreExposure);

        // Defensive: ensure the preferredPeople set uses case-insensitive comparison.
        // Callers should already pass OrdinalIgnoreCase, but rebuild if not to prevent
        // silent zero-overlap from TMDb name casing differences.
        if (preferredPeople.Count > 0 && preferredPeople.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            preferredPeople = new HashSet<string>(preferredPeople, StringComparer.OrdinalIgnoreCase);
        }

        var genres = TmdbGenreMap.ToJellyfinGenres(candidate.GenreIds);

        var features = new CandidateFeatures
        {
            // Strong signals (derivable from TMDb)
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences),
            CombinedCriticScore = Math.Clamp(candidate.VoteAverage / 10.0, 0.0, 1.0),
            // Recency is quantized to the release YEAR (mid-year anchor) rather than the full
            // EffectiveReleaseDate, to stay bit-identical with the discovery TRAINING path
            // (DiscoveryFeedbackExampleBuilder), which only has the release year cached on the
            // feedback entry. Using the full date here would make the same title score a slightly
            // different recency at train vs. serve - a subtle skew on this feature.
            RecencyScore = candidate.EffectiveReleaseDate is { } releaseDate
                                && releaseDate.Year is >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(releaseDate.Year, 7, 1))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(
                candidate.EffectiveReleaseDate?.Year, avgYear),
            GenreCount = genres.Count,
            IsSeries = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase),
            PopularityScore = NormalizePopularity(candidate.Popularity),
            PeopleSimilarity = ComputePeopleSimilarity(candidate, preferredPeople),

            // Neutral signals (no library data available)
            CollaborativeScore = 0.5,
            UserRatingScore = 0.5,
            HasUserInteraction = false,
            CompletionRatio = 0.5,
            StudioMatch = false,
            SeriesProgressionBoost = 0.0,
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            IsWeekend = false,
            TagSimilarity = 0.0,
            LibraryAddedRecency = 0.5,
            ContentNearestNeighborScore = 0.0,
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            CollectionProgressionBoost = 0.0,

            // New content-affinity signals are library-only and cannot be derived from the TMDb
            // discover payload (no collection id, countries, writers, billing, or series status),
            // so they are neutralized: overlap-style signals -> 0.0, SeriesCompletability -> 0.5 (N/A).
            // These MUST stay lock-step with DiscoveryFeedbackExampleBuilder to avoid train/serve skew.
            FranchiseAffinity = 0.0,
            ProductionLocationAffinity = 0.0,
            InheritedTagSimilarity = 0.0,
            SeriesCompletability = 0.5,
            WriterAffinity = 0.0,
            BillingWeightedPeople = 0.0,
            GenreStudioIdfPrior = 0.0
        };

        // Genre exposure features: MUST be computed here (inference) with the same analysis
        // used by DiscoveryFeedbackExampleBuilder (training) to avoid train/serve skew.
        // Discovery candidates are fetched by the user's top genres, so DominanceRatio is
        // typically high (deserved boost) while Underexposure/AffinityGap flag off-taste drift.
        // For users with insufficient history, the analysis is invalid and all three collapse
        // to 0.0 - identical to the previous behavior, so short-history users are unaffected.
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        return features;
    }

    /// <summary>
    ///     Normalizes a raw TMDb popularity value into the [0, 1] range used by the
    ///     <see cref="CandidateFeatures.PopularityScore"/> feature.
    ///     <para>
    ///         Single source of truth shared by the discovery inference path
    ///         (<see cref="Build"/>) and the discovery training path
    ///         (<c>DiscoveryFeedbackExampleBuilder</c>) so the two can never drift apart and
    ///         reintroduce a train/serve skew for the popularity feature.
    ///     </para>
    /// </summary>
    /// <param name="rawPopularity">The raw TMDb popularity value (typically 0-200+).</param>
    /// <returns>A normalized popularity score in [0, 1].</returns>
    internal static double NormalizePopularity(double rawPopularity)
    {
        if (!double.IsFinite(rawPopularity) || rawPopularity <= 0)
        {
            return 0.0;
        }

        return Math.Clamp(rawPopularity / PopularityNormalizationCap, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes people similarity from a list of known people names against preferred people.
    ///     Shared formula used by both live scoring and training data building.
    ///     Deduplicates names to prevent double-counting (e.g., director + writer credits).
    /// </summary>
    /// <param name="knownPeople">The candidate's known people names.</param>
    /// <param name="preferredPeople">The user's preferred people set (case-insensitive).</param>
    /// <returns>A similarity score between 0.0 and 1.0.</returns>
    internal static double ComputePeopleSimilarityFromNames(
        IEnumerable<string>? knownPeople,
        HashSet<string> preferredPeople)
    {
        if (knownPeople == null || preferredPeople.Count == 0)
        {
            return 0.0;
        }

        var overlap = knownPeople
            .Where(p => !string.IsNullOrWhiteSpace(p) && preferredPeople.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return Math.Clamp((double)overlap / Math.Min(preferredPeople.Count, MinPeopleForFullScore), 0.0, 1.0);
    }

    /// <summary>
    ///     Computes people similarity from limited TMDb data.
    ///     TMDb discover responses include limited cast data; full cast requires
    ///     a separate /movie/{id}/credits call which is too expensive for bulk queries.
    /// </summary>
    private static double ComputePeopleSimilarity(
        TmdbDiscoverItem candidate,
        HashSet<string> preferredPeople)
    {
        if (preferredPeople.Count == 0 || candidate.KnownPeople is not { Count: > 0 })
        {
            return 0.0;
        }

        return ComputePeopleSimilarityFromNames(candidate.KnownPeople, preferredPeople);
    }
}