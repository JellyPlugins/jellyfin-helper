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
    /// <returns>A populated <see cref="CandidateFeatures"/> instance.</returns>
    internal static CandidateFeatures Build(
        TmdbDiscoverItem candidate,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(genrePreferences);
        ArgumentNullException.ThrowIfNull(preferredPeople);

        // Defensive: ensure the preferredPeople set uses case-insensitive comparison.
        // Callers should already pass OrdinalIgnoreCase, but rebuild if not to prevent
        // silent zero-overlap from TMDb name casing differences.
        if (preferredPeople.Count > 0 && preferredPeople.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            preferredPeople = new HashSet<string>(preferredPeople, StringComparer.OrdinalIgnoreCase);
        }

        var genres = TmdbGenreMap.ToJellyfinGenres(candidate.GenreIds);

        return new CandidateFeatures
        {
            // Strong signals (derivable from TMDb)
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences),
            CombinedCriticScore = Math.Clamp(candidate.VoteAverage / 10.0, 0.0, 1.0),
            RecencyScore = candidate.EffectiveReleaseDate.HasValue
                ? ContentScoring.ComputeRecencyScore(candidate.EffectiveReleaseDate.Value)
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(
                candidate.EffectiveReleaseDate?.Year, avgYear),
            GenreCount = genres.Count,
            IsSeries = string.Equals(candidate.MediaType, "tv", StringComparison.OrdinalIgnoreCase),
            PopularityScore = Math.Clamp(candidate.Popularity / PopularityNormalizationCap, 0.0, 1.0),
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
            CollectionProgressionBoost = 0.0
        };
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