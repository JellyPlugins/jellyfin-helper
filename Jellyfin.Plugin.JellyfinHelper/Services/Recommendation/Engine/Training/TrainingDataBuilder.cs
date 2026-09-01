using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using PerUserArtifacts = (
    System.Collections.Generic.Dictionary<string, double> GenrePreferences,
    System.Collections.Generic.Dictionary<System.Guid, double> CoOccurrence,
    double CollaborativeMax,
    double AvgYear,
    Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.PreferenceBuilder.GenreExposureAnalysis GenreExposure,
    System.Collections.Generic.IReadOnlyDictionary<string, double> PeopleWeights,
    System.Collections.Generic.HashSet<string> PreferredStudios,
    System.Collections.Generic.HashSet<string> PreferredTags,
    System.Collections.Generic.IReadOnlyDictionary<string, double> PreferredFranchises,
    System.Collections.Generic.IReadOnlyDictionary<string, double> PreferredCountries,
    System.Collections.Generic.HashSet<string> PreferredInheritedTags,
    System.Collections.Generic.IReadOnlyDictionary<string, double> PreferredWriterWeights);

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>
///     Builds labelled training examples from previous recommendation results and user watch data.
/// </summary>
internal static class TrainingDataBuilder
{
    /// <summary>
    ///     Builds all training examples from previous results and user profiles.
    /// </summary>
    /// <param name="previousResults">The recommendation results from previous runs.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A tuple of training examples plus per-phase counts.</returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount, int DiscoveryCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        CancellationToken cancellationToken)
    {
        return BuildExamples(previousResults, allProfiles, discoveryFeedback: null, seriesEpisodeCounts: null, genreStudioIdf: null, cancellationToken);
    }

