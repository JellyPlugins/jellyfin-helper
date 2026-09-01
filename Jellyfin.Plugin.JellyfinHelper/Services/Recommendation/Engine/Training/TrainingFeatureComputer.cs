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
            if (!w.HasMeaningfulInteraction())
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
            if (!w.HasMeaningfulInteraction())
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

        var candidateGenreSet = new HashSet<string>(candidateGenres, StringComparer.OrdinalIgnoreCase);
        return ComputeTrainingTemporalAffinity(watchedItem, candidateGenreSet, userProfile, isDay);
    }

    /// <summary>
    ///     Core implementation that takes a prebuilt genre set.
    /// </summary>
    internal static double ComputeTrainingTemporalAffinity(
        WatchedItemInfo? watchedItem,
        HashSet<string> candidateGenreSet,
        UserWatchProfile userProfile,
        bool isDay)
    {
        if (watchedItem?.LastPlayedDate is null || candidateGenreSet.Count == 0)
        {
            return 0.5;
        }

        var watchDate = watchedItem.LastPlayedDate.Value;

        var (matchCount, totalInBucket) = CountBucketMatches(watchedItem, candidateGenreSet, userProfile, watchDate, isDay);

        if (totalInBucket < 3)
        {
            return 0.5;
        }

        return Math.Clamp((double)matchCount / totalInBucket, 0.0, 1.0);
    }

    /// <summary>
    ///     Counts, among the user's watch history in the same temporal bucket as watchDate, how many items share a genre with the candidate.
    /// </summary>
    /// <param name="watchedItem">The target watched item (excluded to avoid label leakage).</param>
    /// <param name="candidateGenreSet">The candidate's genre set.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="watchDate">The reference watch timestamp defining the bucket.</param>
    /// <param name="isDay">Whether to bucket by day-of-week (else by hour bucket).</param>
    /// <returns>The genre-match count and the total items in the bucket.</returns>
    private static (int MatchCount, int TotalInBucket) CountBucketMatches(
        WatchedItemInfo watchedItem,
        HashSet<string> candidateGenreSet,
        UserWatchProfile userProfile,
        DateTime watchDate,
        bool isDay)
    {
        var matchCount = 0;
        var totalInBucket = 0;

        foreach (var w in userProfile.WatchedItems)
        {
            // Match live scoring check for playback activity.
            if (!w.HasPlaybackActivity() || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            // Exclude the target itself or training would leak its own watch event.
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

        return (matchCount, totalInBucket);
    }

    /// <summary>
    ///     Builds a single aggregated TrainingExample from all episodes of a series.
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
        IReadOnlyDictionary<string, double> preferredFranchises,
        IReadOnlyDictionary<string, double> preferredCountries,
        HashSet<string> preferredInheritedTags,
        IReadOnlyDictionary<string, double> preferredWriterWeights,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        DateTime organicFallbackTimestamp,
        ContentScoring.SeriesAffinityContext? seriesAffinityContext = null)
    {
        // Use latest episode for temporal signals.
        var mostRecent = episodes
            .OrderByDescending(e => e.LastPlayedDate)
            .FirstOrDefault();

        var playedEps = episodes.Count(e => e.Played);
        var completionRatio = episodes.Count > 0
            ? Math.Clamp(
                episodes.Average(ContentScoring.ComputeCompletionRatio),
                0.0,
                1.0)
            : 0.0;

        var collabScore = ContentScoring.ComputeCollaborativeScore(seriesId, coOccurrence, collaborativeMax);
        var combinedCriticScore = ContentScoring.ComputeCombinedCriticScore(mostRecent?.CommunityRating, null);

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

        var allGenres = AggregateSeriesGenres(episodes, out var representativeYear);

        var genreList = allGenres.ToList();
        AggregateSeriesContentFields(
            episodes,
            out var seriesFranchise,
            out var seriesCountries,
            out var seriesInheritedTags,
            out var seriesWriters);

        // Exclude the series id and all its episode ids from genre engagement to prevent label leakage.
        // The series id is included because a watch record can be tagged with the series id rather than an episode id.
        var episodeIds = new HashSet<Guid>(episodes.Count + 1) { seriesId };
        foreach (var e in episodes)
        {
            episodeIds.Add(e.ItemId);
        }

        var (familiarity3, genreAvgCompletion3, genreAbandonRate3) = ContentScoring.ComputeGenreEngagement(genreList, userProfile, episodeIds);
        var userRatingScore3 = ContentScoring.ComputeGenreRatingScore(genreList, userProfile, episodeIds);

        // SeriesAffinity on the same basis as inference, excluding this series from the progressing-series
        // comparison so an aggregated example is not scored for affinity to itself (self-leakage).
        var seriesAffinity = seriesAffinityContext is not null
            ? ContentScoring.ComputeSeriesAffinity(true, seriesId, genreList, seriesAffinityContext, cachedPeopleLookup, excludeSeriesId: seriesId)
            : 0.0;

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(genreList, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = representativeYear is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1, 0, 0, 0, DateTimeKind.Utc))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(representativeYear, avgYear),
            GenreCount = genreList.Count,
            IsSeries = true,
            UserRatingScore = userRatingScore3,
            HasUserInteraction = familiarity3 > 0.0,
            CompletionRatio = genreAvgCompletion3,
            IsAbandoned = genreAbandonRate3,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesAffinity = seriesAffinity,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = ComputeTrainingTemporalAffinity(mostRecent, allGenres, userProfile, isDay: true),
            HourOfDayAffinity = ComputeTrainingTemporalAffinity(mostRecent, allGenres, userProfile, isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, mostRecent?.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = episodes
                .Select(e => e.DateCreated)
                .Where(d => d.HasValue)
                .Min() is { } minDate
                ? ContentScoring.ComputeRecencyScore(minDate)
                : 0.5,
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(seriesFranchise, preferredFranchises),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity([.. seriesCountries], preferredCountries),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity([.. seriesInheritedTags], preferredInheritedTags),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(true, mostRecent?.SeriesStatus, mostRecent?.EndDate.HasValue ?? false),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity([.. seriesWriters], preferredWriterWeights),
            BillingWeightedPeople = 0.0,
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(genreList, null, genreStudioIdf)
        };

        // Genre exposure features
        var (underexp, domRatio, affGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(genreList, genreExposure);
        features.GenreUnderexposure = underexp;
        features.GenreDominanceRatio = domRatio;
        features.GenreAffinityGap = affGap;

        // Label from aggregated completion.
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
                SampleWeight = 0.7,
                UserId = userProfile.UserId
            });
    }

    /// <summary>
    ///     Collects the union of non-blank genres across all episodes and the first available production year.
    /// </summary>
    /// <param name="episodes">The series' episodes.</param>
    /// <param name="representativeYear">Receives the first available episode production year.</param>
    /// <returns>The case-insensitive union of episode genres.</returns>
    private static HashSet<string> AggregateSeriesGenres(List<WatchedItemInfo> episodes, out int? representativeYear)
    {
        var allGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        representativeYear = null;
        foreach (var ep in episodes)
        {
            allGenres.UnionWith((ep.Genres ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)));

            // Use the first available production year as representative
            representativeYear ??= ep.Year;
        }

        return allGenres;
    }

    /// <summary>
    ///     Aggregates the content-affinity fields (first non-empty franchise; union of countries, inherited tags, writers) across a series' episodes.
    /// </summary>
    /// <param name="episodes">The series' episodes.</param>
    /// <param name="seriesFranchise">Receives the first non-empty TMDb collection name.</param>
    /// <param name="seriesCountries">Receives the union of production countries.</param>
    /// <param name="seriesInheritedTags">Receives the union of inherited tags.</param>
    /// <param name="seriesWriters">Receives the union of writer names.</param>
    private static void AggregateSeriesContentFields(
        List<WatchedItemInfo> episodes,
        out string? seriesFranchise,
        out HashSet<string> seriesCountries,
        out HashSet<string> seriesInheritedTags,
        out HashSet<string> seriesWriters)
    {
        seriesFranchise = null;
        seriesCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seriesInheritedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seriesWriters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in episodes)
        {
            if (seriesFranchise is null && !string.IsNullOrWhiteSpace(ep.TmdbCollectionName))
            {
                seriesFranchise = ep.TmdbCollectionName;
            }

            foreach (var c in ep.ProductionCountries.Where(static c => !string.IsNullOrWhiteSpace(c)))
            {
                seriesCountries.Add(c);
            }

            foreach (var t in ep.InheritedTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
            {
                seriesInheritedTags.Add(t);
            }

            foreach (var wn in ep.WriterNames.Where(static w => !string.IsNullOrWhiteSpace(w)))
            {
                seriesWriters.Add(wn);
            }
        }
    }

    /// <summary>
    ///     Computes tag similarity from cached lists.
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
    ///     Rebuilds the billing weight map from cached name and weight lists.
    /// </summary>
    /// <param name="peopleNames">Cached names.</param>
    /// <param name="peopleWeights">Cached weights aligned to names.</param>
    /// <returns>A name to weight map.</returns>
    internal static Dictionary<string, double> BuildBillingMapFromCache(
        IReadOnlyList<string> peopleNames,
        IReadOnlyList<double> peopleWeights)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (peopleNames.Count == 0 || peopleNames.Count != peopleWeights.Count)
        {
            return map;
        }

        for (var i = 0; i < peopleNames.Count; i++)
        {
            var name = peopleNames[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var weight = peopleWeights[i];
            if (!map.TryGetValue(name, out var existing) || weight > existing)
            {
                map[name] = weight;
            }
        }

        return map;
    }

    /// <summary>
    ///     Computes ContentNearestNeighborScore from cached data.
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
    ///     Computes subtitle language affinity from cached data.
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
    ///     Computes audio language affinity from cached data.
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
    ///     Core language affinity logic.
    /// </summary>
    /// <param name="candidateLanguages">Candidate languages.</param>
    /// <param name="primaryLang">Primary language.</param>
    /// <param name="preferredLangs">Preferred languages.</param>
    /// <param name="toleratedLangs">Tolerated languages.</param>
    /// <param name="languageProfile">Full language profile.</param>
    /// <returns>Score between 0.1 and 1.0.</returns>
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
