using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

#pragma warning disable SA1611 // Element parameters should be documented
#pragma warning disable SA1615 // Element return value should be documented

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>
///     Static helper methods for computing features from cached recommendation data during training.
/// </summary>
internal static class TrainingFeatureComputer
{
    /// <summary>
    ///     Builds a set of preferred studio names for a user from a precomputed item-to-studios lookup.
    ///     Collects studios from items the user has watched (matched by item ID or series ID).
    ///     This mirrors <see cref="PreferenceBuilder.BuildStudioPreferenceSet"/> but uses cached data
    ///     instead of live BaseItem objects.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="itemStudiosLookup">Precomputed itemId ? studios mapping built once from all previous results.</param>
    internal static HashSet<string> BuildStudioPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.IsFavorite && !w.Played && w.PlayCount <= 0)
            {
                continue;
            }

            // Look up studios by the item's own ID
            if (itemStudiosLookup.TryGetValue(w.ItemId, out var itemStudios))
            {
                foreach (var s in itemStudios.Where(static s => !string.IsNullOrWhiteSpace(s)))
                {
                    studios.Add(s);
                }
            }

            // Also look up studios by the item's series ID (episodes ? series mapping)
            if (!w.SeriesId.HasValue ||
                !itemStudiosLookup.TryGetValue(w.SeriesId.Value, out var seriesStudios))
            {
                continue;
            }

            foreach (var s in seriesStudios.Where(static s => !string.IsNullOrWhiteSpace(s)))
            {
                studios.Add(s);
            }
        }

        return studios;
    }

    /// <summary>
    ///     Builds a set of preferred tag names for a user from a precomputed item-to-tags lookup.
    ///     This mirrors <see cref="PreferenceBuilder.BuildTagPreferenceSet"/> but uses cached data.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="itemTagsLookup">Precomputed itemId ? tags mapping built once from all previous results.</param>
    internal static HashSet<string> BuildTagPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.IsFavorite && !w.Played && w.PlayCount <= 0)
            {
                continue;
            }

            // Look up tags by the item's own ID
            if (itemTagsLookup.TryGetValue(w.ItemId, out var itemTags))
            {
                foreach (var t in itemTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                {
                    tags.Add(t);
                }
            }

            // Also look up tags by the item's series ID (episodes ? series mapping)
            if (!w.SeriesId.HasValue || !itemTagsLookup.TryGetValue(w.SeriesId.Value, out var seriesTags))
            {
                continue;
            }

            foreach (var t in seriesTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
            {
                tags.Add(t);
            }
        }

        return tags;
    }

    /// <summary>
    ///     Computes temporal affinity for training examples using the actual watch timestamp.
    ///     Instead of setting temporal features to neutral (0.5), uses the real DayOfWeek/HourOfDay
    ///     from when the user watched the item. This allows the model to learn temporal weights.
    /// </summary>
    /// <param name="watchedItem">The watched item (may be null for unmatched items).</param>
    /// <param name="candidateGenres">The candidate item's genres.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="isDay">True for day-of-week affinity, false for hour-of-day affinity.</param>
    /// <returns>A temporal affinity score between 0 and 1, or 0.5 if no timestamp is available.</returns>
    internal static double ComputeTrainingTemporalAffinity(
        WatchedItemInfo? watchedItem,
        IReadOnlyList<string>? candidateGenres,
        UserWatchProfile userProfile,
        bool isDay)
    {
        if (watchedItem?.LastPlayedDate is null || candidateGenres is null || candidateGenres.Count == 0)
        {
            return 0.5;
        }

        var watchDate = watchedItem.LastPlayedDate.Value;
        var candidateGenreSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);

        var matchCount = 0;
        var totalInBucket = 0;

        foreach (var w in userProfile.WatchedItems)
        {
            // Use HasPlaybackActivity() to match TemporalFeatures.ComputeDayOfWeekAffinity/
            // ComputeHourOfDayAffinity scoring logic (includes PlayCount > 0 and
            // PlaybackPositionTicks > 0, not just Played). Ensures consistent temporal
            // bucket populations between training and scoring.
            if (!w.HasPlaybackActivity() || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            // Exclude the target item itself to prevent label leakage: during live scoring,
            // the candidate is never in the user's watch history, so including it here would
            // inflate the temporal affinity signal with its own watch event during training.
            if (w.ItemId == watchedItem.ItemId)
            {
                continue;
            }

            var inBucket = isDay
                ? w.LastPlayedDate.Value.DayOfWeek == watchDate.DayOfWeek
                : TemporalFeatures.GetTimeBucket(w.LastPlayedDate.Value.Hour)
                  == TemporalFeatures.GetTimeBucket(watchDate.Hour);

            if (!inBucket)
            {
                continue;
            }

            totalInBucket++;
            if (w.Genres is not null && w.Genres.Any(candidateGenreSet.Contains))
            {
                matchCount++;
            }
        }

        if (totalInBucket < 3)
        {
            return 0.5;
        }

        return Math.Clamp((double)matchCount / totalInBucket, 0.0, 1.0);
    }

    /// <summary>
    ///     Builds a single aggregated TrainingExample from all episodes of a series.
    ///     Instead of emitting one example per episode (which skews the dataset toward
    ///     series with many episodes), this collapses all episodes into one series-level
    ///     example with averaged/aggregated signals matching Engine.ScoreCandidate() logic.
    /// </summary>
    internal static void AddAggregatedSeriesExample(
        List<TrainingExample> examples,
        List<WatchedItemInfo> episodes,
        Guid seriesId,
        UserWatchProfile userProfile,
        Dictionary<string, double> genrePreferences,
        Dictionary<Guid, double> coOccurrence,
        double collaborativeMax,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        IReadOnlyDictionary<string, double> preferredPeopleWeights,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        HashSet<string> preferredStudios,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        HashSet<string> preferredTags,
        DateTime organicFallbackTimestamp)
    {
        // Use the most-recently-watched episode for temporal features (mirrors Phase 1 series logic)
        var mostRecent = episodes
            .OrderByDescending(e => e.LastPlayedDate)
            .FirstOrDefault();

        // Aggregated completion: average per-episode completion ratios.
        // Using ContentScoring.ComputeCompletionRatio per episode (same as Phase 1 series scoring)
        // instead of binary playedEps/totalEps, so partially watched episodes contribute proportionally
        // rather than being counted as 0.
        var playedEps = episodes.Count(e => e.Played);
        var completionRatio = episodes.Count > 0
            ? Math.Clamp(
                episodes.Average(ContentScoring.ComputeCompletionRatio),
                0.0,
                1.0)
            : 0.0;

        // Aggregated user rating: average of all rated episodes
        var ratedEpisodes = episodes.Where(e => e.UserRating is > 0).ToList();
        var userRatingScore = ratedEpisodes.Count > 0
            ? Math.Clamp(ratedEpisodes.Average(e => e.UserRating!.Value) / 10.0, 0.0, 1.0)
            : 0.5;

        // Use seriesId for collaborative score (matches Phase 1 series scoring)
        var collabScore = ContentScoring.ComputeCollaborativeScore(seriesId, coOccurrence, collaborativeMax);
        var combinedCriticScore = ContentScoring.ComputeCombinedCriticScore(mostRecent?.CommunityRating, null);

        // Series progression boost: hardcoded 0.0 to mirror the live inference path
        // (Engine.ScoreCandidate writes a constant 0.0 for this channel). Aggregated series
        // examples describe series the user has already interacted with meaningfully, so the
        // watchedSeriesIds filter permanently excludes them from live candidate scoring —
        // emitting a graded value here would train a signal the network can never observe.
        const double seriesProgressionBoost = 0.0;

        // PeopleSimilarity: try seriesId first (most likely hit for series-level metadata).
        // Roadmap v3 (C2): weighted overload for train/serve parity with Engine.ScoreCandidate.
        var peopleSimilarity = cachedPeopleLookup.TryGetValue(seriesId, out var seriesPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(seriesPeople, preferredPeopleWeights)
            : 0.0;

        // StudioMatch and TagSimilarity: look up by seriesId
        var studioMatch = false;
        var tagSimilarity = 0.0;

        if (itemStudiosLookup.TryGetValue(seriesId, out var seriesStudios) && seriesStudios.Count > 0)
        {
            studioMatch = seriesStudios.Any(preferredStudios.Contains);
        }

        if (itemTagsLookup.TryGetValue(seriesId, out var seriesTags) && seriesTags.Count > 0)
        {
            tagSimilarity = ComputeTagSimilarityFromCache(seriesTags, preferredTags);
        }

        // Collect all unique genres across episodes for genre similarity
        var allGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? representativeYear = null;
        foreach (var ep in episodes)
        {
            foreach (var g in ep.Genres ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(g))
                {
                    allGenres.Add(g);
                }
            }

            // Use the first available production year as representative
            representativeYear ??= ep.Year;
        }

        var genreList = allGenres.ToList();

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genreList, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            // Use production year for recency (not watch date) to match Phase 1 semantics
            RecencyScore = representativeYear is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(representativeYear, avgYear),
            GenreCount = genreList.Count,
            IsSeries = true,
            // Train/serve parity: aggregated series examples are excluded from live scoring by the
            // watchedSeriesIds filter in Engine.GenerateForUser (a series with meaningful episode
            // interaction never re-enters the candidate pool). Feeding real per-episode averages
            // for UserRatingScore / HasUserInteraction therefore trains signals the model can
            // never see at inference. The engagement label below still carries the positive signal.
            UserRatingScore = 0.5,
            HasUserInteraction = false,
            CompletionRatio = completionRatio,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = ComputeTrainingTemporalAffinity(mostRecent, genreList, userProfile, isDay: true),
            HourOfDayAffinity = ComputeTrainingTemporalAffinity(mostRecent, genreList, userProfile, isDay: false),
            // Shared IsWeekend resolver: user-anchored, falls back to the most recently played
            // episode's LastPlayedDate when the profile carries no anchor yet.
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, mostRecent?.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = episodes
                .Select(e => e.DateCreated)
                .Where(d => d.HasValue)
                .Min() is { } minDate
                ? ContentScoring.ComputeRecencyScore(minDate)
                : 0.5,
            // Language affinity features: neutral (0.5) for aggregated series because
            // WatchedItemInfo does not carry per-episode stream metadata. The live scoring
            // path also returns neutral for Series candidates (GetMediaStreams() is empty
            // on folder-type items), maintaining train/serve parity.
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5
        };

        // Genre exposure features
        var (underexp, domRatio, affGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genreList, genreExposure);
        features.GenreUnderexposure = underexp;
        features.GenreDominanceRatio = domRatio;
        features.GenreAffinityGap = affGap;

        // Label based on aggregated completion:
        // - No episodes played (all favorite-only): 0.65 (explicit interest)
        // - Low completion (started but abandoned most episodes): AbandonedLabel (0.0)
        // - Normal completion: engagement-proportional (0.5–0.85)
        // When playedEps == 0 and no episode has playback progress or favorites,
        // completionRatio is 0.0 → ComputeEngagementLabel yields WatchedLabelFloor (0.5).
        // This case implies PlayCount > 0 only items from HasMeaningfulInteraction() filtering.
        var label = playedEps switch
        {
            0 when episodes.Any(e => e.PlaybackPositionTicks > 0) => completionRatio <
                                                                     EngineConstants.AbandonedCompletionThreshold
                ? EngineConstants.AbandonedLabel
                : ContentScoring.ComputeEngagementLabel(completionRatio),
            0 when episodes.Any(e => e.IsFavorite) => 0.65,
            _ => ContentScoring.ComputeEngagementLabel(completionRatio)
        };

        examples.Add(
            new TrainingExample
            {
                Features = features,
                Label = label,
                GeneratedAtUtc = mostRecent?.LastPlayedDate ?? organicFallbackTimestamp,
                SampleWeight = 0.7 // Slightly lower weight than recommended items to avoid overwhelming
            });
    }

    /// <summary>
    ///     Computes tag similarity from cached tag lists using Jaccard similarity.
    ///     This mirrors <see cref="SimilarityComputer.ComputeTagSimilarity"/> but works with
    ///     <see cref="IReadOnlyList{T}"/> instead of <see cref="MediaBrowser.Controller.Entities.BaseItem"/>.
    /// </summary>
    internal static double ComputeTagSimilarityFromCache(
        IReadOnlyList<string> candidateTags,
        HashSet<string> preferredTags)
    {
        if (candidateTags.Count == 0 || preferredTags.Count == 0)
        {
            return 0.0;
        }

        var candidateSet = new HashSet<string>(candidateTags, StringComparer.OrdinalIgnoreCase);
        return SimilarityComputer.ComputeJaccardFromSets(candidateSet, preferredTags);
    }

    /// <summary>
    ///     Computes ContentNearestNeighborScore from cached recommendation data.
    ///     Mirrors <see cref="ContentScoring.ComputeContentNearestNeighborScore"/> but works with
    ///     <see cref="IReadOnlyList{T}"/> from cached <see cref="RecommendedItem"/> data.
    ///     Returns 0.0 when no watched items or candidate metadata is available.
    /// </summary>
    internal static double ComputeContentNearestNeighborFromCache(
        IReadOnlyList<string> candidateGenres,
        IReadOnlyList<string> candidatePeople,
        IReadOnlyList<string> candidateStudios,
        List<HashSet<string>> watchedGenreSets,
        List<HashSet<string>> watchedPeopleSets,
        List<HashSet<string>> watchedStudioSets)
    {
        if (watchedGenreSets.Count == 0 || candidateGenres.Count == 0)
        {
            return 0.0;
        }

        var candidateGenreSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? candidatePeopleSet = candidatePeople.Count > 0
            ? new HashSet<string>(candidatePeople, StringComparer.OrdinalIgnoreCase)
            : null;
        HashSet<string>? candidateStudioSet = candidateStudios.Count > 0
            ? new HashSet<string>(candidateStudios, StringComparer.OrdinalIgnoreCase)
            : null;

        return ContentScoring.ComputeContentNearestNeighborScore(
            candidateGenreSet,
            candidatePeopleSet,
            candidateStudioSet,
            watchedGenreSets,
            watchedPeopleSets,
            watchedStudioSets);
    }

    /// <summary>
    ///     Computes SubtitleLanguageAffinity from cached subtitle language data stored on <see cref="RecommendedItem"/>.
    ///     Delegates to <see cref="ComputeBestLanguageAffinity"/> for the core scoring logic.
    ///     Returns 0.5 (neutral) when no subtitle language data is available on either side.
    /// </summary>
    internal static double ComputeSubtitleLanguageAffinityFromCache(
        IReadOnlyList<string> candidateSubtitleLanguages,
        UserWatchProfile userProfile)
    {
        if (candidateSubtitleLanguages.Count == 0 || userProfile.SubtitleLanguageProfile.Count == 0)
        {
            return 0.5;
        }

        return ComputeBestLanguageAffinity(
            candidateSubtitleLanguages,
            userProfile.PrimarySubtitleLanguage,
            userProfile.PreferredSubtitleLanguages,
            userProfile.ToleratedSubtitleLanguages,
            userProfile.SubtitleLanguageProfile);
    }

    /// <summary>
    ///     Computes LanguageAffinity from cached audio language data stored on <see cref="RecommendedItem"/>.
    ///     Delegates to <see cref="ComputeBestLanguageAffinity"/> for the core scoring logic.
    ///     Returns 0.5 (neutral) when no language data is available on either side.
    /// </summary>
    internal static double ComputeLanguageAffinityFromCache(
        IReadOnlyList<string> candidateAudioLanguages,
        UserWatchProfile userProfile)
    {
        if (candidateAudioLanguages.Count == 0 || userProfile.LanguageProfile.Count == 0)
        {
            return 0.5;
        }

        return ComputeBestLanguageAffinity(
            candidateAudioLanguages,
            userProfile.PrimaryLanguage,
            userProfile.PreferredLanguages,
            userProfile.ToleratedLanguages,
            userProfile.LanguageProfile);
    }

    /// <summary>
    ///     Core language affinity scoring logic shared between live scoring (<see cref="Engine.ComputeLanguageAffinity"/>)
    ///     and training (<see cref="ComputeLanguageAffinityFromCache"/>).
    ///     Scores how well the candidate's audio languages match the user's language profile.
    ///     Uses the chosen-vs-forced distinction: primary = 1.0, preferred = 0.85, tolerated = 0.5,
    ///     known = 0.3, unknown = 0.1.
    /// </summary>
    /// <param name="candidateLanguages">The candidate's available audio language codes.</param>
    /// <param name="primaryLang">The user's primary (most-watched) language.</param>
    /// <param name="preferredLangs">Languages the user actively chooses.</param>
    /// <param name="toleratedLangs">Languages the user watches only when forced.</param>
    /// <param name="languageProfile">The full language profile (language → watch data).</param>
    /// <returns>A language affinity score between 0.1 and 1.0.</returns>
    internal static double ComputeBestLanguageAffinity(
        IEnumerable<string> candidateLanguages,
        string? primaryLang,
        IReadOnlySet<string> preferredLangs,
        IReadOnlySet<string> toleratedLangs,
        IDictionary<string, LanguageProfileEntry> languageProfile)
    {
        var bestAffinity = 0.1;

        foreach (var lang in candidateLanguages)
        {
            double affinity;

            if (string.Equals(lang, primaryLang, StringComparison.OrdinalIgnoreCase))
            {
                affinity = 1.0;
            }
            else if (preferredLangs.Contains(lang))
            {
                affinity = 0.85;
            }
            else if (toleratedLangs.Contains(lang))
            {
                affinity = 0.5;
            }
            else if (languageProfile.ContainsKey(lang))
            {
                affinity = 0.3;
            }
            else
            {
                affinity = 0.1;
            }

            if (affinity > bestAffinity)
            {
                bestAffinity = affinity;
            }

            if (bestAffinity >= 1.0)
            {
                break;
            }
        }

        return bestAffinity;
    }
}
