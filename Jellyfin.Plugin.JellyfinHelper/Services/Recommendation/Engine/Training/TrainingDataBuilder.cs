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

        var examples = new List<TrainingExample>();

        BuildPerUserCache(
            allProfiles,
            precomputedUserSets,
            cachedPeopleLookup,
            itemStudiosLookup,
            itemTagsLookup,
            seriesEpisodeCounts,
            out var perUserCache,
            out var profileById);

        EmitRecommendationFeedbackExamples(
            previousResults,
            profileLookup,
            seriesLookup,
            profileById,
            perUserCache,
            cachedPeopleLookup,
            itemStudiosLookup,
            itemBoxSetIdsLookup,
            genreStudioIdf,
            examples,
            cancellationToken);

        var recommendedItemIdsByUser = BuildRecommendedItemIdsByUser(previousResults);

        // Stable timestamp anchor for organic items without LastPlayedDate.
        // Using the earliest recommendation GeneratedAt provides a deterministic value
        // that doesn't drift across runs (unlike DateTime.UtcNow.AddDays(-90)).
        // Guard: if previousResults is empty (no prior recommendation runs), use a
        // conservative fallback 90 days ago. This path is defensive only - BuildExamples()
        // callers always pass non-empty results from the recommendation store.
        var organicFallbackTimestamp = previousResults.Count > 0
            ? previousResults.Min(r => r.GeneratedAt)
            : DateTime.UtcNow.AddDays(-90);

        var organicCount = EmitOrganicExamples(
            allProfiles,
            perUserCache,
            recommendedItemIdsByUser,
            cachedPeopleLookup,
            itemStudiosLookup,
            itemTagsLookup,
            itemBoxSetIdsLookup,
            genreStudioIdf,
            organicFallbackTimestamp,
            examples,
            cancellationToken);

        var randomNegativeCount = EmitRandomNegativeExamples(
            previousResults,
            allProfiles,
            profileLookup,
            seriesLookup,
            recommendedItemIdsByUser,
            perUserCache,
            cachedPeopleLookup,
            itemStudiosLookup,
            itemBoxSetIdsLookup,
            genreStudioIdf,
            organicFallbackTimestamp,
            examples,
            cancellationToken);

        // === Phase 4: Discovery feedback examples (shown/dismissed/requested/watched) ===
        // Discovery items are external (not in library); their interactions give explicit signals -
        // requests are strong positives, dismissals negatives. Added only when feedback is available.
        // Kept as a separate counter (not folded into organicCount) so operators can see how much
        // positive signal comes from external Seerr requests vs. actual watched consumption - mixing
        // them once made a "205 organic" log look healthy when only 5 items were truly watched.
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
        // Include both played AND favorited items as positive interactions.
        // A favorited-but-not-played recommended item signals explicit interest
        // and should not be labeled as exposure/abandonment.
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
    ///     This allows computing PeopleSimilarity during training without re-querying the library.
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
        // This avoids O(users × results × recommendations) rescanning in BuildStudioPreferenceSetFromCache
        // and BuildTagPreferenceSetFromCache - each user's preference set is now O(watchedItems) instead.
        // The BoxSet lookup mirrors the shape of Engine's live candidateBoxSetLookup so training and
        // inference can iterate watched items (organic or recommended) through the same helper.
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
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        out Dictionary<Guid, PerUserArtifacts> perUserCache,
        out Dictionary<Guid, UserWatchProfile> profileById)
    {
        // Pre-compute per-user artifacts once and cache them. These are reused across
        // Phase 1 (recommendation feedback), Phase 2 (organic examples), and Phase 3 (random
        // negatives), avoiding redundant scans of the watched-items list for the same user.
        perUserCache = new Dictionary<Guid, PerUserArtifacts>();

        // Build a lookup for O(1) profile access by user ID (avoids O(N) FirstOrDefault per result)
        profileById = new Dictionary<Guid, UserWatchProfile>(allProfiles.Count);

        foreach (var profile in allProfiles)
        {
            var gp = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
            var co = CollaborativeFilter.BuildCollaborativeMap(profile, allProfiles, precomputedUserSets);
            var cm = 0.0;
            foreach (var v in co.Values)
            {
                if (double.IsFinite(v) && v > cm)
                {
                    cm = v;
                }
            }

            var ay = ContentScoring.ComputeAverageYear(profile);
            var ge = PreferenceBuilder.BuildGenreExposureAnalysis(gp, profile);
            var pw = PreferenceBuilder.BuildPeoplePreferenceWeights(profile, cachedPeopleLookup, seriesEpisodeCounts);
            var ps = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, itemStudiosLookup);
            var pt = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, itemTagsLookup);
            // The new content-affinity preference maps read directly off WatchedItemInfo's cached
            // fields, so the SAME PreferenceBuilder functions used at live scoring time apply here -
            // identical inputs -> identical maps -> train/serve parity by construction.
            var pf = PreferenceBuilder.BuildFranchisePreferenceVector(profile);
            var pc = PreferenceBuilder.BuildProductionCountryPreferenceVector(profile);
            var pit = PreferenceBuilder.BuildInheritedTagPreferenceSet(profile);
            var pww = PreferenceBuilder.BuildWriterPreferenceWeights(profile);
            perUserCache[profile.UserId] = (gp, co, cm, ay, ge, pw, ps, pt, pf, pc, pit, pww);
            profileById[profile.UserId] = profile;
        }
    }

    /// <summary>
    ///     Phase 1: emits recommendation-feedback training examples into <paramref name="examples"/>.
    /// </summary>
    private static void EmitRecommendationFeedbackExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Dictionary<Guid, HashSet<Guid>> profileLookup,
        Dictionary<Guid, HashSet<Guid>> seriesLookup,
        Dictionary<Guid, UserWatchProfile> profileById,
        Dictionary<Guid, PerUserArtifacts> perUserCache,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        List<TrainingExample> examples,
        CancellationToken cancellationToken)
    {
        foreach (var prevResult in previousResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!profileLookup.TryGetValue(prevResult.UserId, out var watchedIds))
            {
                continue;
            }

            seriesLookup.TryGetValue(prevResult.UserId, out var watchedSeriesIds);

            if (!profileById.TryGetValue(prevResult.UserId, out var userProfile))
            {
                continue;
            }

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposure, preferredPeopleWeights, preferredStudios, preferredTags,
                preferredFranchises, preferredCountries, preferredInheritedTags, preferredWriterWeights) =
                perUserCache[userProfile.UserId];

            var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
            foreach (var w in userProfile.WatchedItems)
            {
                watchedItemLookup.TryAdd(w.ItemId, w);
            }

            // Build series episode lookup for series-level aggregation
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

            // Build watched genre/people/studio sets for ContentNearestNeighborScore. Mirrors
            // Engine.GenerateForUser (parallel lists indexed by watched item). Filter on
            // HasMeaningfulInteraction() to match the live path (PlayCount > 0 / PlaybackPositionTicks > 0,
            // not just Played || IsFavorite); a narrower filter would shrink the training watched set vs.
            // inference, skewing ContentNearestNeighborScore.
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
                    cachedPeopleLookup.TryGetValue(w.ItemId, out var wp) ? wp : []);

                HashSet<string> studioSet = [];
                if (itemStudiosLookup.TryGetValue(w.ItemId, out var ws) && ws.Count > 0)
                {
                    studioSet = new HashSet<string>(ws, StringComparer.OrdinalIgnoreCase);
                }
                else if (w.SeriesId.HasValue
                         && itemStudiosLookup.TryGetValue(w.SeriesId.Value, out var ss) && ss.Count > 0)
                {
                    studioSet = new HashSet<string>(ss, StringComparer.OrdinalIgnoreCase);
                }

                watchedStudioSets.Add(studioSet);
            }

            // Build per-user watchedBoxSetCounts by iterating the user's watched items directly (matches
            // Engine.BuildWatchedBoxSetCounts). Uses the global itemBoxSetIdsLookup so organic watches
            // (never recommended to this user) also contribute BoxSet membership when the item was
            // recommended to at least one OTHER user and thus has cached BoxSet metadata. Previously only
            // this user's recommendations counted, systematically under-counting BoxSet progression.
            var watchedBoxSetCounts = new Dictionary<Guid, int>();
            foreach (var watchedId in BuildWatchedIdSet(watchedIds, watchedSeriesIds))
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

            foreach (var rec in prevResult.Recommendations)
            {
                examples.Add(
                    BuildRecommendationFeedbackExample(
                        rec,
                        prevResult,
                        userProfile,
                        watchedIds,
                        watchedSeriesIds,
                        watchedItemLookup,
                        seriesEpisodeLookup,
                        genrePreferences,
                        coOccurrence,
                        collaborativeMax,
                        avgYear,
                        genreExposure,
                        preferredPeopleWeights,
                        preferredStudios,
                        preferredTags,
                        preferredFranchises,
                        preferredCountries,
                        preferredInheritedTags,
                        preferredWriterWeights,
                        cachedPeopleLookup,
                        watchedGenreSets,
                        watchedPeopleSets,
                        watchedStudioSets,
                        watchedBoxSetCounts,
                        genreStudioIdf));
            }
        }
    }

    /// <summary>
    ///     Builds a single Phase 1 recommendation-feedback training example for one recommended item.
    /// </summary>
    private static TrainingExample BuildRecommendationFeedbackExample(
        RecommendedItem rec,
        RecommendationResult prevResult,
        UserWatchProfile userProfile,
        HashSet<Guid> watchedIds,
        HashSet<Guid>? watchedSeriesIds,
        Dictionary<Guid, WatchedItemInfo> watchedItemLookup,
        Dictionary<Guid, List<WatchedItemInfo>> seriesEpisodeLookup,
        Dictionary<string, double> genrePreferences,
        Dictionary<Guid, double> coOccurrence,
        double collaborativeMax,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        IReadOnlyDictionary<string, double> preferredPeopleWeights,
        HashSet<string> preferredStudios,
        HashSet<string> preferredTags,
        IReadOnlyDictionary<string, double> preferredFranchises,
        IReadOnlyDictionary<string, double> preferredCountries,
        HashSet<string> preferredInheritedTags,
        IReadOnlyDictionary<string, double> preferredWriterWeights,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        List<HashSet<string>> watchedGenreSets,
        List<HashSet<string>> watchedPeopleSets,
        List<HashSet<string>> watchedStudioSets,
        Dictionary<Guid, int> watchedBoxSetCounts,
        IReadOnlyDictionary<string, double>? genreStudioIdf)
    {
        var wasWatched = watchedIds.Contains(rec.ItemId)
                         || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);

        watchedItemLookup.TryGetValue(rec.ItemId, out var watchedItemForRec);

        var isSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        // Compute user-specific signals matching Engine.ScoreCandidate() logic.
        // Train/serve parity: a Series that a user meaningfully interacted with is filtered
        // out of the live candidate pool by GenerateForUser's watchedSeriesIds check, so it
        // never reaches ScoreCandidate. Feeding real per-episode averages here trained
        // signals the inference path never sees; those channels are now neutralised. The
        // "series was recommended and later watched" signal survives via the positive
        // wasWatched label below and via CompletionRatio, which the neural model consumes
        // as an engagement magnitude rather than a user-interaction gate.
        //
        // We still resolve watchedItemForRec from the most recent episode so that temporal
        // features (DayOfWeek/HourOfDay/IsWeekend) - which do have real inference-time
        // signal from user-anchored watch times - stay grounded.
        double userRatingScore;
        double completionRatio;
        bool hasUserInteraction;

        switch (isSeries)
        {
            case true when seriesEpisodeLookup.TryGetValue(rec.ItemId, out var episodesForScoring):
                {
                    watchedItemForRec = episodesForScoring
                        .OrderByDescending(e => e.LastPlayedDate)
                        .FirstOrDefault();

                    // Neutralise all user-interaction channels to match inference: the live path
                    // filters watched series out of the candidate pool entirely, so any series
                    // candidate at inference has hasUserInteraction=false and completionRatio=0.0.
                    hasUserInteraction = false;
                    userRatingScore = 0.5;
                    completionRatio = 0.0;
                    break;
                }

            case true when wasWatched && watchedItemForRec is null:
                // Series-level favorite without watched episodes. The live path never sees
                // this series (favorite pushes it into watchedSeriesIds), so all user-
                // interaction features must be neutral. The positive intent still flows
                // through the label branch below.
                hasUserInteraction = false;
                userRatingScore = 0.5;
                completionRatio = 0.0;
                break;
            default:
                hasUserInteraction = watchedItemForRec is not null;
                userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItemForRec);
                completionRatio = hasUserInteraction
                    ? ContentScoring.ComputeCompletionRatio(watchedItemForRec)
                    : 0.0;
                break;
        }

        // Compute collaborative score for this specific item
        var collabScore = ContentScoring.ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax);

        // Popularity proxy matching Engine.ScoreCandidate() logic
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(rec.CommunityRating, rec.CriticRating);
        var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

        // Series progression boost: hardcoded 0.0 to mirror inference. The live path in
        // Engine.ScoreCandidate now writes a constant 0.0 for this channel because the
        // watchedSeriesIds filter upstream removes any series with meaningful interaction
        // from the candidate pool before scoring. Emitting a non-zero value here would
        // reintroduce train/serve skew: training would see a boost the inference path never emits.
        const double seriesProgressionBoost = 0.0;

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var peopleSimilarity = cachedPeopleLookup.TryGetValue(rec.ItemId, out var candidatePeople)
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

        // Build the COMPLETE feature vector matching Engine.ScoreCandidate() logic
        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(rec.Genres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            // Matches Engine.ScoreCandidate: PremiereDate ?? DateCreated ?? neutral 0.5.
            // The third fallback covers legacy cache entries from before 2.0.0.3 where
            // DateCreated was not yet persisted on RecommendedItem.
            RecencyScore = rec.PremiereDate.HasValue
                ? ContentScoring.ComputeRecencyScore(rec.PremiereDate.Value)
                : rec.DateCreated.HasValue
                    ? ContentScoring.ComputeRecencyScore(rec.DateCreated.Value)
                    : 0.5,
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
                watchedGenreSets,
                watchedPeopleSets,
                watchedStudioSets),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(rec.AudioLanguages, userProfile),
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(rec.BoxSetIds, watchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(rec.SubtitleLanguages, userProfile),
            // Content-affinity signals - SAME shared helpers as live scoring, over the cached
            // RecommendedItem fields. Missing/legacy fields (null string, empty list) self-neutralize
            // to 0.0 (or 0.5 for completability) inside the helpers, so old cache entries never crash.
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(rec.TmdbCollectionName, preferredFranchises),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(rec.ProductionCountries, preferredCountries),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(rec.InheritedTags, preferredInheritedTags),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, rec.SeriesStatus, rec.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(rec.WriterNames, preferredWriterWeights),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(rec.PeopleNames, rec.PeopleWeights), preferredPeopleWeights),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(rec.Genres, rec.Studios, genreStudioIdf)
        };

        // Genre exposure features: compute from cached per-user analysis
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(rec.Genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        double label;
        if (wasWatched)
        {
            // Determine base label based on interaction type:
            // 1. Favorite-only (no playback): explicit interest signal -> 0.65
            // 2. Abandoned (started but stopped early): strong negative signal -> 0.0
            // 3. Normal watch: engagement-proportional label (0.5-0.85)
            double baseLabel;
            switch (watchedItemForRec)
            {
                case { IsFavorite: true, Played: false, PlaybackPositionTicks: <= 0, PlayCount: <= 0 }:
                // Series-level favorite without episode data
                case null when isSeries:
                    baseLabel = 0.65; // Favorite-only: explicit interest without playback
                    break;
                default:
                    {
                        // User started the item but abandoned it early - this is a stronger
                        // negative signal than "never seen" (exposure). Active rejection > passive ignore.
                        baseLabel =
                            features.CompletionRatio is > 0 and < EngineConstants.AbandonedCompletionThreshold
                                ? EngineConstants.AbandonedLabel
                                : ContentScoring.ComputeEngagementLabel(features.CompletionRatio);
                        break;
                    }
            }

            // Watched shortly after recommendation - boost label (but not abandoned items)
            label = baseLabel > EngineConstants.AbandonedLabel
                    && watchedItemForRec?.LastPlayedDate is not null
                    && (watchedItemForRec.LastPlayedDate.Value - prevResult.GeneratedAt).TotalDays
                    <= EngineConstants.RecommendationInfluenceWindowDays
                    && watchedItemForRec.LastPlayedDate.Value >= prevResult.GeneratedAt
                ? Math.Max(baseLabel, EngineConstants.RecommendationInfluencedLabel)
                : baseLabel;
        }
        else if (features.CompletionRatio is > 0 and < EngineConstants.AbandonedCompletionThreshold)
        {
            label = EngineConstants.AbandonedLabel;
        }
        else
        {
            label = EngineConstants.ExposureLabel;
        }

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = prevResult.GeneratedAt
        };
    }

    /// <summary>
    ///     Builds the per-user set of item IDs recommended to that user across all previous results.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> BuildRecommendedItemIdsByUser(
        IReadOnlyList<RecommendationResult> previousResults)
    {
        // === Phase 2: Add organic watch examples (watched-but-never-recommended items) ===
        // Items the user found and watched on their own are strong positive signal the
        // recommendation-only approach misses, reducing training bias. Per-user recommended sets keep
        // an item recommended to user A from suppressing user B's organic discovery of the same item.
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
    ///     Phase 2: emits organic (watched-but-never-recommended) examples into <paramref name="examples"/>.
    /// </summary>
    /// <returns>The number of organic examples emitted.</returns>
    private static int EmitOrganicExamples(
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, PerUserArtifacts> perUserCache,
        Dictionary<Guid, HashSet<Guid>> recommendedItemIdsByUser,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        DateTime organicFallbackTimestamp,
        List<TrainingExample> examples,
        CancellationToken cancellationToken)
    {
        var organicCount = 0;
        foreach (var userProfile in allProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureOrganic,
                 preferredPeopleWeightsOrganic, preferredStudiosOrganic, preferredTagsOrganic,
                 preferredFranchisesOrganic, preferredCountriesOrganic, preferredInheritedTagsOrganic, preferredWriterWeightsOrganic) =
                perUserCache[userProfile.UserId];

            // Resolve the per-user recommended set; users with no previous results get an empty set
            if (!recommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Build series episode lookup, seriesWithOrgEpisodes set, and watched-id sets
            // in a single pass over WatchedItems to avoid four separate iterations.
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

            // === Series aggregation: collapse episodes into one example per series ===
            // Without aggregation, a series with 50 episodes produces 50 training examples,
            // massively skewing the dataset toward that series. Instead, group episodes by
            // SeriesId and emit a single aggregated TrainingExample per series. Standalone
            // items (movies, series-level favorites without SeriesId) are emitted 1:1 as before.
            var aggregatedSeriesIds = new HashSet<Guid>();

            // Build per-user BoxSet counts for Phase 2 organic items so that standalone organic
            // movies/series receive a real CollectionProgressionBoost feature rather than the
            // hardcoded 0.0 that would create a train/serve skew (the live scoring path always
            // computes the real boost via Engine.ComputeCollectionProgressionBoostLive).
            var watchedBoxSetCountsOrganic = new Dictionary<Guid, int>();
            foreach (var watchedId in BuildWatchedIdSet(watchedItemIds, watchedSeriesIds))
            {
                if (!itemBoxSetIdsLookup.TryGetValue(watchedId, out var orgBoxSetIds))
                {
                    continue;
                }

                foreach (var boxSetId in orgBoxSetIds)
                {
                    watchedBoxSetCountsOrganic.TryGetValue(boxSetId, out var cnt);
                    watchedBoxSetCountsOrganic[boxSetId] = cnt + 1;
                }
            }

            foreach (var w in userProfile.WatchedItems)
            {
                // Include played OR favorited items that were NEVER recommended (organic discoveries).
                if (!w.HasMeaningfulInteraction() || recommendedItemIds.Contains(w.ItemId))
                {
                    continue;
                }

                // Skip series IDs already covered by Phase 1 recommendations
                if (w.SeriesId.HasValue && recommendedItemIds.Contains(w.SeriesId.Value))
                {
                    continue;
                }

                // For episodes belonging to a series, aggregate at the series level.
                // Skip if this series was already aggregated from an earlier episode row.
                if (w.SeriesId.HasValue)
                {
                    if (!aggregatedSeriesIds.Add(w.SeriesId.Value))
                    {
                        continue; // Already emitted an aggregated example for this series
                    }

                    // Retrieve all episodes for this series from the pre-built lookup
                    if (seriesEpisodeLookupOrganic.TryGetValue(w.SeriesId.Value, out var seriesEpisodes))
                    {
                        TrainingFeatureComputer.AddAggregatedSeriesExample(
                            examples,
                            seriesEpisodes,
                            w.SeriesId.Value,
                            userProfile,
                            genrePreferences,
                            coOccurrence,
                            collaborativeMax,
                            avgYear,
                            genreExposureOrganic,
                            cachedPeopleLookup,
                            preferredPeopleWeightsOrganic,
                            itemStudiosLookup,
                            preferredStudiosOrganic,
                            itemTagsLookup,
                            preferredTagsOrganic,
                            preferredFranchisesOrganic,
                            preferredCountriesOrganic,
                            preferredInheritedTagsOrganic,
                            preferredWriterWeightsOrganic,
                            genreStudioIdf,
                            organicFallbackTimestamp);
                        organicCount++;
                    }

                    continue;
                }

                // === Standalone items (movies, series-level favorites without SeriesId) ===
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

                // If this standalone series has episode rows in the organic set, skip it -
                // the episode-based aggregation path (above) produces richer training signals.
                // Without this guard, iteration order could cause the standalone row to "win"
                // the aggregatedSeriesIds race and suppress episode-level aggregation.
                if (isSeries && seriesWithOrgEpisodes.Contains(w.ItemId))
                {
                    continue;
                }

                // If this standalone item is a Series object (w.SeriesId == null, w.ItemType == "Series")
                // and the series was already emitted via the aggregation path above (episode rows with matching
                // SeriesId), skip to avoid double-counting the same series with two training examples.
                if (isSeries && aggregatedSeriesIds.Contains(w.ItemId))
                {
                    continue;
                }

                // Mark this standalone series as aggregated so that if episode rows for the same
                // series appear later, the aggregation path won't emit a duplicate example.
                if (isSeries)
                {
                    aggregatedSeriesIds.Add(w.ItemId);
                }

                // Compute PeopleSimilarity from cached data using the weighted overload
                // Matches Engine.ScoreCandidate() live logic for train/serve parity.
                examples.Add(
                    BuildOrganicStandaloneExample(
                        w,
                        userProfile,
                        collabScore,
                        combinedCriticScore,
                        completionRatio,
                        isSeries,
                        genrePreferences,
                        avgYear,
                        genreExposureOrganic,
                        preferredPeopleWeightsOrganic,
                        preferredStudiosOrganic,
                        preferredTagsOrganic,
                        preferredFranchisesOrganic,
                        preferredCountriesOrganic,
                        preferredInheritedTagsOrganic,
                        preferredWriterWeightsOrganic,
                        cachedPeopleLookup,
                        itemStudiosLookup,
                        itemTagsLookup,
                        itemBoxSetIdsLookup,
                        watchedBoxSetCountsOrganic,
                        genreStudioIdf,
                        organicFallbackTimestamp));
                organicCount++;
            }
        }

        return organicCount;
    }

    /// <summary>
    ///     Builds a single Phase 2 organic standalone (non-series-episode) training example.
    /// </summary>
    private static TrainingExample BuildOrganicStandaloneExample(
        WatchedItemInfo w,
        UserWatchProfile userProfile,
        double collabScore,
        double combinedCriticScore,
        double completionRatio,
        bool isSeries,
        Dictionary<string, double> genrePreferences,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposureOrganic,
        IReadOnlyDictionary<string, double> preferredPeopleWeightsOrganic,
        HashSet<string> preferredStudiosOrganic,
        HashSet<string> preferredTagsOrganic,
        IReadOnlyDictionary<string, double> preferredFranchisesOrganic,
        IReadOnlyDictionary<string, double> preferredCountriesOrganic,
        HashSet<string> preferredInheritedTagsOrganic,
        IReadOnlyDictionary<string, double> preferredWriterWeightsOrganic,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup,
        Dictionary<Guid, int> watchedBoxSetCountsOrganic,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        DateTime organicFallbackTimestamp)
    {
        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var peopleSimilarity = cachedPeopleLookup.TryGetValue(w.ItemId, out var organicPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(organicPeople, preferredPeopleWeightsOrganic)
            : 0.0;

        // Compute StudioMatch and TagSimilarity from precomputed lookups (by item ID only).
        var studioMatch = false;
        var tagSimilarity = 0.0;

        // Resolve the item's studios once: used for BOTH StudioMatch and the genre/studio IDF
        // prior below. The live scoring path feeds candidate.Studios into
        // ComputeGenreStudioIdfPrior, so the organic training path must feed the same real
        // studios (not null) or the prior's studio-rarity contribution silently differs
        // between train and serve.
        itemStudiosLookup.TryGetValue(w.ItemId, out var organicStudios);

        if (organicStudios is { Count: > 0 })
        {
            studioMatch = organicStudios.Any(preferredStudiosOrganic.Contains);
        }

        if (itemTagsLookup.TryGetValue(w.ItemId, out var organicTags) && organicTags.Count > 0)
        {
            tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
        }

        // Series progression boost: hardcoded 0.0. Standalone organic series rows never
        // re-enter the live candidate pool (watchedSeriesIds pushes them out in
        // Engine.GenerateForUser), so any non-zero value here would produce a
        // distribution the neural network never sees at inference. Kept as a named local
        // to preserve the surrounding assignment shape.
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
            // Use content release year for recency (not watch date) to match Phase 1 semantics.
            // Phase 1 uses rec.PremiereDate; organic items lack premiere metadata so
            // approximate via ProductionYear, falling back to neutral 0.5.
            RecencyScore = w.Year is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
            GenreCount = wGenres.Count,
            IsSeries = isSeries,
            // Train/serve parity: at inference these organic items are unwatched candidates
            // (candidate.Id not in watchedItemLookup), so UserRatingScore is the neutral 0.5
            // default from ComputeUserRatingScore(null) and HasUserInteraction is false. Feeding
            // real w.UserRating / true here would skew the feature distribution; the "user liked
            // this" signal is already carried by the positive Label below.
            UserRatingScore = 0.5,
            HasUserInteraction = false,
            CompletionRatio = completionRatio,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
            CollectionProgressionBoost = itemBoxSetIdsLookup.TryGetValue(w.ItemId, out var orgBoxSetIds2)
                ? ComputeCollectionProgressionBoostWithCounts(orgBoxSetIds2, watchedBoxSetCountsOrganic)
                : 0.0,
            // Popularity prior: identical composition to Phase 1, Phase 3 and the live scoring
            // path (collaborative + critic blend). Previously omitted here, leaving organic
            // examples at the 0.0 default while every other path fed a real value - a
            // train/serve skew on this dimension. collabScore/combinedCriticScore are the same
            // locals already feeding CollaborativeScore/CombinedCriticScore above.
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, w.LastPlayedDate),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = w.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value)
                : 0.5,
            // Organic standalone items lack per-item stream metadata (AudioLanguages/SubtitleLanguages
            // are only cached on RecommendedItem from Phase 1). Use neutral 0.5 to match the live
            // scoring path which also returns 0.5 when stream data is unavailable, preventing
            // train/serve skew on these two dimensions.
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            // Content-affinity signals from WatchedItemInfo's cached fields, using the SAME shared
            // helpers as live scoring. BillingWeightedPeople now reads the cached PeopleNames/
            // PeopleWeights (billing weights persisted at watch-history build) so organic examples
            // carry the same real value the live path computes - closing the prior 0.0 skew.
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(w.TmdbCollectionName, preferredFranchisesOrganic),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(w.ProductionCountries, preferredCountriesOrganic),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(w.InheritedTags, preferredInheritedTagsOrganic),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, w.SeriesStatus, w.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(w.WriterNames, preferredWriterWeightsOrganic),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(w.PeopleNames, w.PeopleWeights), preferredPeopleWeightsOrganic),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(w.Genres, organicStudios, genreStudioIdf)
        };

        // Genre exposure features: compute from cached per-user analysis (mirrors Phase 1)
        var (organicUnderexp, organicDomRatio, organicAffGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(wGenres, genreExposureOrganic);
        features.GenreUnderexposure = organicUnderexp;
        features.GenreDominanceRatio = organicDomRatio;
        features.GenreAffinityGap = organicAffGap;

        // Organic watches are strong positive signals - label based on completion.
        // Favorite-only items (not played, no playback progress) get an explicit positive label.
        // Items started but abandoned (not played, but has playback progress) get a negative label.
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
            SampleWeight = 0.7 // Slightly lower weight than recommended items to avoid overwhelming
        };
    }

    /// <summary>
    ///     Phase 3: emits cross-user random-negative examples into <paramref name="examples"/>.
    /// </summary>
    /// <returns>The number of random-negative examples emitted.</returns>
    private static int EmitRandomNegativeExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, HashSet<Guid>> profileLookup,
        Dictionary<Guid, HashSet<Guid>> seriesLookup,
        Dictionary<Guid, HashSet<Guid>> recommendedItemIdsByUser,
        Dictionary<Guid, PerUserArtifacts> perUserCache,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        DateTime organicFallbackTimestamp,
        List<TrainingExample> examples,
        CancellationToken cancellationToken)
    {
        // === Phase 3: Random negative sampling (cross-user items the user never interacted with) ===
        // Phase 1 negatives are only items the system recommended to THIS user (exposure bias); Phase 2
        // adds only positives. Without true negatives the model lacks a "baseline irrelevant" class and
        // may overfit its own recommendation distribution. Cross-user negatives sample items recommended
        // to OTHER users that this user never touched - genuine "irrelevant for this user" examples with
        // full metadata available.
        var randomNegativeCount = 0;
        // Deduplicate by ItemId to prevent popular titles (recommended to multiple users)
        // from appearing multiple times in candidateNegatives, which would overweight them
        // as negatives purely because they were widely recommended elsewhere.
        var seenNegItemIds = new HashSet<Guid>();
        var allRecommendedItems = new List<RecommendedItem>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                if (seenNegItemIds.Add(rec.ItemId))
                {
                    allRecommendedItems.Add(rec);
                }
            }
        }

        if (allRecommendedItems.Count > 0)
        {
            foreach (var userProfile in allProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Per-user deterministic RNG: same cache + same user => same negatives.
                // Keeps training reproducible without coupling across users.
                var rngNeg = new Random(Engine.ComputeStableSeed(userProfile.UserId, previousResults.Count));

                if (!profileLookup.TryGetValue(userProfile.UserId, out var userWatchedIds))
                {
                    continue;
                }

                seriesLookup.TryGetValue(userProfile.UserId, out var userWatchedSeriesIds);

                if (!recommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var userRecommendedIds))
                {
                    userRecommendedIds = new HashSet<Guid>();
                }

                var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureNeg,
                     preferredPeopleWeightsNeg, preferredStudiosNeg, preferredTagsNeg,
                     preferredFranchisesNeg, preferredCountriesNeg, preferredInheritedTagsNeg, preferredWriterWeightsNeg) =
                    perUserCache[userProfile.UserId];

                // Build watched genre/people/studio sets for ContentNearestNeighborScore (mirrors Phase 1).
                // Use HasMeaningfulInteraction() for train/serve parity - see Phase 1 comment above.
                var watchedGenreSetsNeg = new List<HashSet<string>>();
                var watchedPeopleSetsNeg = new List<HashSet<string>>();
                var watchedStudioSetsNeg = new List<HashSet<string>>();
                foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
                {
                    watchedGenreSetsNeg.Add(
                        w.Genres is { Count: > 0 }
                            ? new HashSet<string>(w.Genres, StringComparer.OrdinalIgnoreCase)
                            : []);
                    watchedPeopleSetsNeg.Add(
                        cachedPeopleLookup.TryGetValue(w.ItemId, out var wpn) ? wpn : []);
                    HashSet<string> studioSetN = [];
                    if (itemStudiosLookup.TryGetValue(w.ItemId, out var wsn) && wsn.Count > 0)
                    {
                        studioSetN = new HashSet<string>(wsn, StringComparer.OrdinalIgnoreCase);
                    }
                    else if (w.SeriesId.HasValue
                             && itemStudiosLookup.TryGetValue(w.SeriesId.Value, out var ssn) && ssn.Count > 0)
                    {
                        studioSetN = new HashSet<string>(ssn, StringComparer.OrdinalIgnoreCase);
                    }

                    watchedStudioSetsNeg.Add(studioSetN);
                }

                // Build a per-user watchedBoxSetCounts lookup by iterating this user's watched items
                // directly and resolving BoxSet membership through the global itemBoxSetIdsLookup.
                // Matches Engine.BuildWatchedBoxSetCounts so organic watches also contribute counts
                // whenever the item was recommended to at least one other user (so we have BoxSet
                // metadata for it). This closes the earlier train/serve gap where Phase 3 only saw
                // items recommended to this user's neighbours in the recommendation set.
                var watchedBoxSetCountsNeg = new Dictionary<Guid, int>();
                foreach (var watchedId in BuildWatchedIdSet(userWatchedIds, userWatchedSeriesIds))
                {
                    if (!itemBoxSetIdsLookup.TryGetValue(watchedId, out var negBoxSetIds))
                    {
                        continue;
                    }

                    foreach (var boxSetId in negBoxSetIds)
                    {
                        watchedBoxSetCountsNeg.TryGetValue(boxSetId, out var count);
                        watchedBoxSetCountsNeg[boxSetId] = count + 1;
                    }
                }

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
                var sampleCount = Math.Min(EngineConstants.RandomNegativeSamplesPerUser, candidateNegatives.Count);
                for (var s = 0; s < sampleCount; s++)
                {
                    // Fisher-Yates partial shuffle to pick without replacement
                    var swapIdx = rngNeg.Next(s, candidateNegatives.Count);
                    (candidateNegatives[s], candidateNegatives[swapIdx]) =
                        (candidateNegatives[swapIdx], candidateNegatives[s]);

                    var neg = candidateNegatives[s];
                    examples.Add(
                        BuildRandomNegativeExample(
                            neg,
                            userProfile,
                            genrePreferences,
                            coOccurrence,
                            collaborativeMax,
                            avgYear,
                            genreExposureNeg,
                            preferredPeopleWeightsNeg,
                            preferredStudiosNeg,
                            preferredTagsNeg,
                            preferredFranchisesNeg,
                            preferredCountriesNeg,
                            preferredInheritedTagsNeg,
                            preferredWriterWeightsNeg,
                            cachedPeopleLookup,
                            watchedGenreSetsNeg,
                            watchedPeopleSetsNeg,
                            watchedStudioSetsNeg,
                            watchedBoxSetCountsNeg,
                            genreStudioIdf,
                            organicFallbackTimestamp));
                    randomNegativeCount++;
                }
            }
        }

        return randomNegativeCount;
    }

    /// <summary>
    ///     Builds a single Phase 3 cross-user random-negative training example.
    /// </summary>
    private static TrainingExample BuildRandomNegativeExample(
        RecommendedItem neg,
        UserWatchProfile userProfile,
        Dictionary<string, double> genrePreferences,
        Dictionary<Guid, double> coOccurrence,
        double collaborativeMax,
        double avgYear,
        PreferenceBuilder.GenreExposureAnalysis genreExposureNeg,
        IReadOnlyDictionary<string, double> preferredPeopleWeightsNeg,
        HashSet<string> preferredStudiosNeg,
        HashSet<string> preferredTagsNeg,
        IReadOnlyDictionary<string, double> preferredFranchisesNeg,
        IReadOnlyDictionary<string, double> preferredCountriesNeg,
        HashSet<string> preferredInheritedTagsNeg,
        IReadOnlyDictionary<string, double> preferredWriterWeightsNeg,
        Dictionary<Guid, HashSet<string>> cachedPeopleLookup,
        List<HashSet<string>> watchedGenreSetsNeg,
        List<HashSet<string>> watchedPeopleSetsNeg,
        List<HashSet<string>> watchedStudioSetsNeg,
        Dictionary<Guid, int> watchedBoxSetCountsNeg,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        DateTime organicFallbackTimestamp)
    {
        var collabScore = ContentScoring.ComputeCollaborativeScore(
            neg.ItemId,
            coOccurrence,
            collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(neg.CommunityRating, neg.CriticRating);
        var isSeries = string.Equals(neg.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

        // Compute PeopleSimilarity from cached data using the weighted overload
        // Matches Engine.ScoreCandidate() live logic for train/serve parity.
        var negPeopleSimilarity = cachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
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

        var features = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(negGenres, genrePreferences),
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = neg.PremiereDate.HasValue
                ? ContentScoring.ComputeRecencyScore(neg.PremiereDate.Value)
                : neg.DateCreated.HasValue
                    ? ContentScoring.ComputeRecencyScore(neg.DateCreated.Value)
                    : 0.5,
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
                watchedGenreSetsNeg,
                watchedPeopleSetsNeg,
                watchedStudioSetsNeg),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(neg.AudioLanguages, userProfile),
            // Same diminishing-returns formula as inference
            // (Engine.ComputeCollectionProgressionBoostLive), via the per-user
            // watchedBoxSetCountsNeg built above. Eliminates the prior train/serve divergence
            // where Phase 3 emitted 0.0/0.3/0.5 while inference emitted 0.3/0.5/0.7/0.9. An
            // empty dictionary yields 0.0, the correct signal.
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(neg.BoxSetIds, watchedBoxSetCountsNeg),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(neg.SubtitleLanguages, userProfile),
            // Content-affinity signals - same shared helpers, over the cached RecommendedItem
            // negative sample. neg carries the full field set (incl. PeopleWeights), so billing
            // is real here (unlike the organic branch). Empty/legacy fields self-neutralize.
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(neg.TmdbCollectionName, preferredFranchisesNeg),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(neg.ProductionCountries, preferredCountriesNeg),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(neg.InheritedTags, preferredInheritedTagsNeg),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, neg.SeriesStatus, neg.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(neg.WriterNames, preferredWriterWeightsNeg),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(neg.PeopleNames, neg.PeopleWeights), preferredPeopleWeightsNeg),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(neg.Genres, neg.Studios, genreStudioIdf)
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
            SampleWeight =
                0.5 // Lower weight than real interactions - we infer irrelevance, not observe it
        };
    }

    /// <summary>
    ///     Builds the union of watched item IDs and watched series IDs for BoxSet-count computation.
    ///     Shared by Phase 1 and Phase 3 to avoid duplicating the null-safe union pattern.
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
    ///     Computes CollectionProgressionBoost using the same diminishing-returns formula as
    ///     <see cref="Engine.ComputeCollectionProgressionBoostLive"/>, via a pre-built
    ///     <paramref name="watchedBoxSetCounts"/> dictionary (built once per user by iterating the user's
    ///     watched items through the global BoxSet lookup) for train/serve parity.
    ///     <para>
    ///         <c>internal</c> (not <c>private</c>) so the test assembly can call it directly via
    ///         <c>InternalsVisibleTo</c> without reflection.
    ///     </para>
    ///     <para>
    ///         The formula <c>0.3 + (n-1) × 0.2, clamped [0,1]</c> lives centrally in
    ///         <see cref="EngineConstants.ComputeCollectionProgressionBoost(int)"/>; both this training
    ///         path and <see cref="Engine.ComputeCollectionProgressionBoostLive"/> call it, making
    ///         copy-drift architecturally impossible. The 16 <c>CollectionProgressionBoostTests</c>
    ///         therefore protect both callers.
    ///     </para>
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
        // The formula itself is delegated to the shared helper in EngineConstants so the live
        // inference path (Engine.ComputeCollectionProgressionBoostLive) uses exactly the same
        // implementation - guaranteeing train/serve parity by construction.
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
}
