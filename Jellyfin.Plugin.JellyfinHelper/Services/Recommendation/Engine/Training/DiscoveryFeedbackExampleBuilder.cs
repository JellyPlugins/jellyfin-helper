using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>
///     Builds training examples from discovery feedback (shown, dismissed, requested, watched).
/// </summary>
internal static class DiscoveryFeedbackExampleBuilder
{
    /// <summary>
    ///     Builds training examples from all discovery feedback entries.
    /// </summary>
    /// <param name="feedbackResults">All discovery feedback data (loaded from <see cref="IDiscoveryFeedbackStore"/>).</param>
    /// <param name="profileById">User watch profiles keyed by user ID (for computing user-specific features).</param>
    /// <param name="seriesEpisodeCounts">Per series episode count. Same weighting as inference. Null means neutral.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list of training examples and the count of discovery examples added.</returns>
    internal static (List<TrainingExample> Examples, int Count) BuildDiscoveryExamples(
        IReadOnlyList<DiscoveryFeedbackResult> feedbackResults,
        IReadOnlyDictionary<Guid, UserWatchProfile> profileById,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        CancellationToken cancellationToken)
    {
        var examples = new List<TrainingExample>();

        if (feedbackResults.Count == 0)
        {
            return (examples, 0);
        }

        foreach (var userFeedback in feedbackResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (userFeedback.Entries.Count == 0)
            {
                continue;
            }

            profileById.TryGetValue(userFeedback.UserId, out var userProfile);

            BuildUserContext(
                userProfile,
                seriesEpisodeCounts,
                out var genrePreferences,
                out var avgYear,
                out var preferredPeople,
                out var genreExposure);

            foreach (var entry in userFeedback.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var status = entry.GetStatus();
                var label = GetLabelForStatus(status);

                var entryGenres = entry.Genres ?? Array.Empty<string>();
                var (familiarity, genreAvgCompletion, genreAbandonRate) = userProfile is not null
                    ? ContentScoring.ComputeGenreEngagement(entryGenres, userProfile)
                    : (0.0, 0.5, 0.0);
                var userRatingScore = userProfile is not null
                    ? ContentScoring.ComputeGenreRatingScore(entryGenres, userProfile)
                    : 0.5;
                var features = BuildFeaturesFromEntry(
                    entry,
                    genrePreferences,
                    preferredPeople,
                    avgYear,
                    genreExposure,
                    out var hasLegacyPopularity);
                features.HasUserInteraction = familiarity > 0.0;
                features.CompletionRatio = genreAvgCompletion;
                features.IsAbandoned = genreAbandonRate;
                features.UserRatingScore = userRatingScore;

                var sampleWeight = hasLegacyPopularity
                    ? EngineConstants.DiscoveryFeedbackSampleWeight * 0.5
                    : EngineConstants.DiscoveryFeedbackSampleWeight;

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = GetLatestInteractionUtc(entry),
                    SampleWeight = sampleWeight,
                    UserId = userFeedback.UserId
                });
            }
        }

        return (examples, examples.Count);
    }

    /// <summary>
    ///     Builds the per-user preference context (genre preferences, average year, preferred people, genre-exposure analysis) for discovery-feedback feature computation.
    /// </summary>
    /// <param name="userProfile">The user's watch profile, or <c>null</c>.</param>
    /// <param name="seriesEpisodeCounts">Optional per-series episode-count map.</param>
    /// <param name="genrePreferences">Receives the genre preference vector.</param>
    /// <param name="avgYear">Receives the user's average watched production year.</param>
    /// <param name="preferredPeople">Receives the preferred-people set.</param>
    /// <param name="genreExposure">Receives the genre-exposure analysis.</param>
    private static void BuildUserContext(
        UserWatchProfile? userProfile,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        out Dictionary<string, double> genrePreferences,
        out double avgYear,
        out HashSet<string> preferredPeople,
        out PreferenceBuilder.GenreExposureAnalysis genreExposure)
    {
        genrePreferences = userProfile != null
            ? PreferenceBuilder.BuildGenrePreferenceVector(userProfile, seriesEpisodeCounts)
            : new Dictionary<string, double>();

        avgYear = userProfile != null
            ? ContentScoring.ComputeAverageYear(userProfile)
            : 0.0;

        preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (userProfile?.TopPeople is { } topPeople)
        {
            foreach (var person in topPeople)
            {
                preferredPeople.Add(person);
            }
        }

        genreExposure = userProfile != null
            ? PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, userProfile)
            : new PreferenceBuilder.GenreExposureAnalysis
            {
                UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AveragePreferenceWeight = 0,
                GenrePreferences = genrePreferences,
                IsValid = false
            };
    }

    /// <summary>
    ///     Maps a <see cref="DiscoveryInteractionStatus"/> to the appropriate training label.
    /// </summary>
    private static double GetLabelForStatus(DiscoveryInteractionStatus status)
    {
        return status switch
        {
            DiscoveryInteractionStatus.RequestedAndWatched => EngineConstants.DiscoveryRequestedAndWatchedLabel,
            DiscoveryInteractionStatus.Requested => EngineConstants.DiscoveryRequestedLabel,
            DiscoveryInteractionStatus.Dismissed => EngineConstants.DiscoveryDismissedLabel,
            DiscoveryInteractionStatus.Shown => EngineConstants.DiscoveryShownLabel,
            _ => EngineConstants.DiscoveryShownLabel
        };
    }

    /// <summary>
    ///     Returns the most recent interaction timestamp for a feedback entry.
    /// </summary>
    private static DateTime GetLatestInteractionUtc(DiscoveryFeedbackEntry entry)
    {
        var latest = entry.ShownAtUtc;
        if (entry.DismissedAtUtc.HasValue && entry.DismissedAtUtc.Value > latest)
        {
            latest = entry.DismissedAtUtc.Value;
        }

        if (entry.RequestedAtUtc.HasValue && entry.RequestedAtUtc.Value > latest)
        {
            latest = entry.RequestedAtUtc.Value;
        }

        if (entry.WatchedAtUtc.HasValue && entry.WatchedAtUtc.Value > latest)
        {
            latest = entry.WatchedAtUtc.Value;
        }

        return latest;
    }

    /// <summary>
    ///     Builds a CandidateFeatures vector from a discovery feedback entry.
    /// </summary>
    private static CandidateFeatures BuildFeaturesFromEntry(
        DiscoveryFeedbackEntry entry,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        out bool hasLegacyPopularity)
    {
        var genres = entry.Genres ?? Array.Empty<string>();

        var genreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences);

        var combinedCriticScore = ContentScoring.ComputeCombinedCriticScore((float?)entry.TmdbRating, null);

        var recencyScore = entry.Year is { } year and >= 1 and <= 9999
            ? ContentScoring.ComputeRecencyScore(new DateTime(year, 7, 1, 0, 0, 0, DateTimeKind.Utc))
            : 0.5;

        var yearProximityScore = ContentScoring.ComputeYearProximity(entry.Year, avgYear);

        var peopleSimilarity = 0.0;
        if (entry.KnownPeople is { Count: > 0 } && preferredPeople.Count > 0)
        {
            peopleSimilarity = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
                entry.KnownPeople, preferredPeople);
        }

        var popularityScore = ExternalCandidateFeatureBuilder.NormalizePopularity(entry.Popularity);
        hasLegacyPopularity = entry.Popularity <= 0;

        var isSeries = string.Equals(entry.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        var features = new CandidateFeatures
        {
            GenreSimilarity = genreSimilarity,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = recencyScore,
            YearProximityScore = yearProximityScore,
            GenreCount = genres.Count,
            IsSeries = isSeries,
            PeopleSimilarity = peopleSimilarity,
            PopularityScore = popularityScore,

            CollaborativeScore = 0.5,
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
            FranchiseAffinity = 0.0,
            ProductionLocationAffinity = 0.0,
            InheritedTagSimilarity = 0.0,
            SeriesCompletability = 0.5,
            WriterAffinity = 0.0,
            BillingWeightedPeople = 0.0,
            GenreStudioIdfPrior = 0.0
        };

        // Genre exposure features
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        return features;
    }
}