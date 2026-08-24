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
    /// <param name="seriesEpisodeCounts">
    ///     Per-series total-episode-count map, forwarded to <see cref="PreferenceBuilder.BuildGenrePreferenceVector"/>
    ///     so the discovery-feedback training examples apply the same progression weighting as the
    ///     inference path. May be null/empty (neutral, unweighted) - see <c>TrainingDataBuilder.BuildExamples</c>.
    /// </param>
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

            // Look up the user's watch profile for computing features.
            // If the profile is not found (e.g., watch history was cleared after discovery generation),
            // use neutral/empty defaults so the user's explicit feedback (dismiss/request) still
            // contributes training signal even without user-specific preference data.
            profileById.TryGetValue(userFeedback.UserId, out var userProfile);

            var context = BuildUserFeatureContext(userProfile, seriesEpisodeCounts);

            foreach (var entry in userFeedback.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examples.Add(BuildExampleFromEntry(entry, context));
            }
        }

        return (examples, examples.Count);
    }

    /// <summary>
    ///     Builds the per-user feature-computation context (genre preferences, average year, preferred
    ///     people, and genre exposure) from the user's watch profile.
    ///     Extracted verbatim from <see cref="BuildDiscoveryExamples"/> to reduce cognitive complexity.
    /// </summary>
    /// <param name="userProfile">The user's watch profile, or null when unavailable.</param>
    /// <param name="seriesEpisodeCounts">Per-series total-episode-count map used for progression weighting.</param>
    /// <returns>The assembled per-user feature context.</returns>
    private static UserFeatureContext BuildUserFeatureContext(
        UserWatchProfile? userProfile,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts)
    {
        // Build user-specific preferences for feature computation.
        // Users without a watch profile get empty preferences - their explicit discovery
        // interactions (request/dismiss) are still valuable training signals even when
        // genre features default to zero/neutral.
        var genrePreferences = userProfile != null
            ? PreferenceBuilder.BuildGenrePreferenceVector(userProfile, seriesEpisodeCounts)
            : new Dictionary<string, double>();

        var avgYear = userProfile != null
            ? ContentScoring.ComputeAverageYear(userProfile)
            : 0.0;

        // Build preferred people set from watch profile
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (userProfile?.TopPeople is { } topPeople)
        {
            foreach (var person in topPeople)
            {
                preferredPeople.Add(person);
            }
        }

        // Genre exposure analysis for advanced features.
        // BuildGenreExposureAnalysis handles empty genrePreferences gracefully
        // by returning an analysis with IsValid=false, which causes all exposure
        // features to default to 0.0 (neutral).
        var genreExposure = userProfile != null
            ? PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, userProfile)
            : new PreferenceBuilder.GenreExposureAnalysis
            {
                UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AveragePreferenceWeight = 0,
                GenrePreferences = genrePreferences,
                IsValid = false
            };

        return new UserFeatureContext(genrePreferences, avgYear, preferredPeople, genreExposure);
    }

    /// <summary>
    ///     Builds a single <see cref="TrainingExample"/> from a discovery feedback entry using the
    ///     per-user feature context.
    ///     Extracted verbatim from <see cref="BuildDiscoveryExamples"/> to reduce cognitive complexity.
    /// </summary>
    /// <param name="entry">The discovery feedback entry.</param>
    /// <param name="context">The per-user feature-computation context.</param>
    /// <returns>The assembled training example.</returns>
    private static TrainingExample BuildExampleFromEntry(DiscoveryFeedbackEntry entry, UserFeatureContext context)
    {
        var status = entry.GetStatus();
        var label = GetLabelForStatus(status);

        // Build feature vector from discovery metadata.
        // hasLegacyPopularity signals that the original TMDb popularity was not persisted
        // on this entry. The feature itself is now routed through the SAME
        // NormalizePopularity helper as inference (which returns 0.0 for missing/non-
        // positive popularity), so no train/serve skew remains on the feature value.
        // The halved sample weight is preserved as an orthogonal provenance signal:
        // rows without a recorded popularity still train the model, but contribute a
        // smaller gradient because we know less about them.
        var features = BuildFeaturesFromEntry(
            entry,
            context.GenrePreferences,
            context.PreferredPeople,
            context.AvgYear,
            context.GenreExposure,
            out var hasLegacyPopularity);

        var sampleWeight = hasLegacyPopularity
            ? EngineConstants.DiscoveryFeedbackSampleWeight * 0.5
            : EngineConstants.DiscoveryFeedbackSampleWeight;

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = GetLatestInteractionUtc(entry),
            SampleWeight = sampleWeight
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
    ///     Uses the maximum of all stored timestamps to ensure training examples
    ///     are placed at the correct temporal position for incremental-cutoff and holdout logic.
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
    ///     Builds a <see cref="CandidateFeatures"/> vector from a discovery feedback entry.
    ///     Uses the same feature structure as the main scoring pipeline but with neutral values
    ///     for features that require library-side data.
    /// </summary>
    private static CandidateFeatures BuildFeaturesFromEntry(
        DiscoveryFeedbackEntry entry,
        Dictionary<string, double> genrePreferences,
        HashSet<string> preferredPeople,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        out bool hasLegacyPopularity)
    {
        // Null-safe genre access
        var genres = entry.Genres ?? Array.Empty<string>();

        // Compute genre similarity against user profile
        var genreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genres, genrePreferences);

        // Compute combined critic score from TMDb rating; zero/absent ratings return 0.5 (neutral),
        // matching inference behavior.
        var combinedCriticScore = ContentScoring.ComputeCombinedCriticScore((float?)entry.TmdbRating, null);

        // Compute recency from production year
        var recencyScore = entry.Year is { } year and >= 1 and <= 9999
            ? ContentScoring.ComputeRecencyScore(new DateTime(year, 7, 1, 0, 0, 0, DateTimeKind.Utc))
            : 0.5;

        // Year proximity
        var yearProximityScore = ContentScoring.ComputeYearProximity(entry.Year, avgYear);

        // People similarity from cached KnownPeople.
        // Only populated for candidates that were enriched with credits data during discovery
        // generation (top-N by pre-score via EnrichTopCandidatesWithCreditsAsync).
        // Items that were not enriched will have empty KnownPeople and produce a
        // PeopleSimilarity of 0 for their training examples.
        var peopleSimilarity = 0.0;
        if (entry.KnownPeople is { Count: > 0 } && preferredPeople.Count > 0)
        {
            peopleSimilarity = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
                entry.KnownPeople, preferredPeople);
        }

        // Popularity feature: route THROUGH the same normalisation helper inference uses so
        // the training path can never diverge from what the model sees at serve time.
        //   call NormalizePopularity(entry.Popularity) directly - legacy
        //     rows with Popularity==0 now produce PopularityScore==0.0, bit-identical to
        //     what ExternalCandidateFeatureBuilder.Build would compute at inference time.
        //     The reduced provenance is signalled to the training pipeline through the
        //     halved sample weight in the caller (see BuildDiscoveryExamples above), which
        //     is orthogonal to the feature value and does not reintroduce any skew.
        var popularityScore = ExternalCandidateFeatureBuilder.NormalizePopularity(entry.Popularity);
        hasLegacyPopularity = entry.Popularity <= 0;

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
            CollectionProgressionBoost = 0.0,

            // Lock-step with ExternalCandidateFeatureBuilder: the same 7 new library-only signals are
            // neutralized identically (overlap -> 0.0, SeriesCompletability -> 0.5) because the discovery
            // feedback entry carries no collection/country/writer/billing/status data. Any divergence
            // between these two files reintroduces train/serve skew.
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

    /// <summary>
    ///     Per-user feature-computation context assembled once per feedback user and reused for
    ///     every entry belonging to that user.
    /// </summary>
    /// <param name="GenrePreferences">The user's genre preference vector.</param>
    /// <param name="AvgYear">The user's average production year.</param>
    /// <param name="PreferredPeople">The set of preferred person names.</param>
    /// <param name="GenreExposure">The genre exposure analysis for advanced features.</param>
    private readonly record struct UserFeatureContext(
        Dictionary<string, double> GenrePreferences,
        double AvgYear,
        HashSet<string> PreferredPeople,
        PreferenceBuilder.GenreExposureAnalysis GenreExposure);
}