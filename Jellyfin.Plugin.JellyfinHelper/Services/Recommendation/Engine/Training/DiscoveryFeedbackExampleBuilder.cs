using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>
///     Builds training examples from discovery feedback (shown, dismissed, requested, watched).
///     Called as Phase 4 by the TrainingDataBuilder.
///     Discovery items are external (not in library), so features are limited to:
///     GenreSimilarity, CombinedCriticScore, RecencyScore, YearProximityScore, PopularityScore, PeopleSimilarity.
///     Library-only features (CollaborativeScore, ContentNearestNeighbor, etc.) are set to neutral (0.5 or 0.0).
/// </summary>
internal static class DiscoveryFeedbackExampleBuilder
{
    /// <summary>
    ///     Builds training examples from all discovery feedback entries.
    /// </summary>
    /// <param name="feedbackResults">All discovery feedback data (loaded from <see cref="IDiscoveryFeedbackStore"/>).</param>
    /// <param name="profileById">User watch profiles keyed by user ID (for computing user-specific features).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list of training examples and the count of discovery examples added.</returns>
    internal static (List<TrainingExample> Examples, int Count) BuildDiscoveryExamples(
        IReadOnlyList<DiscoveryFeedbackResult> feedbackResults,
        IReadOnlyDictionary<Guid, UserWatchProfile> profileById,
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

            // Look up the user's watch profile for computing features
            if (!profileById.TryGetValue(userFeedback.UserId, out var userProfile))
            {
                continue;
            }

            // Build user-specific preferences for feature computation.
            // Do NOT skip users with empty genre preferences (cold-start users).
            // Their explicit discovery interactions (request/dismiss) are still valuable
            // training signals even when genre features default to zero/neutral.
            var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(userProfile);

            var avgYear = ContentScoring.ComputeAverageYear(userProfile);

            // Build preferred people set from watch profile
            var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (userProfile.TopPeople is { } topPeople)
            {
                foreach (var person in topPeople)
                {
                    preferredPeople.Add(person);
                }
            }

            // Genre exposure analysis for advanced features
            var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, userProfile);

            foreach (var entry in userFeedback.Entries)
            {
                var status = entry.GetStatus();
                var label = GetLabelForStatus(status);

                // Build feature vector from discovery metadata
                var features = BuildFeaturesFromEntry(
                    entry,
                    genrePreferences,
                    preferredPeople,
                    avgYear,
                    genreExposure);

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = entry.RequestedAtUtc ?? entry.DismissedAtUtc ?? entry.ShownAtUtc,
                    SampleWeight = EngineConstants.DiscoveryFeedbackSampleWeight
                });
            }
        }

        return (examples, examples.Count);
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
    ///     Builds a <see cref="CandidateFeatures"/> vector from a discovery feedback entry.
    ///     Uses the same feature structure as the main scoring pipeline but with neutral values
    ///     for features that require library-side data.
    /// </summary>
    private static CandidateFeatures BuildFeaturesFromEntry(
        DiscoveryFeedbackEntry entry,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure)
    {
        // Null-safe genre access
        var genres = entry.Genres ?? Array.Empty<string>();

        // Compute genre similarity against user profile
        var genreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences);

        // Compute combined critic score from TMDb rating (normalized 0-10 → 0-1)
        var combinedCriticScore = Math.Clamp(entry.TmdbRating / 10.0, 0.0, 1.0);

        // Compute recency from production year
        var recencyScore = entry.Year is { } year and >= 1 and <= 9999
            ? ContentScoring.ComputeRecencyScore(new DateTime(year, 7, 1))
            : 0.5;

        // Year proximity
        var yearProximityScore = ContentScoring.ComputeYearProximity(entry.Year, avgYear);

        // People similarity from cached KnownPeople.
        // NOTE: Currently always 0 because RecordShown does not persist KnownPeople
        // (credits data is only available during GenerateForUserAsync via EnrichTopCandidatesWithCreditsAsync
        // and is not propagated to the feedback entry). This feature will activate once
        // RecordShown is extended to include KnownPeople from the DiscoveryRecommendation.
        var peopleSimilarity = 0.0;
        if (entry.KnownPeople is { Count: > 0 } && preferredPeople.Count > 0)
        {
            peopleSimilarity = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
                entry.KnownPeople, preferredPeople);
        }

        // Popularity proxy from TMDb rating + score
        var popularityScore = Math.Clamp(entry.Score, 0.0, 1.0);

        var isSeries = string.Equals(entry.MediaType, "tv", StringComparison.OrdinalIgnoreCase);

        var features = new CandidateFeatures
        {
            // Strong signals (derivable from TMDb metadata)
            GenreSimilarity = genreSimilarity,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = recencyScore,
            YearProximityScore = yearProximityScore,
            GenreCount = genres.Count,
            IsSeries = isSeries,
            PeopleSimilarity = peopleSimilarity,
            PopularityScore = popularityScore,

            // Neutral signals (not available for external/discovery items)
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

        // Genre exposure features
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        return features;
    }
}