    /// <summary>
    ///     Builds all training examples from previous results, user profiles, and optional discovery feedback.
    /// </summary>
    /// <param name="previousResults">The recommendation results from previous runs.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="discoveryFeedback">Optional discovery feedback data for Phase 4.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    ///     A tuple with the training examples and three separate counters - organic watches (Phase 2),
    ///     cross-user random negatives (Phase 3) and discovery interactions (Phase 4). Splitting the
    ///     discovery counter out of <c>OrganicCount</c> lets operators see at a glance whether the
    ///     positive signal comes from actual consumption or external Seerr requests (very different
    ///     implications for training-data health).
    /// </returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount, int DiscoveryCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        IReadOnlyList<DiscoveryFeedbackResult>? discoveryFeedback,
        CancellationToken cancellationToken)
    {
        return BuildExamples(previousResults, allProfiles, discoveryFeedback, seriesEpisodeCounts: null, genreStudioIdf: null, cancellationToken);
    }

    /// <summary>
    ///     Builds all training examples from previous results, user profiles, optional discovery
    ///     feedback, and the per-series total-episode-count map.
    /// </summary>
    /// <param name="previousResults">The recommendation results from previous runs.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="discoveryFeedback">Optional discovery feedback data for Phase 4.</param>
    /// <param name="seriesEpisodeCounts">
    ///     Per series total episode count. When supplied the same progression multiplier as inference is used so training and inference see the same features. Null or empty means neutral weight.
    /// </param>
    /// <param name="genreStudioIdf">
    ///     Library wide genre and studio rarity table. Same table as inference so GenreStudioIdfPrior matches. Null means neutral 0.0 on both sides.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    ///     Training examples and counts per phase. Discovery is counted separately so you can tell if positives come from watch history or from Seerr requests.
    /// </returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount, int DiscoveryCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        IReadOnlyList<DiscoveryFeedbackResult>? discoveryFeedback,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profileLookup = BuildProfileLookup(allProfiles);

        cancellationToken.ThrowIfCancellationRequested();

        var seriesLookup = BuildSeriesLookup(allProfiles);

        // Pre-compute collaborative data for all users (needed for full feature vectors)
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        var cachedPeopleLookup = BuildCachedPeopleLookup(previousResults);

        BuildItemMetadataLookups(
            previousResults,
            out var itemStudiosLookup,
            out var itemTagsLookup,
            out var itemBoxSetIdsLookup);

        var lookups = new TrainingLookups(
            cachedPeopleLookup,
            itemStudiosLookup,
            itemTagsLookup,
            itemBoxSetIdsLookup,
            genreStudioIdf);

        var examples = new List<TrainingExample>();

        BuildPerUserCache(
            allProfiles,
            precomputedUserSets,
            lookups,
            seriesEpisodeCounts,
            out var perUserCache,
            out var profileById);

        var recommendedItemIdsByUser = BuildRecommendedItemIdsByUser(previousResults);

        // Stable fallback for organic items without a date. Use earliest GeneratedAt so the value is deterministic.
        var organicFallbackTimestamp = previousResults.Count > 0
            ? previousResults.Min(r => r.GeneratedAt)
            : DateTime.UtcNow.AddDays(-90);

        // Bundle shared context so each phase needs only one argument.
        var ctx = new TrainingContext
        {
            PreviousResults = previousResults,
            AllProfiles = allProfiles,
            ProfileLookup = profileLookup,
            SeriesLookup = seriesLookup,
            ProfileById = profileById,
            RecommendedItemIdsByUser = recommendedItemIdsByUser,
            PerUserCache = perUserCache,
            Lookups = lookups,
            OrganicFallbackTimestamp = organicFallbackTimestamp,
            Examples = examples,
            CancellationToken = cancellationToken,
            SeriesEpisodeCounts = seriesEpisodeCounts
        };

        EmitRecommendationFeedbackExamples(ctx);

        var organicCount = EmitOrganicExamples(ctx);

        var randomNegativeCount = EmitRandomNegativeExamples(ctx);

        // Discovery items are external. Requests are strong positives and dismissals are negatives.
        var discoveryCount = 0;
        if (discoveryFeedback is { Count: > 0 })
        {
            var (discoveryExamples, phase4Count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
                discoveryFeedback,
                profileById,
                seriesEpisodeCounts,
                cancellationToken);

            if (discoveryExamples.Count > 0)
            {
                examples.AddRange(discoveryExamples);
                discoveryCount = phase4Count;
            }
        }

        return (examples, organicCount, randomNegativeCount, discoveryCount);
    }

    /// <summary>
    ///     Builds the per-user set of meaningfully-interacted item IDs.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> BuildProfileLookup(Collection<UserWatchProfile> allProfiles)
    {
        // Favorited items count as positive even without playback.
        var profileLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            profileLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => w.HasMeaningfulInteraction())
                    .Select(w => w.ItemId));
        }

        return profileLookup;
    }

    /// <summary>
    ///     Builds the per-user set of meaningfully-interacted (and favorited) series IDs.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> BuildSeriesLookup(Collection<UserWatchProfile> allProfiles)
    {
        var seriesLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            var seriesIds = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => w.HasMeaningfulInteraction() && w.SeriesId.HasValue)
                    .Select(w => w.SeriesId!.Value));

            // Include series favorites as well.
            foreach (var favSeriesId in profile.FavoriteSeriesIds)
            {
                seriesIds.Add(favSeriesId);
            }

            seriesLookup[profile.UserId] = seriesIds;
        }

        return seriesLookup;
    }

    /// <summary>
    ///     Builds a people lookup from cached recommendation data (PeopleNames stored on RecommendedItem).
    /// </summary>
    private static Dictionary<Guid, HashSet<string>> BuildCachedPeopleLookup(
        IReadOnlyList<RecommendationResult> previousResults)
    {
        var cachedPeopleLookup = new Dictionary<Guid, HashSet<string>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations.Where(r => r.PeopleNames.Count > 0))
            {
                cachedPeopleLookup.TryAdd(
                    rec.ItemId,
                    new HashSet<string>(rec.PeopleNames, StringComparer.OrdinalIgnoreCase));
            }
        }

        return cachedPeopleLookup;
    }

    /// <summary>
    ///     Pre-computes itemId to studios / tags / BoxSet lookups ONCE from all previous results.
    /// </summary>
    private static void BuildItemMetadataLookups(
        IReadOnlyList<RecommendationResult> previousResults,
        out Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        out Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        out Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup)
    {
        // Build id lookups once from all previous results.
        itemStudiosLookup = new Dictionary<Guid, IReadOnlyList<string>>();
        itemTagsLookup = new Dictionary<Guid, IReadOnlyList<string>>();
        itemBoxSetIdsLookup = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                if (rec.Studios.Count > 0)
                {
                    itemStudiosLookup.TryAdd(rec.ItemId, rec.Studios);
                }

                if (rec.Tags.Count > 0)
                {
                    itemTagsLookup.TryAdd(rec.ItemId, rec.Tags);
                }

                if (rec.BoxSetIds.Count > 0)
                {
                    itemBoxSetIdsLookup.TryAdd(rec.ItemId, rec.BoxSetIds);
                }
            }
        }
    }

    /// <summary>
    ///     Pre-computes per-user artifacts once and caches them, plus a profile-by-id lookup.
    /// </summary>
    private static void BuildPerUserCache(
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, HashSet<Guid>> precomputedUserSets,
        TrainingLookups lookups,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        out Dictionary<Guid, PerUserArtifacts> perUserCache,
        out Dictionary<Guid, UserWatchProfile> profileById)
    {
        // Cache per user artifacts to reuse across all phases.
        perUserCache = new Dictionary<Guid, PerUserArtifacts>();

        // Fast profile lookup by user id.
        profileById = new Dictionary<Guid, UserWatchProfile>(allProfiles.Count);

        foreach (var profile in allProfiles)
        {
            var gp = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
            var co = CollaborativeFilter.BuildCollaborativeMap(profile, allProfiles, precomputedUserSets);
            var cm = co.Values.Where(v => double.IsFinite(v) && v > 0.0).DefaultIfEmpty(0.0).Max();

            var ay = ContentScoring.ComputeAverageYear(profile);
            var ge = PreferenceBuilder.BuildGenreExposureAnalysis(gp, profile);
            var pw = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookups.CachedPeopleLookup, seriesEpisodeCounts);
            var ps = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookups.ItemStudiosLookup);
            var pt = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, lookups.ItemTagsLookup);
            // Same PreferenceBuilder helpers as live scoring.
            var pf = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
            var pc = PreferenceBuilder.BuildProductionCountryPreferenceVector(profile);
            var pit = PreferenceBuilder.BuildInheritedTagPreferenceSet(profile);
            var pww = PreferenceBuilder.BuildWriterPreferenceWeights(profile);
            perUserCache[profile.UserId] = (gp, co, cm, ay, ge, pw, ps, pt, pf, pc, pit, pww);
            profileById[profile.UserId] = profile;
        }
    }

    /// <summary>
    ///     Phase 1: emits recommendation-feedback training examples into <c>ctx.Examples</c>.
    /// </summary>
    private static void EmitRecommendationFeedbackExamples(TrainingContext ctx)
    {
        foreach (var prevResult in ctx.PreviousResults)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            if (!ctx.ProfileLookup.TryGetValue(prevResult.UserId, out var watchedIds))
            {
                continue;
            }

            ctx.SeriesLookup.TryGetValue(prevResult.UserId, out var watchedSeriesIds);

            if (!ctx.ProfileById.TryGetValue(prevResult.UserId, out var userProfile))
            {
                continue;
            }

            var artifacts = ctx.PerUserCache[userProfile.UserId];

            var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
            foreach (var w in userProfile.WatchedItems)
            {
                watchedItemLookup.TryAdd(w.ItemId, w);
            }

            var seriesEpisodeLookup = BuildSeriesEpisodeLookup(userProfile);

            var (watchedGenreSets, watchedPeopleSets, watchedStudioSets) =
                BuildWatchedContentSets(userProfile, ctx.Lookups);

            // Same BoxSet counting as live scoring.
            var watchedBoxSetCounts = BuildWatchedBoxSetCounts(
                BuildWatchedIdSet(watchedIds, watchedSeriesIds),
                ctx.Lookups.ItemBoxSetIdsLookup);

            var contentSets = new WatchedContentSets(
                watchedGenreSets,
                watchedPeopleSets,
                watchedStudioSets,
                watchedBoxSetCounts);

            var userCtx = new Phase1UserContext
            {
                UserProfile = userProfile,
                WatchedIds = watchedIds,
                WatchedSeriesIds = watchedSeriesIds,
                WatchedItemLookup = watchedItemLookup,
                SeriesEpisodeLookup = seriesEpisodeLookup,
                Artifacts = artifacts,
                ContentSets = contentSets,
                SeriesAffinity = ctx.SeriesEpisodeCounts is not null
                    ? ContentScoring.BuildSeriesAffinityContext(userProfile, ctx.SeriesEpisodeCounts)
                    : null
            };

            foreach (var rec in prevResult.Recommendations)
            {
                ctx.Examples.Add(
                    BuildRecommendationFeedbackExample(rec, prevResult, userCtx, ctx.Lookups));
            }
        }
    }

    /// <summary>
    ///     Builds the per-series episode lookup for a single user's watched items.
    /// </summary>
    private static Dictionary<Guid, List<WatchedItemInfo>> BuildSeriesEpisodeLookup(UserWatchProfile userProfile)
    {
        var seriesEpisodeLookup = new Dictionary<Guid, List<WatchedItemInfo>>();
        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.SeriesId.HasValue)
            {
                continue;
            }

            if (!seriesEpisodeLookup.TryGetValue(w.SeriesId.Value, out var list))
            {
                list = [];
                seriesEpisodeLookup[w.SeriesId.Value] = list;
            }

            list.Add(w);
        }

        return seriesEpisodeLookup;
    }

    /// <summary>
    ///     Builds the set of item ids to exclude from a series' own genre engagement: every watched
    ///     episode of the series plus the series id itself. A series example must not draw familiarity,
    ///     completion or abandon signal from its own episodes, which would leak the label.
    /// </summary>
    private static HashSet<Guid> BuildEpisodeExcludeSet(Guid seriesId, List<WatchedItemInfo> episodes)
    {
        var set = new HashSet<Guid>(episodes.Count + 1) { seriesId };
        foreach (var e in episodes)
        {
            set.Add(e.ItemId);
        }

        return set;
    }

    /// <summary>
    ///     Builds the parallel watched genre/people/studio sets (indexed by meaningfully-interacted watched item) that feed ContentNearestNeighborScore.
    /// </summary>
    private static (List<HashSet<string>> Genre, List<HashSet<string>> People, List<HashSet<string>> Studio)
        BuildWatchedContentSets(UserWatchProfile userProfile, TrainingLookups lookups)
    {
        var watchedGenreSets = new List<HashSet<string>>();
        var watchedPeopleSets = new List<HashSet<string>>();
        var watchedStudioSets = new List<HashSet<string>>();
        foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
        {
            watchedGenreSets.Add(
                w.Genres is { Count: > 0 }
                    ? new HashSet<string>(w.Genres, StringComparer.OrdinalIgnoreCase)
                    : []);

            watchedPeopleSets.Add(
                lookups.CachedPeopleLookup.TryGetValue(w.ItemId, out var wp) ? wp : []);

            HashSet<string> studioSet = [];
            if (lookups.ItemStudiosLookup.TryGetValue(w.ItemId, out var ws) && ws.Count > 0)
            {
                studioSet = new HashSet<string>(ws, StringComparer.OrdinalIgnoreCase);
            }
            else if (w.SeriesId.HasValue
                     && lookups.ItemStudiosLookup.TryGetValue(w.SeriesId.Value, out var ss) && ss.Count > 0)
            {
                studioSet = new HashSet<string>(ss, StringComparer.OrdinalIgnoreCase);
            }

            watchedStudioSets.Add(studioSet);
        }

        return (watchedGenreSets, watchedPeopleSets, watchedStudioSets);
    }

    /// <summary>
    ///     Builds the per-user watched-BoxSet count map by resolving each watched id through the global
    ///     BoxSet lookup. Shared by Phase 1 and Phase 3.
    /// </summary>
    private static Dictionary<Guid, int> BuildWatchedBoxSetCounts(
        HashSet<Guid> watchedIdSet,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup)
    {
        var watchedBoxSetCounts = new Dictionary<Guid, int>();
        foreach (var watchedId in watchedIdSet)
        {
            if (!itemBoxSetIdsLookup.TryGetValue(watchedId, out var watchedBoxSetIds))
            {
                continue;
            }

            foreach (var boxSetId in watchedBoxSetIds)
            {
                watchedBoxSetCounts.TryGetValue(boxSetId, out var count);
                watchedBoxSetCounts[boxSetId] = count + 1;
            }
        }

        return watchedBoxSetCounts;
    }

    /// <summary>
    ///     Builds a single Phase 1 recommendation-feedback training example for one recommended item.
    /// </summary>
    private static TrainingExample BuildRecommendationFeedbackExample(
        RecommendedItem rec,
        RecommendationResult prevResult,
        Phase1UserContext userCtx,
        TrainingLookups lookups)
    {
        var userProfile = userCtx.UserProfile;
        var watchedIds = userCtx.WatchedIds;
        var watchedSeriesIds = userCtx.WatchedSeriesIds;
        var watchedItemLookup = userCtx.WatchedItemLookup;
        var seriesEpisodeLookup = userCtx.SeriesEpisodeLookup;
        var contentSets = userCtx.ContentSets;

        var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposure, preferredPeopleWeights, preferredStudios, preferredTags,
            preferredFranchises, preferredCountries, preferredInheritedTags, preferredWriterWeights) = userCtx.Artifacts;

        var wasWatched = watchedIds.Contains(rec.ItemId)
                         || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);

        watchedItemLookup.TryGetValue(rec.ItemId, out var watchedItemForRec);

        var isSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        // Use latest episode for series temporal signals.
        List<WatchedItemInfo>? episodesForSeries = null;
        if (isSeries && seriesEpisodeLookup.TryGetValue(rec.ItemId, out var eps))
        {
            episodesForSeries = eps;
            watchedItemForRec = episodesForSeries
                .Where(e => e.HasPlaybackActivity())
                .OrderByDescending(e => e.LastPlayedDate)
                .FirstOrDefault();
        }

        // For a series the watched records are its episodes (ItemId == episodeId), not the series id.
        // Exclude every episode id so the series example cannot draw genre engagement from its own
        // watch history. At inference a scored series is filtered out upstream and contributes nothing,
        // so training must match by excluding it here too.
        var engagementExclude = episodesForSeries is not null
            ? BuildEpisodeExcludeSet(rec.ItemId, episodesForSeries)
            : new HashSet<Guid> { rec.ItemId };
        var (familiarity, genreAvgCompletion, genreAbandonRate) = ContentScoring.ComputeGenreEngagement(
            rec.Genres, userProfile, engagementExclude);
        var userRatingScore = ContentScoring.ComputeGenreRatingScore(rec.Genres, userProfile, engagementExclude);
        var completionRatio = genreAvgCompletion;
        var hasUserInteraction = familiarity > 0.0;
        var isAbandoned = genreAbandonRate;
        var actualCompletionRatio = ContentScoring.ComputeCompletionRatio(watchedItemForRec);

        var collabScore = ContentScoring.ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax);

        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(rec.CommunityRating, rec.CriticRating);
        var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

        // Compute actual SeriesAffinity (train/serve parity) using the per-user context built once in
        // EmitRecommendationFeedbackExamples. Exclude this candidate's own series: at inference a scored
        // Series is never in the user's progressing set (watched series are filtered out upstream), so
        // excluding it here prevents a training-only self-Jaccard that inference can never produce.
        var seriesAffinity = userCtx.SeriesAffinity is not null
            ? ContentScoring.ComputeSeriesAffinity(isSeries, rec.ItemId, rec.Genres, userCtx.SeriesAffinity, lookups.CachedPeopleLookup, excludeSeriesId: rec.ItemId)
            : 0.0;

        var peopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(rec.ItemId, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights)
            : 0.0;

        var studioMatch = rec.Studios.Count > 0
                          && rec.Studios.Any(preferredStudios.Contains);

        var tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(rec.Tags, preferredTags);
        var recGenreSet = rec.Genres.Count > 0
            ? new HashSet<string>(rec.Genres, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        double recencyScore;
        if (rec.PremiereDate.HasValue)
        {
            recencyScore = ContentScoring.ComputeRecencyScore(rec.PremiereDate.Value);
        }
        else if (rec.DateCreated.HasValue)
        {
            recencyScore = ContentScoring.ComputeRecencyScore(rec.DateCreated.Value);
        }
        else
        {
            recencyScore = 0.5;
        }

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(rec.Genres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = recencyScore,
            YearProximityScore = ContentScoring.ComputeYearProximity(rec.Year, avgYear),
            GenreCount = rec.Genres.Count,
            IsSeries = isSeries,
            UserRatingScore = userRatingScore,
            HasUserInteraction = hasUserInteraction,
            CompletionRatio = completionRatio,
            IsAbandoned = isAbandoned,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesAffinity = seriesAffinity,
            PopularityScore = popularityScore,
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
                watchedItemForRec,
                recGenreSet,
                userProfile,
                isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
                watchedItemForRec,
                recGenreSet,
                userProfile,
                isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, watchedItemForRec?.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = rec.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(rec.DateCreated.Value)
                : 0.5,
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                rec.Genres,
                rec.PeopleNames,
                rec.Studios,
                contentSets.WatchedGenreSets,
                contentSets.WatchedPeopleSets,
                contentSets.WatchedStudioSets),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(rec.AudioLanguages, userProfile),
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(rec.BoxSetIds, contentSets.WatchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(rec.SubtitleLanguages, userProfile),
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(rec.TmdbCollectionName, preferredFranchises),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(rec.ProductionCountries, preferredCountries),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(rec.InheritedTags, preferredInheritedTags),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, rec.SeriesStatus, rec.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(rec.WriterNames, preferredWriterWeights),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(rec.PeopleNames, rec.PeopleWeights), preferredPeopleWeights),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(rec.Genres, rec.Studios, lookups.GenreStudioIdf)
        };

        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(rec.Genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        var label = ComputeRecommendationFeedbackLabel(wasWatched, isSeries, watchedItemForRec, prevResult, actualCompletionRatio);

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = prevResult.GeneratedAt,
            UserId = userProfile.UserId
        };
    }

    /// <summary>
    ///     Computes the label for a Phase 1 example from the actual watch state. Features stay neutral.
    /// </summary>
    private static double ComputeRecommendationFeedbackLabel(
        bool wasWatched,
        bool isSeries,
        WatchedItemInfo? watchedItemForRec,
        RecommendationResult prevResult,
        double actualCompletionRatio)
    {
        if (wasWatched)
        {
            double baseLabel;
            switch (watchedItemForRec)
            {
                case { IsFavorite: true, Played: false, PlaybackPositionTicks: <= 0, PlayCount: <= 0 }:
                case null when isSeries:
                    baseLabel = 0.65;
                    break;
                default:
                    {
                        baseLabel =
                            actualCompletionRatio is > 0 and < EngineConstants.AbandonedCompletionThreshold
                                ? EngineConstants.AbandonedLabel
                                : ContentScoring.ComputeEngagementLabel(actualCompletionRatio);
                        break;
                    }
            }

            return baseLabel > EngineConstants.AbandonedLabel
                    && watchedItemForRec?.LastPlayedDate is not null
                    && (watchedItemForRec.LastPlayedDate.Value - prevResult.GeneratedAt).TotalDays
                    <= EngineConstants.RecommendationInfluenceWindowDays
                    && watchedItemForRec.LastPlayedDate.Value >= prevResult.GeneratedAt
                ? Math.Max(baseLabel, EngineConstants.RecommendationInfluencedLabel)
                : baseLabel;
        }

        return EngineConstants.ExposureLabel;
    }

    /// <summary>
    ///     Builds the per-user set of item IDs recommended to that user across all previous results.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> BuildRecommendedItemIdsByUser(
        IReadOnlyList<RecommendationResult> previousResults)
    {
        // Organic watches add positives the recommendation history would miss.
        var recommendedItemIdsByUser = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var prevResult in previousResults)
        {
            if (!recommendedItemIdsByUser.TryGetValue(prevResult.UserId, out var userRecommendedItemIds))
            {
                userRecommendedItemIds = [];
                recommendedItemIdsByUser[prevResult.UserId] = userRecommendedItemIds;
            }

            foreach (var rec in prevResult.Recommendations)
            {
                userRecommendedItemIds.Add(rec.ItemId);
            }
        }

        return recommendedItemIdsByUser;
    }

    /// <summary>
    ///     Phase 2: emits organic (watched-but-never-recommended) examples into <c>ctx.Examples</c>.
    /// </summary>
    /// <returns>The number of organic examples emitted.</returns>
    private static int EmitOrganicExamples(TrainingContext ctx)
    {
        var organicCount = 0;
        foreach (var userProfile in ctx.AllProfiles)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            var artifacts = ctx.PerUserCache[userProfile.UserId];

            if (!ctx.RecommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Single pass to collect lookups needed for organic examples.
            var (seriesEpisodeLookupOrganic, seriesWithOrgEpisodes, watchedItemIds, watchedSeriesIds) =
                PrescanOrganicWatchedItems(userProfile, recommendedItemIds);

            // Aggregate series so one show does not produce dozens of examples.
            var aggregatedSeriesIds = new HashSet<Guid>();

            // Same BoxSet counting as live scoring.
            var watchedBoxSetCountsOrganic = BuildWatchedBoxSetCounts(
                BuildWatchedIdSet(watchedItemIds, watchedSeriesIds),
                ctx.Lookups.ItemBoxSetIdsLookup);

            var userCtx = new Phase2UserContext
            {
                UserProfile = userProfile,
                RecommendedItemIds = recommendedItemIds,
                SeriesEpisodeLookupOrganic = seriesEpisodeLookupOrganic,
                SeriesWithOrgEpisodes = seriesWithOrgEpisodes,
                AggregatedSeriesIds = aggregatedSeriesIds,
                WatchedBoxSetCountsOrganic = watchedBoxSetCountsOrganic,
                Artifacts = artifacts,
                SeriesAffinity = ctx.SeriesEpisodeCounts is not null
                    ? ContentScoring.BuildSeriesAffinityContext(userProfile, ctx.SeriesEpisodeCounts)
                    : null
            };

            foreach (var w in userProfile.WatchedItems)
            {
                organicCount += ProcessOrganicWatchedItem(w, userCtx, ctx);
            }
        }

        return organicCount;
    }

    /// <summary>
    ///     Single-pass prescan over a user's watched items building the per-series episode lookup, the set of series with organic (never-recommended) episodes, and the watched item/series id sets.
    /// </summary>
    private static (Dictionary<Guid, List<WatchedItemInfo>> SeriesEpisodeLookup, HashSet<Guid> SeriesWithOrgEpisodes,
        HashSet<Guid> WatchedItemIds, HashSet<Guid> WatchedSeriesIds)
        PrescanOrganicWatchedItems(UserWatchProfile userProfile, HashSet<Guid> recommendedItemIds)
    {
        var seriesEpisodeLookupOrganic = new Dictionary<Guid, List<WatchedItemInfo>>();
        var seriesWithOrgEpisodes = new HashSet<Guid>();
        var watchedItemIds = new HashSet<Guid>();
        var watchedSeriesIds = new HashSet<Guid>();

        foreach (var ep in userProfile.WatchedItems)
        {
            watchedItemIds.Add(ep.ItemId);

            if (ep.SeriesId.HasValue)
            {
                watchedSeriesIds.Add(ep.SeriesId.Value);

                if (!seriesEpisodeLookupOrganic.TryGetValue(ep.SeriesId.Value, out var epList))
                {
                    epList = [];
                    seriesEpisodeLookupOrganic[ep.SeriesId.Value] = epList;
                }

                epList.Add(ep);

                if (ep.HasMeaningfulInteraction()
                    && !recommendedItemIds.Contains(ep.ItemId)
                    && !recommendedItemIds.Contains(ep.SeriesId.Value))
                {
                    seriesWithOrgEpisodes.Add(ep.SeriesId.Value);
                }
            }
        }

        return (seriesEpisodeLookupOrganic, seriesWithOrgEpisodes, watchedItemIds, watchedSeriesIds);
    }

    /// <summary>
    ///     Processes one watched item for Phase 2, emitting either an aggregated series example or a standalone example (or nothing).
    /// </summary>
    private static int ProcessOrganicWatchedItem(
        WatchedItemInfo w,
        Phase2UserContext userCtx,
        TrainingContext ctx)
    {
        var userProfile = userCtx.UserProfile;
        var recommendedItemIds = userCtx.RecommendedItemIds;
        var seriesEpisodeLookupOrganic = userCtx.SeriesEpisodeLookupOrganic;
        var seriesWithOrgEpisodes = userCtx.SeriesWithOrgEpisodes;
        var aggregatedSeriesIds = userCtx.AggregatedSeriesIds;
        var artifacts = userCtx.Artifacts;
        var lookups = ctx.Lookups;

        var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureOrganic,
             preferredPeopleWeightsOrganic, preferredStudiosOrganic, preferredTagsOrganic,
             preferredFranchisesOrganic, preferredCountriesOrganic, preferredInheritedTagsOrganic, preferredWriterWeightsOrganic) =
            artifacts;

        // Only organic watches.
        if (!w.HasMeaningfulInteraction() || recommendedItemIds.Contains(w.ItemId))
        {
            return 0;
        }

        if (w.SeriesId.HasValue && recommendedItemIds.Contains(w.SeriesId.Value))
        {
            return 0;
        }

        // Aggregate episodes at series level.
        if (w.SeriesId.HasValue)
        {
            if (!aggregatedSeriesIds.Add(w.SeriesId.Value))
            {
                return 0;
            }

            if (seriesEpisodeLookupOrganic.TryGetValue(w.SeriesId.Value, out var seriesEpisodes))
            {
                TrainingFeatureComputer.AddAggregatedSeriesExample(
                    ctx.Examples,
                    seriesEpisodes,
                    w.SeriesId.Value,
                    userProfile,
                    genrePreferences,
                    coOccurrence,
                    collaborativeMax,
                    avgYear,
                    genreExposureOrganic,
                    lookups.CachedPeopleLookup,
                    preferredPeopleWeightsOrganic,
                    lookups.ItemStudiosLookup,
                    preferredStudiosOrganic,
                    lookups.ItemTagsLookup,
                    preferredTagsOrganic,
                    preferredFranchisesOrganic,
                    preferredCountriesOrganic,
                    preferredInheritedTagsOrganic,
                    preferredWriterWeightsOrganic,
                    lookups.GenreStudioIdf,
                    ctx.OrganicFallbackTimestamp,
                    userCtx.SeriesAffinity);
                return 1;
            }

            return 0;
        }

        var collabScore = ContentScoring.ComputeCollaborativeScore(w.ItemId, coOccurrence, collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(
                w.CommunityRating,
                null);
        // Favorites without playback should not count as fully watched.
        var completionRatio = ContentScoring.ComputeCompletionRatio(w);

        var isSeries = string.Equals(w.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        // Prefer aggregated series example when episodes exist.
        if (isSeries && seriesWithOrgEpisodes.Contains(w.ItemId))
        {
            return 0;
        }

        if (isSeries && aggregatedSeriesIds.Contains(w.ItemId))
        {
            return 0;
        }

        if (isSeries)
        {
            aggregatedSeriesIds.Add(w.ItemId);
        }

        ctx.Examples.Add(
            BuildOrganicStandaloneExample(
                w,
                userCtx,
                ctx,
                collabScore,
                combinedCriticScore,
                completionRatio,
                isSeries));
        return 1;
    }

    /// <summary>
    ///     Builds a single Phase 2 organic standalone (non-series-episode) training example.
    /// </summary>
    private static TrainingExample BuildOrganicStandaloneExample(
        WatchedItemInfo w,
        Phase2UserContext userCtx,
        TrainingContext ctx,
        double collabScore,
        double combinedCriticScore,
        double completionRatio,
        bool isSeries)
    {
        var userProfile = userCtx.UserProfile;
        var watchedBoxSetCountsOrganic = userCtx.WatchedBoxSetCountsOrganic;
        var lookups = ctx.Lookups;
        var organicFallbackTimestamp = ctx.OrganicFallbackTimestamp;

        var (genrePreferences, _, _, avgYear, genreExposureOrganic,
             preferredPeopleWeightsOrganic, preferredStudiosOrganic, preferredTagsOrganic,
             preferredFranchisesOrganic, preferredCountriesOrganic, preferredInheritedTagsOrganic, preferredWriterWeightsOrganic) =
            userCtx.Artifacts;

        var peopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(w.ItemId, out var organicPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(organicPeople, preferredPeopleWeightsOrganic)
            : 0.0;

        var studioMatch = false;
        var tagSimilarity = 0.0;
        lookups.ItemStudiosLookup.TryGetValue(w.ItemId, out var organicStudios);

        if (organicStudios is { Count: > 0 })
        {
            studioMatch = organicStudios.Any(preferredStudiosOrganic.Contains);
        }

        if (lookups.ItemTagsLookup.TryGetValue(w.ItemId, out var organicTags) && organicTags.Count > 0)
        {
            tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
        }

        var wGenres = w.Genres ?? Array.Empty<string>();
        // Exclude the target item from genre engagement to prevent label leakage. For a series the watch
        // records are its episodes (ItemId == episodeId), so excluding only w.ItemId (the series id) would
        // let the series' own episodes leak in. This path is reached for a series only when it has no
        // meaningful organic episodes, but non-meaningful or recommended episodes can still sit in the
        // profile, so exclude every episode id as well when the lookup has any.
        HashSet<Guid> organicExclude;
        if (isSeries && userCtx.SeriesEpisodeLookupOrganic.TryGetValue(w.ItemId, out var wEpisodes))
        {
            organicExclude = BuildEpisodeExcludeSet(w.ItemId, wEpisodes);
        }
        else
        {
            organicExclude = new HashSet<Guid> { w.ItemId };
        }

        var (familiarity2, genreAvgCompletion2, genreAbandonRate2) = ContentScoring.ComputeGenreEngagement(
            wGenres, userProfile, organicExclude);
        var userRatingScore2 = ContentScoring.ComputeGenreRatingScore(wGenres, userProfile, organicExclude);
        var wGenreSet = wGenres.Count > 0
            ? new HashSet<string>(wGenres, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(wGenres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = w.Year is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1, 0, 0, 0, DateTimeKind.Utc))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
            GenreCount = wGenres.Count,
            IsSeries = isSeries,
            UserRatingScore = userRatingScore2,
            HasUserInteraction = familiarity2 > 0.0,
            CompletionRatio = genreAvgCompletion2,
            IsAbandoned = genreAbandonRate2,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesAffinity = userCtx.SeriesAffinity is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, w.ItemId, wGenres, userCtx.SeriesAffinity, lookups.CachedPeopleLookup, excludeSeriesId: w.ItemId)
                : 0.0,
            CollectionProgressionBoost = lookups.ItemBoxSetIdsLookup.TryGetValue(w.ItemId, out var orgBoxSetIds2)
                ? ComputeCollectionProgressionBoostWithCounts(orgBoxSetIds2, watchedBoxSetCountsOrganic)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, w.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = w.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value)
                : 0.5,
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(w.TmdbCollectionName, preferredFranchisesOrganic),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(w.ProductionCountries, preferredCountriesOrganic),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(w.InheritedTags, preferredInheritedTagsOrganic),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, w.SeriesStatus, w.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(w.WriterNames, preferredWriterWeightsOrganic),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(w.PeopleNames, w.PeopleWeights), preferredPeopleWeightsOrganic),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(w.Genres, organicStudios, lookups.GenreStudioIdf)
        };

        var (organicUnderexp, organicDomRatio, organicAffGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(wGenres, genreExposureOrganic);
        features.GenreUnderexposure = organicUnderexp;
        features.GenreDominanceRatio = organicDomRatio;
        features.GenreAffinityGap = organicAffGap;

        // Organic watches are positives. Favorites without playback get a fixed positive label.
        var label = w switch
        {
            { Played: false, PlaybackPositionTicks: > 0 } when completionRatio <
                                                               EngineConstants.AbandonedCompletionThreshold =>
                EngineConstants.AbandonedLabel,
            { Played: false, PlaybackPositionTicks: <= 0, IsFavorite: true } => 0.65,
            _ => ContentScoring.ComputeEngagementLabel(completionRatio)
        };

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = w.LastPlayedDate ?? organicFallbackTimestamp,
            SampleWeight = 0.7,
            UserId = userProfile.UserId
        };
    }

    /// <summary>
    ///     Phase 3: emits cross-user random-negative examples into <c>ctx.Examples</c>.
    /// </summary>
    /// <returns>The number of random-negative examples emitted.</returns>
    private static int EmitRandomNegativeExamples(TrainingContext ctx)
    {
        // Phase 1 negatives are only items the system recommended to THIS user (exposure bias); Phase 2 adds only positives.
        var randomNegativeCount = 0;
        // Deduplicate by ItemId to prevent popular titles (recommended to multiple users) from appearing multiple times in candidateNegatives, which would overweight them as negatives purely because they were widely recommended elsewhere.
        var seenNegItemIds = new HashSet<Guid>();
        var allRecommendedItems = new List<RecommendedItem>();
        foreach (var prevResult in ctx.PreviousResults)
        {
            allRecommendedItems.AddRange(prevResult.Recommendations.Where(rec => seenNegItemIds.Add(rec.ItemId)));
        }

        if (allRecommendedItems.Count > 0)
        {
            foreach (var userProfile in ctx.AllProfiles)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();

                randomNegativeCount += ProcessUserRandomNegatives(userProfile, allRecommendedItems, ctx);
            }
        }

        return randomNegativeCount;
    }

    /// <summary>
    ///     Emits the Phase 3 cross-user random-negative examples for a single user. Returns the number of examples emitted.
    /// </summary>
    private static int ProcessUserRandomNegatives(
        UserWatchProfile userProfile,
        List<RecommendedItem> allRecommendedItems,
        TrainingContext ctx)
    {
        var lookups = ctx.Lookups;

        // Per-user deterministic RNG: same cache + same user => same negatives.
        // Keeps training reproducible without coupling across users.
        var rngNeg = new Random(Engine.ComputeStableSeed(userProfile.UserId, ctx.PreviousResults.Count));

        if (!ctx.ProfileLookup.TryGetValue(userProfile.UserId, out var userWatchedIds))
        {
            return 0;
        }

        ctx.SeriesLookup.TryGetValue(userProfile.UserId, out var userWatchedSeriesIds);

        if (!ctx.RecommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var userRecommendedIds))
        {
            userRecommendedIds = new HashSet<Guid>();
        }

        var artifacts = ctx.PerUserCache[userProfile.UserId];

        // Per-user series-affinity context (built once). Random negatives are cross-user items this
        // user never saw, so they are genuine unseen candidates: no self-exclusion, matching inference.
        var negSeriesAffinityCtx = ctx.SeriesEpisodeCounts is not null
            ? ContentScoring.BuildSeriesAffinityContext(userProfile, ctx.SeriesEpisodeCounts)
            : null;

        var (watchedGenreSetsNeg, watchedPeopleSetsNeg, watchedStudioSetsNeg) =
            BuildWatchedContentSets(userProfile, lookups);
        var watchedBoxSetCountsNeg = BuildWatchedBoxSetCounts(
            BuildWatchedIdSet(userWatchedIds, userWatchedSeriesIds),
            lookups.ItemBoxSetIdsLookup);

        var contentSets = new WatchedContentSets(
            watchedGenreSetsNeg,
            watchedPeopleSetsNeg,
            watchedStudioSetsNeg,
            watchedBoxSetCountsNeg);

        // Candidates are items recommended to others that this user never saw.
        var candidateNegatives = new List<RecommendedItem>();
        foreach (var rec in allRecommendedItems)
        {
            if (userWatchedIds.Contains(rec.ItemId)
                || userRecommendedIds.Contains(rec.ItemId)
                || (userWatchedSeriesIds?.Contains(rec.ItemId) ?? false))
            {
                continue;
            }

            candidateNegatives.Add(rec);
        }

        var randomNegativeCount = 0;
        var sampleCount = Math.Min(EngineConstants.RandomNegativeSamplesPerUser, candidateNegatives.Count);
        for (var s = 0; s < sampleCount; s++)
        {
            // Pick without replacement.
            var swapIdx = rngNeg.Next(s, candidateNegatives.Count);
            (candidateNegatives[s], candidateNegatives[swapIdx]) =
                (candidateNegatives[swapIdx], candidateNegatives[s]);

            var neg = candidateNegatives[s];
            ctx.Examples.Add(
                BuildRandomNegativeExample(
                    neg,
                    userProfile,
                    artifacts,
                    contentSets,
                    lookups,
                    ctx.OrganicFallbackTimestamp,
                    negSeriesAffinityCtx));
            randomNegativeCount++;
        }

        return randomNegativeCount;
    }

    /// <summary>
    ///     Builds a single Phase 3 cross-user random-negative training example.
    /// </summary>
    private static TrainingExample BuildRandomNegativeExample(
        RecommendedItem neg,
        UserWatchProfile userProfile,
        PerUserArtifacts artifacts,
        WatchedContentSets contentSets,
        TrainingLookups lookups,
        DateTime organicFallbackTimestamp,
        ContentScoring.SeriesAffinityContext? seriesAffinityCtx)
    {
        var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureNeg,
             preferredPeopleWeightsNeg, preferredStudiosNeg, preferredTagsNeg,
             preferredFranchisesNeg, preferredCountriesNeg, preferredInheritedTagsNeg, preferredWriterWeightsNeg) =
            artifacts;

        var collabScore = ContentScoring.ComputeCollaborativeScore(
            neg.ItemId,
            coOccurrence,
            collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(neg.CommunityRating, neg.CriticRating);
        var isSeries = string.Equals(neg.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        var negPeopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(negPeople, preferredPeopleWeightsNeg)
            : 0.0;

        var negGenres = neg.Genres ?? Array.Empty<string>();
        var negStudios = neg.Studios ?? Array.Empty<string>();
        var negTags = neg.Tags ?? Array.Empty<string>();
        var negStudioMatch = negStudios.Count > 0
                             && negStudios.Any(preferredStudiosNeg.Contains);
        var negTagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(negTags, preferredTagsNeg);

        double negRecencyScore;
        if (neg.PremiereDate.HasValue)
        {
            negRecencyScore = ContentScoring.ComputeRecencyScore(neg.PremiereDate.Value);
        }
        else if (neg.DateCreated.HasValue)
        {
            negRecencyScore = ContentScoring.ComputeRecencyScore(neg.DateCreated.Value);
        }
        else
        {
            negRecencyScore = 0.5;
        }

        var (familiarityNeg, genreAvgCompletionNeg, genreAbandonRateNeg) = ContentScoring.ComputeGenreEngagement(negGenres, userProfile);
        var userRatingScoreNeg = ContentScoring.ComputeGenreRatingScore(negGenres, userProfile);
        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(negGenres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = negRecencyScore,
            YearProximityScore = ContentScoring.ComputeYearProximity(neg.Year, avgYear),
            GenreCount = negGenres.Count,
            IsSeries = isSeries,
            UserRatingScore = userRatingScoreNeg,
            HasUserInteraction = familiarityNeg > 0.0,
            CompletionRatio = genreAvgCompletionNeg,
            IsAbandoned = genreAbandonRateNeg,
            PeopleSimilarity = negPeopleSimilarity,
            StudioMatch = negStudioMatch,
            SeriesAffinity = seriesAffinityCtx is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, neg.ItemId, negGenres, seriesAffinityCtx, lookups.CachedPeopleLookup)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile),
            TagSimilarity = negTagSimilarity,
            LibraryAddedRecency = neg.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(neg.DateCreated.Value)
                : 0.5,
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                negGenres,
                neg.PeopleNames,
                negStudios,
                contentSets.WatchedGenreSets,
                contentSets.WatchedPeopleSets,
                contentSets.WatchedStudioSets),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(neg.AudioLanguages, userProfile),
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(neg.BoxSetIds, contentSets.WatchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(neg.SubtitleLanguages, userProfile),
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(neg.TmdbCollectionName, preferredFranchisesNeg),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(neg.ProductionCountries, preferredCountriesNeg),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(neg.InheritedTags, preferredInheritedTagsNeg),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, neg.SeriesStatus, neg.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(neg.WriterNames, preferredWriterWeightsNeg),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(neg.PeopleNames, neg.PeopleWeights), preferredPeopleWeightsNeg),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(neg.Genres, neg.Studios, lookups.GenreStudioIdf)
        };

        var (negUnderexp, negDomRatio, negAffGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(negGenres, genreExposureNeg);
        features.GenreUnderexposure = negUnderexp;
        features.GenreDominanceRatio = negDomRatio;
        features.GenreAffinityGap = negAffGap;

        return new TrainingExample
        {
            Features = features,
            Label = 0.0,
            GeneratedAtUtc = organicFallbackTimestamp,
            SampleWeight = 0.5,
            UserId = userProfile.UserId
        };
    }

    /// <summary>
    ///     Builds the union of watched item and series ids for BoxSet counting.
    /// </summary>
    private static HashSet<Guid> BuildWatchedIdSet(HashSet<Guid> watchedIds, HashSet<Guid>? watchedSeriesIds)
    {
        var set = new HashSet<Guid>(watchedIds);
        if (watchedSeriesIds is not null)
        {
            set.UnionWith(watchedSeriesIds);
        }

        return set;
    }

    /// <summary>
    ///     Computes CollectionProgressionBoost from prebuilt BoxSet counts.
    /// </summary>
    /// <param name="boxSetIds">The cached BoxSet ids for the candidate.</param>
    /// <param name="watchedBoxSetCounts">Prebuilt BoxSet id to watched count map.</param>
    /// <returns>A boost between 0.0 and 1.0.</returns>
    internal static double ComputeCollectionProgressionBoostWithCounts(
        IReadOnlyList<Guid>? boxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        if (boxSetIds is null || boxSetIds.Count == 0 || watchedBoxSetCounts.Count == 0)
        {
            return 0.0;
        }

        // Best boost across all BoxSets for this candidate.
        var bestBoost = 0.0;
        foreach (var boxSetId in boxSetIds)
        {
            if (!watchedBoxSetCounts.TryGetValue(boxSetId, out var watchedCount))
            {
                continue;
            }

            var boost = EngineConstants.ComputeCollectionProgressionBoost(watchedCount);
            if (boost > bestBoost)
            {
                bestBoost = boost;
            }
        }

        return bestBoost;
    }

    /// <summary>
    ///     Groups item metadata lookups and the genre and studio rarity table.
    /// </summary>
    private readonly record struct TrainingLookups(
        Dictionary<Guid, HashSet<string>> CachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> ItemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> ItemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> ItemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? GenreStudioIdf);

    /// <summary>
    ///     Groups per user watched content sets and BoxSet counts.
    /// </summary>
    private readonly record struct WatchedContentSets(
        List<HashSet<string>> WatchedGenreSets,
        List<HashSet<string>> WatchedPeopleSets,
        List<HashSet<string>> WatchedStudioSets,
        Dictionary<Guid, int> WatchedBoxSetCounts);

    /// <summary>
    ///     Shared context passed through all phases.
    /// </summary>
    private readonly record struct TrainingContext
    {
        /// <summary>Gets the recommendation results from previous runs.</summary>
        public IReadOnlyList<RecommendationResult> PreviousResults { get; init; }

        /// <summary>Gets all user watch profiles.</summary>
        public Collection<UserWatchProfile> AllProfiles { get; init; }

        /// <summary>Gets the per-user set of meaningfully-interacted item IDs.</summary>
        public Dictionary<Guid, HashSet<Guid>> ProfileLookup { get; init; }

        /// <summary>Gets the per-user set of meaningfully-interacted (and favorited) series IDs.</summary>
        public Dictionary<Guid, HashSet<Guid>> SeriesLookup { get; init; }

        /// <summary>Gets the O(1) profile-by-user-id lookup.</summary>
        public Dictionary<Guid, UserWatchProfile> ProfileById { get; init; }

        /// <summary>Gets the per-user set of item IDs recommended to that user.</summary>
        public Dictionary<Guid, HashSet<Guid>> RecommendedItemIdsByUser { get; init; }

        /// <summary>Gets the per-user pre-computed artifacts cache.</summary>
        public Dictionary<Guid, PerUserArtifacts> PerUserCache { get; init; }

        /// <summary>Gets the shared item-metadata lookups and genre/studio IDF table.</summary>
        public TrainingLookups Lookups { get; init; }

        /// <summary>Gets the deterministic timestamp anchor for organic items without a play date.</summary>
        public DateTime OrganicFallbackTimestamp { get; init; }

        /// <summary>Gets the mutable output list that all phases append training examples to.</summary>
        public List<TrainingExample> Examples { get; init; }

        /// <summary>Gets the cancellation token for the build operation.</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>Gets the per-series total episode count map (library-wide). Used in training to compute SeriesAffinity on the same basis as inference.</summary>
        public IReadOnlyDictionary<Guid, int>? SeriesEpisodeCounts { get; init; }
    }

    /// <summary>
    ///     Per-user context for Phase 1 example building. Behaviour-neutral parameter aggregate.
    /// </summary>
    private readonly record struct Phase1UserContext
    {
        /// <summary>Gets the user's watch profile.</summary>
        public UserWatchProfile UserProfile { get; init; }

        /// <summary>Gets the user's meaningfully-interacted item IDs.</summary>
        public HashSet<Guid> WatchedIds { get; init; }

        /// <summary>Gets the user's meaningfully-interacted series IDs, if any.</summary>
        public HashSet<Guid>? WatchedSeriesIds { get; init; }

        /// <summary>Gets the per-item watched-item lookup for this user.</summary>
        public Dictionary<Guid, WatchedItemInfo> WatchedItemLookup { get; init; }

        /// <summary>Gets the per-series episode lookup for this user.</summary>
        public Dictionary<Guid, List<WatchedItemInfo>> SeriesEpisodeLookup { get; init; }

        /// <summary>Gets the user's pre-computed preference artifacts.</summary>
        public PerUserArtifacts Artifacts { get; init; }

        /// <summary>Gets the user's parallel watched-content sets and BoxSet counts.</summary>
        public WatchedContentSets ContentSets { get; init; }

        /// <summary>Gets the pre-built series-affinity context for this user, or null when episode counts are unavailable. Built once per user so SeriesAffinity is computed on the same basis as inference without rebuilding the watched-series lookup per example.</summary>
        public ContentScoring.SeriesAffinityContext? SeriesAffinity { get; init; }
    }

    /// <summary>
    ///     Per-user context for Phase 2 (organic) example building. Behaviour-neutral parameter aggregate.
    /// </summary>
    private readonly record struct Phase2UserContext
    {
        /// <summary>Gets the user's watch profile.</summary>
        public UserWatchProfile UserProfile { get; init; }

        /// <summary>Gets the set of item IDs already recommended to this user.</summary>
        public HashSet<Guid> RecommendedItemIds { get; init; }

        /// <summary>Gets the per-series organic episode lookup for this user.</summary>
        public Dictionary<Guid, List<WatchedItemInfo>> SeriesEpisodeLookupOrganic { get; init; }

        /// <summary>Gets the set of series with organic (never-recommended) episodes.</summary>
        public HashSet<Guid> SeriesWithOrgEpisodes { get; init; }

        /// <summary>Gets the mutable set tracking series already emitted as aggregated examples.</summary>
        public HashSet<Guid> AggregatedSeriesIds { get; init; }

        /// <summary>Gets the user's watched-BoxSet count map for organic items.</summary>
        public Dictionary<Guid, int> WatchedBoxSetCountsOrganic { get; init; }

        /// <summary>Gets the user's pre-computed preference artifacts.</summary>
        public PerUserArtifacts Artifacts { get; init; }

        /// <summary>Gets the pre-built series-affinity context for this user, or null when episode counts are unavailable. Built once per user so SeriesAffinity is computed on the same basis as inference without rebuilding the watched-series lookup per example.</summary>
        public ContentScoring.SeriesAffinityContext? SeriesAffinity { get; init; }
    }
}
