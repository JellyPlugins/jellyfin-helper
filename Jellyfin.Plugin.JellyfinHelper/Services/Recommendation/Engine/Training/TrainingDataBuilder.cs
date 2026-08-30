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
    ///     Per-series total-episode-count map (SeriesId -> playable episodes in the library). When
    ///     supplied, the genre/people preference vectors built here apply the SAME progression
    ///     multiplier the inference path applies (see <see cref="PreferenceBuilder"/>), so the model
    ///     trains on the feature distribution it is actually served - eliminating train/serve skew.
    ///     When null/empty, every episode row keeps neutral weight (1.0), matching the pre-fix and
    ///     no-library-data behavior.
    /// </param>
    /// <param name="genreStudioIdf">
    ///     Library-wide genre/studio IDF rarity table - the SAME table the inference path uses - so the
    ///     GenreStudioIdfPrior feature is identical between train and serve. Null -> neutral 0.0 both sides.
    /// </param>
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

        // Stable timestamp anchor for organic items without LastPlayedDate. Using the earliest recommendation GeneratedAt provides a deterministic value that doesn't drift across runs (unlike DateTime.UtcNow.AddDays(-90)).
        var organicFallbackTimestamp = previousResults.Count > 0
            ? previousResults.Min(r => r.GeneratedAt)
            : DateTime.UtcNow.AddDays(-90);

        // Bundle the immutable, phase-spanning context into a single value so each phase's entry point takes one parameter instead of ten-plus loose arguments.
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
            CancellationToken = cancellationToken
        };

        EmitRecommendationFeedbackExamples(ctx);

        var organicCount = EmitOrganicExamples(ctx);

        var randomNegativeCount = EmitRandomNegativeExamples(ctx);

        // Discovery items are external (not in library); their interactions give explicit signals - requests are strong positives, dismissals negatives.
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
        // Include both played AND favorited items as positive interactions. A favorited-but-not-played recommended item signals explicit interest and should not be labeled as exposure/abandonment.
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

            // Also include series-level favorites (user favorited the series itself, not individual episodes)
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
        // Pre-compute itemId ? studios / tags / BoxSet lookups ONCE from all previous results.
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
        // Pre-compute per-user artifacts once and cache them. These are reused across Phase 1 (recommendation feedback), Phase 2 (organic examples), and Phase 3 (random negatives), avoiding redundant scans of the watched-items list for the same user.
        perUserCache = new Dictionary<Guid, PerUserArtifacts>();

        // Build a lookup for O(1) profile access by user ID (avoids O(N) FirstOrDefault per result)
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
            // The new content-affinity preference maps read directly off WatchedItemInfo's cached fields, so the SAME PreferenceBuilder functions used at live scoring time apply here - identical inputs -> identical maps -> train/serve parity by construction.
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

            // Build series episode lookup for series-level aggregation
            var seriesEpisodeLookup = BuildSeriesEpisodeLookup(userProfile);

            // Build watched genre/people/studio sets for ContentNearestNeighborScore. Mirrors Engine.GenerateForUser (parallel lists indexed by watched item).
            var (watchedGenreSets, watchedPeopleSets, watchedStudioSets) =
                BuildWatchedContentSets(userProfile, ctx.Lookups);

            // Build per-user watchedBoxSetCounts by iterating the user's watched items directly (matches Engine.BuildWatchedBoxSetCounts).
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
                ContentSets = contentSets
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

        // Use most recent playback episode for temporal signals when the candidate is a series
        if (isSeries && seriesEpisodeLookup.TryGetValue(rec.ItemId, out var episodesForSeries))
        {
            watchedItemForRec = episodesForSeries
                .Where(e => e.HasPlaybackActivity())
                .OrderByDescending(e => e.LastPlayedDate)
                .FirstOrDefault();
        }

        // Interaction features stay neutral so the model cannot memorize per-item engagement
        const double userRatingScore = 0.5;
        const double completionRatio = 0.0;
        const bool hasUserInteraction = false;
        var actualCompletionRatio = ContentScoring.ComputeCompletionRatio(watchedItemForRec);

        // Compute collaborative score for this specific item
        var collabScore = ContentScoring.ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax);

        // Popularity proxy matching Engine.ScoreCandidate() logic
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(rec.CommunityRating, rec.CriticRating);
        var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

        // Series progression boost: hardcoded 0.0 to mirror inference.
        const double seriesProgressionBoost = 0.0;

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var peopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(rec.ItemId, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights)
            : 0.0;

        // Compute StudioMatch from cached data (matches Engine.ScoreCandidate() logic)
        var studioMatch = rec.Studios.Count > 0
                          && rec.Studios.Any(preferredStudios.Contains);

        // Compute TagSimilarity from cached data (matches Engine.ScoreCandidate() logic)
        var tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(rec.Tags, preferredTags);

        // Build genre set once; shared by GenreSimilarity, temporal affinity (day + hour), and genre exposure.
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
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
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
            // Shared IsWeekend resolver: user's LastActivityDate wins, falls back to the
            // per-item LastPlayedDate when the profile carries no anchor yet.
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
            // Content-affinity signals - SAME shared helpers as live scoring, over the cached RecommendedItem fields.
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
    ///     Computes the label for a Phase 1 recommendation-feedback example using the actual watch state without leaking it into the feature vector.
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
        // Items the user found and watched on their own are strong positive signal the recommendation-only approach misses, reducing training bias.
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

            // Resolve the per-user recommended set; users with no previous results get an empty set
            if (!ctx.RecommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Build series episode lookup, seriesWithOrgEpisodes set, and watched-id sets
            // in a single pass over WatchedItems to avoid four separate iterations.
            var (seriesEpisodeLookupOrganic, seriesWithOrgEpisodes, watchedItemIds, watchedSeriesIds) =
                PrescanOrganicWatchedItems(userProfile, recommendedItemIds);

            // Without aggregation, a series with 50 episodes produces 50 training examples, massively skewing the dataset toward that series.
            var aggregatedSeriesIds = new HashSet<Guid>();

            // Build per-user BoxSet counts for Phase 2 organic items so that standalone organic movies/series receive a real CollectionProgressionBoost feature rather than the hardcoded 0.0 that would create a train/serve skew (the live scoring path always computes the real boost via.
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
                Artifacts = artifacts
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

        // Include played OR favorited items that were NEVER recommended (organic discoveries).
        if (!w.HasMeaningfulInteraction() || recommendedItemIds.Contains(w.ItemId))
        {
            return 0;
        }

        // Skip series IDs already covered by Phase 1 recommendations
        if (w.SeriesId.HasValue && recommendedItemIds.Contains(w.SeriesId.Value))
        {
            return 0;
        }

        // For episodes belonging to a series, aggregate at the series level.
        // Skip if this series was already aggregated from an earlier episode row.
        if (w.SeriesId.HasValue)
        {
            if (!aggregatedSeriesIds.Add(w.SeriesId.Value))
            {
                return 0; // Already emitted an aggregated example for this series
            }

            // Retrieve all episodes for this series from the pre-built lookup
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
                    ctx.OrganicFallbackTimestamp);
                return 1;
            }

            return 0;
        }

        // Note: w.SeriesId is guaranteed null here because the if (w.SeriesId.HasValue)
        // block above always exits with `continue`. Only non-series items reach this point.
        var collabScore = ContentScoring.ComputeCollaborativeScore(w.ItemId, coOccurrence, collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(
                w.CommunityRating,
                null); // CriticRating not available on WatchedItemInfo
        // Gate completion fallback on w.Played to avoid mis-labeling favorite-only items
        // as fully watched. Favorites without playback evidence get 0.0 completion.
        var completionRatio = ContentScoring.ComputeCompletionRatio(w);

        var isSeries = string.Equals(w.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        // If this standalone series has episode rows in the organic set, skip it - the episode-based aggregation path (above) produces richer training signals.
        if (isSeries && seriesWithOrgEpisodes.Contains(w.ItemId))
        {
            return 0;
        }

        // If this standalone item is a Series object (w.SeriesId == null, w.ItemType == "Series") and the series was already emitted via the aggregation path above (episode rows with matching SeriesId), skip to avoid double-counting the same series with two training examples.
        if (isSeries && aggregatedSeriesIds.Contains(w.ItemId))
        {
            return 0;
        }

        // Mark this standalone series as aggregated so that if episode rows for the same
        // series appear later, the aggregation path won't emit a duplicate example.
        if (isSeries)
        {
            aggregatedSeriesIds.Add(w.ItemId);
        }

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
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

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var peopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(w.ItemId, out var organicPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(organicPeople, preferredPeopleWeightsOrganic)
            : 0.0;

        // Compute StudioMatch and TagSimilarity from precomputed lookups (by item ID only).
        var studioMatch = false;
        var tagSimilarity = 0.0;

        // Resolve the item's studios once: used for BOTH StudioMatch and the genre/studio IDF prior below.
        lookups.ItemStudiosLookup.TryGetValue(w.ItemId, out var organicStudios);

        if (organicStudios is { Count: > 0 })
        {
            studioMatch = organicStudios.Any(preferredStudiosOrganic.Contains);
        }

        if (lookups.ItemTagsLookup.TryGetValue(w.ItemId, out var organicTags) && organicTags.Count > 0)
        {
            tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
        }

        // Series progression boost: hardcoded 0.0.
        const double seriesProgressionBoost = 0.0;

        // Null-safe genre access for deserialized cache objects
        var wGenres = w.Genres ?? Array.Empty<string>();

        // Build genre set once; shared by temporal affinity (day + hour) calls below.
        var wGenreSet = wGenres.Count > 0
            ? new HashSet<string>(wGenres, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(wGenres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            // Use content release year for recency (not watch date) to match Phase 1 semantics. Phase 1 uses rec.PremiereDate; organic items lack premiere metadata so approximate via ProductionYear, falling back to neutral 0.5.
            RecencyScore = w.Year is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1, 0, 0, 0, DateTimeKind.Utc))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
            GenreCount = wGenres.Count,
            IsSeries = isSeries,
            // At inference this organic item is an unwatched candidate, so interaction signals must be neutral (otherwise the model learns a completion signal that never appears at serve time).
            UserRatingScore = 0.5,
            HasUserInteraction = false,
            CompletionRatio = 0.0,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
            CollectionProgressionBoost = lookups.ItemBoxSetIdsLookup.TryGetValue(w.ItemId, out var orgBoxSetIds2)
                ? ComputeCollectionProgressionBoostWithCounts(orgBoxSetIds2, watchedBoxSetCountsOrganic)
                : 0.0,
            // Popularity prior: identical composition to Phase 1, Phase 3 and the live scoring path (collaborative + critic blend).
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, w.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = w.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value)
                : 0.5,
            // Organic standalone items lack per-item stream metadata (AudioLanguages/SubtitleLanguages are only cached on RecommendedItem from Phase 1).
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            // Content-affinity signals from WatchedItemInfo's cached fields, using the SAME shared helpers as live scoring.
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(w.TmdbCollectionName, preferredFranchisesOrganic),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(w.ProductionCountries, preferredCountriesOrganic),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(w.InheritedTags, preferredInheritedTagsOrganic),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, w.SeriesStatus, w.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(w.WriterNames, preferredWriterWeightsOrganic),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(w.PeopleNames, w.PeopleWeights), preferredPeopleWeightsOrganic),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(w.Genres, organicStudios, lookups.GenreStudioIdf)
        };

        // Genre exposure features: compute from cached per-user analysis (mirrors Phase 1)
        var (organicUnderexp, organicDomRatio, organicAffGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(wGenres, genreExposureOrganic);
        features.GenreUnderexposure = organicUnderexp;
        features.GenreDominanceRatio = organicDomRatio;
        features.GenreAffinityGap = organicAffGap;

        // Organic watches are strong positive signals - label based on completion. Favorite-only items (not played, no playback progress) get an explicit positive label.
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

        // Build watched genre/people/studio sets for ContentNearestNeighborScore (mirrors Phase 1).
        // Use HasMeaningfulInteraction() for train/serve parity - see Phase 1 comment above.
        var (watchedGenreSetsNeg, watchedPeopleSetsNeg, watchedStudioSetsNeg) =
            BuildWatchedContentSets(userProfile, lookups);

        // Build a per-user watchedBoxSetCounts lookup by iterating this user's watched items directly and resolving BoxSet membership through the global itemBoxSetIdsLookup.
        var watchedBoxSetCountsNeg = BuildWatchedBoxSetCounts(
            BuildWatchedIdSet(userWatchedIds, userWatchedSeriesIds),
            lookups.ItemBoxSetIdsLookup);

        var contentSets = new WatchedContentSets(
            watchedGenreSetsNeg,
            watchedPeopleSetsNeg,
            watchedStudioSetsNeg,
            watchedBoxSetCountsNeg);

        // Collect candidate negatives: items recommended to others but not interacted with by this user
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

        // Sample up to RandomNegativeSamplesPerUser from the candidates
        var randomNegativeCount = 0;
        var sampleCount = Math.Min(EngineConstants.RandomNegativeSamplesPerUser, candidateNegatives.Count);
        for (var s = 0; s < sampleCount; s++)
        {
            // Fisher-Yates partial shuffle to pick without replacement
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
                    ctx.OrganicFallbackTimestamp));
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
        DateTime organicFallbackTimestamp)
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

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var negPeopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(negPeople, preferredPeopleWeightsNeg)
            : 0.0;

        // Null-safe access for deserialized cache objects
        var negGenres = neg.Genres ?? Array.Empty<string>();
        var negStudios = neg.Studios ?? Array.Empty<string>();
        var negTags = neg.Tags ?? Array.Empty<string>();

        // Compute StudioMatch and TagSimilarity from cached data (mirrors Phase 1/2).
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

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(negGenres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = negRecencyScore,
            YearProximityScore = ContentScoring.ComputeYearProximity(neg.Year, avgYear),
            GenreCount = negGenres.Count,
            IsSeries = isSeries,
            UserRatingScore = 0.5,
            HasUserInteraction = false,
            CompletionRatio = 0.0,
            PeopleSimilarity = negPeopleSimilarity,
            StudioMatch = negStudioMatch,
            // SeriesProgressionBoost stays 0.0 - for cross-user negatives, the user
            // has no episode history for that series, so 0 is the correct value.
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            // Shared IsWeekend resolver: cross-user random negatives have no per-item
            // interaction, so we anchor purely on the user's LastActivityDate. See FIX-1.
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
            // Same diminishing-returns formula as inference (Engine.ComputeCollectionProgressionBoostLive), via the per-user watchedBoxSetCountsNeg built above.
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(neg.BoxSetIds, contentSets.WatchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(neg.SubtitleLanguages, userProfile),
            // Content-affinity signals - same shared helpers, over the cached RecommendedItem negative sample. neg carries the full field set (incl.
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(neg.TmdbCollectionName, preferredFranchisesNeg),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(neg.ProductionCountries, preferredCountriesNeg),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(neg.InheritedTags, preferredInheritedTagsNeg),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, neg.SeriesStatus, neg.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(neg.WriterNames, preferredWriterWeightsNeg),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(neg.PeopleNames, neg.PeopleWeights), preferredPeopleWeightsNeg),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(neg.Genres, neg.Studios, lookups.GenreStudioIdf)
        };

        // Genre exposure features
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
    ///     Builds the union of watched item IDs and watched series IDs for BoxSet-count computation.
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
    ///     Computes CollectionProgressionBoost using the same diminishing-returns formula as ComputeCollectionProgressionBoostLive, via a pre-built watchedBoxSetCounts dictionary (built once per user by iterating the user's watched items through the global BoxSet.
    /// </summary>
    /// <param name="boxSetIds">The cached BoxSet IDs for the candidate item.</param>
    /// <param name="watchedBoxSetCounts">Pre-computed BoxSet ID -> watched member count mapping.</param>
    /// <returns>A collection progression boost between 0.0 and 1.0, matching the inference formula.</returns>
    internal static double ComputeCollectionProgressionBoostWithCounts(
        IReadOnlyList<Guid>? boxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        if (boxSetIds is null || boxSetIds.Count == 0 || watchedBoxSetCounts.Count == 0)
        {
            return 0.0;
        }

        // Find the best progression signal across all BoxSets the candidate belongs to.
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
    ///     Groups the read-only, always-passed-together item-metadata lookups and the library-wide genre/studio IDF table so they travel through the phase builders as a single value rather than five loose parameters.
    /// </summary>
    private readonly record struct TrainingLookups(
        Dictionary<Guid, HashSet<string>> CachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> ItemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> ItemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> ItemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? GenreStudioIdf);

    /// <summary>
    ///     Groups the per-user parallel watched-content sets (genre/people/studio, indexed by watched item) and the per-user watched-BoxSet count map that feed the Phase 1/Phase 3 example builders.
    /// </summary>
    private readonly record struct WatchedContentSets(
        List<HashSet<string>> WatchedGenreSets,
        List<HashSet<string>> WatchedPeopleSets,
        List<HashSet<string>> WatchedStudioSets,
        Dictionary<Guid, int> WatchedBoxSetCounts);

    /// <summary>
    ///     Immutable, phase-spanning context threaded through the Phase 1/2/3 entry points so each takes a single argument instead of a long loose parameter list.
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
    }
}
