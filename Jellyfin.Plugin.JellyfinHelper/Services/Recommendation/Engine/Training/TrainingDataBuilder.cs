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
        return BuildExamples(previousResults, allProfiles, discoveryFeedback: null, seriesEpisodeCounts: null, genreStudioIdf: null, libraryItemMetadata: null, featureMeans: null, cancellationToken);
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
        return BuildExamples(previousResults, allProfiles, discoveryFeedback, seriesEpisodeCounts: null, genreStudioIdf: null, libraryItemMetadata: null, featureMeans: null, cancellationToken);
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
    /// <param name="libraryItemMetadata">
    ///     Item -> (studios/tags/BoxSet ids) map built from the live library. When supplied its entries
    ///     OVERWRITE the previous-recommendations cache so watched-item studios/tags resolve from the same
    ///     source the serve path reads. Null means cache-only behaviour (byte-identical to before).
    /// </param>
    /// <param name="featureMeans">
    ///     Optional per-feature training-set means used to impute the features that cannot be computed for
    ///     external (discovery) candidates, identical to discovery inference. Null keeps legacy constants.
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
        LibraryItemMetadata? libraryItemMetadata,
        IReadOnlyList<double>? featureMeans,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profileLookup = BuildProfileLookup(allProfiles);

        cancellationToken.ThrowIfCancellationRequested();

        var seriesLookup = BuildSeriesLookup(allProfiles);

        var cachedPeopleLookup = BuildCachedPeopleLookup(previousResults);

        BuildItemMetadataLookups(
            previousResults,
            out var itemStudiosLookup,
            out var itemTagsLookup,
            out var itemBoxSetIdsLookup);

        // Overlay the live-library metadata so watched items never previously recommended still resolve
        // their studios/tags/BoxSets exactly as the serve-time candidateLookup does. Library values win;
        // the cache remains the fallback for items the library no longer has.
        MergeLibraryItemMetadata(
            libraryItemMetadata,
            itemStudiosLookup,
            itemTagsLookup,
            itemBoxSetIdsLookup);

        var lookups = new TrainingLookups(
            cachedPeopleLookup,
            itemStudiosLookup,
            itemTagsLookup,
            itemBoxSetIdsLookup,
            genreStudioIdf,
            new Dictionary<WatchedItemInfo, CachedContentSets>(),
            new Dictionary<CachedContentSets, CachedContentSets>(new ContentSetComparer()),
            allProfiles.ToDictionary(profile => profile.UserId, profile => new AvailablePreferenceMetadata(
                profile.WatchedItems.Any(row => !string.IsNullOrWhiteSpace(row.TmdbCollectionName)),
                profile.WatchedItems.Any(row => row.ProductionCountries.Count > 0),
                profile.WatchedItems.Any(row => row.InheritedTags.Count > 0),
                profile.WatchedItems.Any(row => row.WriterNames.Count > 0))),
            BuildTitleIdLookup(previousResults, allProfiles, libraryItemMetadata));

        var examples = new List<TrainingExample>();

        var profileById = allProfiles.ToDictionary(profile => profile.UserId);

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
            Lookups = lookups,
            OrganicFallbackTimestamp = organicFallbackTimestamp,
            Examples = examples,
            CancellationToken = cancellationToken,
            HistoryCache = new TrainingHistoryCache(allProfiles, cancellationToken),
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
                featureMeans,
                cancellationToken);

            if (discoveryExamples.Count > 0)
            {
                examples.AddRange(discoveryExamples);
                discoveryCount = phase4Count;
            }
        }

        return (examples, organicCount, randomNegativeCount, discoveryCount);
    }

    private static Dictionary<Guid, int> BuildTitleIdLookup(IReadOnlyList<RecommendationResult> previous, Collection<UserWatchProfile> profiles, LibraryItemMetadata? library)
    {
        var ids = new Dictionary<Guid, int>();
        foreach (var item in previous.SelectMany(result => result.Recommendations.Concat(result.TrainingContrasts ?? [])).Where(item => item.TmdbId > 0))
        {
            ids[item.ItemId] = item.TmdbId;
        }

        foreach (var row in profiles.SelectMany(profile => profile.WatchedItems))
        {
            if (row.TmdbId > 0)
            {
                ids[row.ItemId] = row.TmdbId;
            }

            if (row.SeriesId is { } seriesId && row.SeriesTmdbId > 0)
            {
                ids[seriesId] = row.SeriesTmdbId;
            }
        }

        if (library?.TmdbIds is { } liveIds)
        {
            foreach (var pair in liveIds.Where(pair => pair.Value > 0))
            {
                ids[pair.Key] = pair.Value;
            }
        }

        return ids;
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
    ///     Overlays live-library item metadata onto the cache-derived lookups. Library entries OVERWRITE
    ///     cache entries (indexer assignment, not TryAdd) so a watched item present in the library resolves
    ///     its studios/tags/BoxSets identically to the serve-time candidateLookup; items the library no
    ///     longer has keep their cached value. No-op when <paramref name="libraryItemMetadata"/> is null.
    /// </summary>
    private static void MergeLibraryItemMetadata(
        LibraryItemMetadata? libraryItemMetadata,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup)
    {
        if (libraryItemMetadata is not { } metadata)
        {
            return;
        }

        foreach (var (itemId, studios) in metadata.Studios)
        {
            itemStudiosLookup[itemId] = studios;
        }

        foreach (var (itemId, tags) in metadata.Tags)
        {
            itemTagsLookup[itemId] = tags;
        }

        foreach (var (itemId, boxSetIds) in metadata.BoxSetIds)
        {
            itemBoxSetIdsLookup[itemId] = boxSetIds;
        }
    }

    private static PerUserArtifacts BuildArtifacts(
        UserWatchProfile profile,
        Dictionary<Guid, double> collaborative,
        TrainingLookups lookups,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts)
    {
        var genres = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
        var maximum = collaborative.Values.Where(v => double.IsFinite(v) && v > 0.0).DefaultIfEmpty(0.0).Max();
        var available = lookups.AvailableMetadata[profile.UserId];
        return (genres, collaborative, maximum, ContentScoring.ComputeAverageYear(profile),
            PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile),
            lookups.CachedPeopleLookup.Count == 0 && profile.PeopleProfile.Count == 0 ? new Dictionary<string, double>() : PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookups.CachedPeopleLookup, seriesEpisodeCounts),
            lookups.ItemStudiosLookup.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, lookups.ItemStudiosLookup),
            lookups.ItemTagsLookup.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, lookups.ItemTagsLookup),
            available.Franchises ? PreferenceBuilder.BuildFranchisePreferenceVector(profile) : new Dictionary<string, double>(),
            available.Countries ? PreferenceBuilder.BuildProductionCountryPreferenceVector(profile) : new Dictionary<string, double>(),
            available.InheritedTags ? PreferenceBuilder.BuildInheritedTagPreferenceSet(profile) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            available.Writers ? PreferenceBuilder.BuildWriterPreferenceWeights(profile) : new Dictionary<string, double>());
    }

    private static (UserWatchProfile Profile, PerUserArtifacts Artifacts, WatchedContentSets Content) HistoricalInputs(
        TrainingContext ctx, Guid userId, DateTime beforeUtc, HashSet<Guid> excluded)
    {
        var history = ctx.HistoryCache.Get(userId, beforeUtc, excluded);
        var profile = history.Profile;
        var artifacts = BuildArtifacts(profile, CollaborativeFilter.BuildCollaborativeMap(profile.UserId, history.Collaborative), ctx.Lookups, ctx.SeriesEpisodeCounts);
        var sets = BuildWatchedContentSets(profile, ctx.Lookups);
        var boxes = new Dictionary<Guid, int>();
        if (ctx.Lookups.ItemBoxSetIdsLookup.Count > 0)
        {
            var ids = new HashSet<Guid>(profile.WatchedItems.Count);
            foreach (var row in profile.WatchedItems)
            {
                ids.Add(row.ItemId);
                if (row.SeriesId is { } seriesId)
                {
                    ids.Add(seriesId);
                }
            }

            boxes = BuildWatchedBoxSetCounts(ids, ctx.Lookups.ItemBoxSetIdsLookup);
        }

        return (profile, artifacts, new WatchedContentSets(sets.Genre, sets.People, sets.Studio, boxes));
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

            if (!ctx.ProfileById.TryGetValue(prevResult.UserId, out var userProfile))
            {
                continue;
            }

            var userCtx = BuildRecommendationFeedbackContext(ctx, userProfile, watchedIds);

            foreach (var rec in prevResult.Recommendations)
            {
                var featureContext = GetRecommendationFeatureContext(rec, prevResult, userCtx, ctx);

                ctx.Examples.Add(
                    BuildRecommendationFeedbackExample(rec, prevResult, featureContext, ctx.Lookups));
            }
        }
    }

    private static Phase1UserContext BuildRecommendationFeedbackContext(TrainingContext ctx, UserWatchProfile userProfile, HashSet<Guid> watchedIds)
    {
        ctx.SeriesLookup.TryGetValue(userProfile.UserId, out var watchedSeriesIds);
        var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
        foreach (var item in userProfile.WatchedItems)
        {
            watchedItemLookup.TryAdd(item.ItemId, item);
        }

        // Only outcome lookups are needed for recorded predictions. Legacy feature inputs
        // are filled separately from their historical cutoff, never from the current profile.
        return new Phase1UserContext
        {
            UserProfile = userProfile,
            WatchedIds = watchedIds,
            WatchedSeriesIds = watchedSeriesIds,
            WatchedItemLookup = watchedItemLookup,
            SeriesEpisodeLookup = BuildSeriesEpisodeLookup(userProfile)
        };
    }

    private static Phase1UserContext GetRecommendationFeatureContext(RecommendedItem item, RecommendationResult result, Phase1UserContext userCtx, TrainingContext ctx)
    {
        if (item.ScoringFeatures is not null)
        {
            return userCtx;
        }

        var excluded = ContentScoring.BuildSeriesExcludeSet(item.ItemId, userCtx.UserProfile);
        var history = HistoricalInputs(ctx, userCtx.UserProfile.UserId, result.GeneratedAt, excluded);
        return userCtx with
        {
            UserProfile = history.Profile,
            Artifacts = history.Artifacts,
            ContentSets = history.Content,
            SeriesAffinity = ctx.SeriesEpisodeCounts is not null ? ContentScoring.BuildSeriesAffinityContext(history.Profile, ctx.SeriesEpisodeCounts) : null
        };
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
        var unique = new HashSet<CachedContentSets>();
        foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
        {
            if (!lookups.ContentByRow.TryGetValue(w, out var content))
            {
                content = BuildRowContentSets(w, lookups);
                lookups.ContentByRow.Add(w, content);
            }

            // Nearest-neighbour scoring takes a maximum: identical metadata cannot change it.
            if (!unique.Add(content))
            {
                continue;
            }

            watchedGenreSets.Add(content.Genres);
            watchedPeopleSets.Add(content.People);
            watchedStudioSets.Add(content.Studios);
        }

        return (watchedGenreSets, watchedPeopleSets, watchedStudioSets);
    }

    private static CachedContentSets BuildRowContentSets(WatchedItemInfo row, TrainingLookups lookups)
    {
        var genres = new HashSet<string>(row.Genres, StringComparer.OrdinalIgnoreCase);
        var people = lookups.CachedPeopleLookup.TryGetValue(row.ItemId, out var known) ? known : [];
        HashSet<string> studios = [];
        if (lookups.ItemStudiosLookup.TryGetValue(row.ItemId, out var ownStudios) && ownStudios.Count > 0)
        {
            studios = new HashSet<string>(ownStudios, StringComparer.OrdinalIgnoreCase);
        }
        else if (row.SeriesId.HasValue && lookups.ItemStudiosLookup.TryGetValue(row.SeriesId.Value, out var seriesStudios))
        {
            studios = new HashSet<string>(seriesStudios, StringComparer.OrdinalIgnoreCase);
        }

        var content = new CachedContentSets(genres, people, studios);
        if (lookups.SharedContent.TryGetValue(content, out var existing))
        {
            return existing;
        }

        lookups.SharedContent.Add(content, content);
        return content;
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
        var isSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase);
        var outcome = ResolveRecommendationOutcome(rec.ItemId, isSeries, prevResult, userCtx);

        if (rec.ScoringFeatures is not null)
        {
            return new TrainingExample
            {
                Features = rec.ScoringFeatures.Copy(),
                Label = outcome.Label,
                GeneratedAtUtc = prevResult.GeneratedAt,
                UserId = prevResult.UserId,
                ObservationId = TrainingExample.CreateTitleObservationId(prevResult.UserId, rec.ItemId, lookups.TmdbIds.GetValueOrDefault(rec.ItemId), isSeries),
                HasStableObservationId = true,
                IsObserved = outcome.IsObserved,
                IsContrastSample = outcome.IsContrast
            };
        }

        var contentSets = userCtx.ContentSets;
        var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposure, preferredPeopleWeights, preferredStudios, preferredTags,
            preferredFranchises, preferredCountries, preferredInheritedTags, preferredWriterWeights) = userCtx.Artifacts;
        // Legacy examples use detached historical inputs, including series-wide self-exclusion.
        var engagementExclude = isSeries ? ContentScoring.BuildSeriesExcludeSet(rec.ItemId, userProfile) : new HashSet<Guid> { rec.ItemId };
        var (familiarity, completionRatio, isAbandoned) = ContentScoring.ComputeGenreEngagement(rec.Genres, userProfile, engagementExclude);
        var hasUserInteraction = familiarity > 0.0;
        var userRatingScore = ContentScoring.ComputeGenreRatingScore(rec.Genres, userProfile, engagementExclude);
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
                new WatchedItemInfo { ItemId = rec.ItemId, LastPlayedDate = prevResult.GeneratedAt },
                recGenreSet,
                userProfile,
                isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
                new WatchedItemInfo { ItemId = rec.ItemId, LastPlayedDate = prevResult.GeneratedAt },
                recGenreSet,
                userProfile,
                isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, prevResult.GeneratedAt),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = rec.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(rec.DateCreated.Value)
                : ContentScoring.ComputeRecencyScore(default),
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

        return new TrainingExample
        {
            Features = features,
            Label = outcome.Label,
            GeneratedAtUtc = prevResult.GeneratedAt,
            UserId = userProfile.UserId,
            ObservationId = TrainingExample.CreateTitleObservationId(userProfile.UserId, rec.ItemId, lookups.TmdbIds.GetValueOrDefault(rec.ItemId), isSeries),
            HasStableObservationId = true,
            IsObserved = outcome.IsObserved,
            IsContrastSample = outcome.IsContrast
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
        double actualCompletionRatio,
        double? explicitRating)
    {
        if (explicitRating.HasValue)
        {
            return explicitRating.Value;
        }

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
                        baseLabel = ContentScoring.ComputeEngagementLabel(actualCompletionRatio);
                        break;
                    }
            }

            var isRecommendationInfluenced = baseLabel > EngineConstants.AbandonedLabel
                && watchedItemForRec?.LastPlayedDate is not null
                && (watchedItemForRec.LastPlayedDate.Value - prevResult.GeneratedAt).TotalDays
                    <= EngineConstants.RecommendationInfluenceWindowDays
                && watchedItemForRec.LastPlayedDate.Value >= prevResult.GeneratedAt;

            return isRecommendationInfluenced
                ? Math.Max(baseLabel, EngineConstants.RecommendationInfluencedLabel)
                : baseLabel;
        }

        return EngineConstants.ExposureLabel;
    }

    private static bool HasReliableOutcome(WatchedItemInfo? item, bool isSeries)
        => item is null ? isSeries : item.Played || item.IsFavorite || NormalizeExplicitRating(item.UserRating).HasValue;

    private static double? NormalizeExplicitRating(double? rating)
        => rating is > 0 && double.IsFinite(rating.Value) ? Math.Clamp(rating.Value / 10.0, 0.0, 1.0) : null;

    /// <summary>Explicit title ratings win; episode ratings are averaged consistently with organic series feedback.</summary>
    private static (double Label, bool IsObserved, bool IsContrast) ResolveRecommendationOutcome(Guid itemId, bool isSeries, RecommendationResult result, Phase1UserContext context)
    {
        var wasWatched = context.WatchedIds.Contains(itemId) || (context.WatchedSeriesIds?.Contains(itemId) ?? false);
        context.WatchedItemLookup.TryGetValue(itemId, out var representative);
        var rating = NormalizeExplicitRating(representative?.UserRating);
        if (isSeries && context.SeriesEpisodeLookup.TryGetValue(itemId, out var episodes))
        {
            rating ??= TrainingFeatureComputer.AverageExplicitRatings(episodes);
            // A newer partial play is not a completed or explicit outcome. It must not replace one.
            representative = episodes.Where(episode => HasReliableOutcome(episode, isSeries: false))
                .OrderByDescending(episode => episode.LastPlayedDate).FirstOrDefault()
                ?? representative
                ?? episodes.OrderByDescending(episode => episode.LastPlayedDate).FirstOrDefault();
        }

        var observed = wasWatched && HasReliableOutcome(representative, isSeries);
        var label = ComputeRecommendationFeedbackLabel(wasWatched, isSeries, representative, result, ContentScoring.ComputeCompletionRatio(representative), rating);
        return (label, observed, !wasWatched);
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

            if (!ctx.RecommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Single pass to collect lookups needed for organic examples.
            var (seriesEpisodeLookupOrganic, seriesWithOrgEpisodes) =
                PrescanOrganicWatchedItems(userProfile, recommendedItemIds);

            // Aggregate series so one show does not produce dozens of examples.
            var aggregatedSeriesIds = new HashSet<Guid>();

            var userCtx = new Phase2UserContext
            {
                UserProfile = userProfile,
                RecommendedItemIds = recommendedItemIds,
                SeriesEpisodeLookupOrganic = seriesEpisodeLookupOrganic,
                SeriesWithOrgEpisodes = seriesWithOrgEpisodes,
                AggregatedSeriesIds = aggregatedSeriesIds
            };

            foreach (var w in userProfile.WatchedItems)
            {
                organicCount += ProcessOrganicWatchedItem(w, userCtx, ctx);
            }
        }

        return organicCount;
    }

    /// <summary>
    ///     Builds the episode lookup and the set of series with organic (never-recommended) episodes in one pass.
    /// </summary>
    private static (Dictionary<Guid, List<WatchedItemInfo>> SeriesEpisodeLookup, HashSet<Guid> SeriesWithOrgEpisodes)
        PrescanOrganicWatchedItems(UserWatchProfile userProfile, HashSet<Guid> recommendedItemIds)
    {
        var seriesEpisodeLookupOrganic = new Dictionary<Guid, List<WatchedItemInfo>>();
        var seriesWithOrgEpisodes = new HashSet<Guid>();

        foreach (var ep in userProfile.WatchedItems)
        {
            if (ep.SeriesId.HasValue)
            {
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

        return (seriesEpisodeLookupOrganic, seriesWithOrgEpisodes);
    }

    /// <summary>
    ///     Processes one watched item for Phase 2, emitting either an aggregated series example or a standalone example (or nothing).
    /// </summary>
    private static int ProcessOrganicWatchedItem(
        WatchedItemInfo w,
        Phase2UserContext userCtx,
        TrainingContext ctx)
    {
        var recommendedItemIds = userCtx.RecommendedItemIds;
        var seriesWithOrgEpisodes = userCtx.SeriesWithOrgEpisodes;
        var aggregatedSeriesIds = userCtx.AggregatedSeriesIds;

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
            return EmitOrganicSeriesExample(w.SeriesId.Value, userCtx, ctx);
        }

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
                combinedCriticScore,
                completionRatio,
                isSeries));
        return 1;
    }

    private static int EmitOrganicSeriesExample(Guid seriesId, Phase2UserContext userCtx, TrainingContext ctx)
    {
        if (!userCtx.AggregatedSeriesIds.Add(seriesId) || !userCtx.SeriesEpisodeLookupOrganic.TryGetValue(seriesId, out var episodes))
        {
            return 0;
        }

        var before = episodes.Select(episode => episode.LastPlayedDate).Max() ?? ctx.OrganicFallbackTimestamp;
        var history = HistoricalInputs(ctx, userCtx.UserProfile.UserId, before, ContentScoring.BuildSeriesExcludeSet(seriesId, userCtx.UserProfile));
        var artifacts = history.Artifacts;
        TrainingFeatureComputer.AddAggregatedSeriesExample(
            ctx.Examples,
            episodes,
            seriesId,
            history.Profile,
            artifacts.GenrePreferences,
            artifacts.CoOccurrence,
            artifacts.CollaborativeMax,
            artifacts.AvgYear,
            artifacts.GenreExposure,
            ctx.Lookups.CachedPeopleLookup,
            artifacts.PeopleWeights,
            ctx.Lookups.ItemStudiosLookup,
            artifacts.PreferredStudios,
            ctx.Lookups.ItemTagsLookup,
            artifacts.PreferredTags,
            artifacts.PreferredFranchises,
            artifacts.PreferredCountries,
            artifacts.PreferredInheritedTags,
            artifacts.PreferredWriterWeights,
            ctx.Lookups.GenreStudioIdf,
            ctx.OrganicFallbackTimestamp,
            ctx.SeriesEpisodeCounts is not null ? ContentScoring.BuildSeriesAffinityContext(history.Profile, ctx.SeriesEpisodeCounts) : null,
            userCtx.UserProfile.WatchedItems.FirstOrDefault(row => row.ItemId == seriesId)?.UserRating,
            ctx.Lookups.TmdbIds.GetValueOrDefault(seriesId));
        return 1;
    }

    /// <summary>
    ///     Builds a single Phase 2 organic standalone (non-series-episode) training example.
    /// </summary>
    private static TrainingExample BuildOrganicStandaloneExample(
        WatchedItemInfo w,
        Phase2UserContext userCtx,
        TrainingContext ctx,
        double combinedCriticScore,
        double completionRatio,
        bool isSeries)
    {
        var excluded = isSeries ? ContentScoring.BuildSeriesExcludeSet(w.ItemId, userCtx.UserProfile) : new HashSet<Guid> { w.ItemId };
        var history = HistoricalInputs(ctx, userCtx.UserProfile.UserId, w.LastPlayedDate ?? ctx.OrganicFallbackTimestamp, excluded);
        var userProfile = history.Profile;
        var watchedBoxSetCountsOrganic = history.Content.WatchedBoxSetCounts;
        var lookups = ctx.Lookups;
        var organicFallbackTimestamp = ctx.OrganicFallbackTimestamp;

        var (genrePreferences, _, _, avgYear, genreExposureOrganic,
             preferredPeopleWeightsOrganic, preferredStudiosOrganic, preferredTagsOrganic,
             preferredFranchisesOrganic, preferredCountriesOrganic, preferredInheritedTagsOrganic, preferredWriterWeightsOrganic) =
             history.Artifacts;
        var collabScore = ContentScoring.ComputeCollaborativeScore(w.ItemId, history.Artifacts.CoOccurrence, history.Artifacts.CollaborativeMax);

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
        // Exclude the target from genre engagement to prevent label leakage. For a series the watch
        // records are its episodes (ItemId == episodeId), so excluding only w.ItemId (the series id)
        // would let its own episodes leak in. Built from the profile rather than the organic episode
        // lookup, which is a filtered subset: engagement scans the full profile, so a leak-safe exclude
        // must cover every record of this series, not just the ones that survived the lookup's filter.
        var organicExclude = isSeries
            ? ContentScoring.BuildSeriesExcludeSet(w.ItemId, userProfile)
            : new HashSet<Guid> { w.ItemId };

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
            SeriesAffinity = ctx.SeriesEpisodeCounts is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, w.ItemId, wGenres, ContentScoring.BuildSeriesAffinityContext(userProfile, ctx.SeriesEpisodeCounts), lookups.CachedPeopleLookup, excludeSeriesId: w.ItemId)
                : 0.0,
            CollectionProgressionBoost = lookups.ItemBoxSetIdsLookup.TryGetValue(w.ItemId, out var orgBoxSetIds2)
                ? ComputeCollectionProgressionBoostWithCounts(orgBoxSetIds2, watchedBoxSetCountsOrganic)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: true),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: false),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, w.LastPlayedDate ?? organicFallbackTimestamp),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = w.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value)
                : ContentScoring.ComputeRecencyScore(default),
            LanguageAffinity = 0.5,
            SubtitleLanguageAffinity = 0.5,
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                wGenres, w.PeopleNames, organicStudios ?? [], history.Content.WatchedGenreSets, history.Content.WatchedPeopleSets, history.Content.WatchedStudioSets),
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
        var label = ComputeOrganicStandaloneLabel(w, completionRatio);

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = w.LastPlayedDate ?? organicFallbackTimestamp,
            SampleWeight = 0.7,
            UserId = userProfile.UserId,
            ObservationId = TrainingExample.CreateTitleObservationId(userProfile.UserId, w.ItemId, lookups.TmdbIds.GetValueOrDefault(w.ItemId), isSeries),
            HasStableObservationId = true,
            IsObserved = HasReliableOutcome(w, isSeries)
        };
    }

    private static double ComputeOrganicStandaloneLabel(WatchedItemInfo item, double completionRatio)
        => item.UserRating is > 0 && double.IsFinite(item.UserRating.Value)
            ? Math.Clamp(item.UserRating.Value / 10.0, 0.0, 1.0)
            : item switch
            {
                { Played: false, PlaybackPositionTicks: <= 0, IsFavorite: true } => 0.65,
                _ => ContentScoring.ComputeEngagementLabel(completionRatio)
            };

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
        var predictionTimes = new Dictionary<Guid, DateTime>();
        foreach (var prevResult in ctx.PreviousResults)
        {
            foreach (var item in prevResult.Recommendations.Where(item => seenNegItemIds.Add(item.ItemId)))
            {
                allRecommendedItems.Add(item);
                predictionTimes.Add(item.ItemId, prevResult.GeneratedAt);
            }
        }

        if (allRecommendedItems.Count > 0)
        {
            foreach (var userProfile in ctx.AllProfiles)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();

                randomNegativeCount += ProcessUserRandomNegatives(userProfile, allRecommendedItems, predictionTimes, ctx);
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
        Dictionary<Guid, DateTime> predictionTimes,
        TrainingContext ctx)
    {
        var ownResult = ctx.PreviousResults.Where(result => result.UserId == userProfile.UserId).MaxBy(result => result.GeneratedAt);
        if (ownResult?.TrainingContrasts is not null)
        {
            return EmitRecordedContrasts(ownResult, ctx);
        }

        if (ownResult?.Recommendations.Any(item => item.ScoringFeatures is not null) == true)
        {
            // Old snapshot-aware caches lack contrast predictions. Phase 1's recorded unwatched
            // recommendations already provide contrasts; do not add mismatched full-history reconstructions.
            return 0;
        }

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
            ctx.Examples.Add(BuildHistoricalNegativeExample(neg, userProfile.UserId, predictionTimes[neg.ItemId], ctx));
            randomNegativeCount++;
        }

        return randomNegativeCount;
    }

    private static int EmitRecordedContrasts(RecommendationResult result, TrainingContext ctx)
    {
        var emitted = 0;
        var watched = ctx.ProfileLookup[result.UserId];
        var series = ctx.SeriesLookup[result.UserId];
        var recommended = ctx.RecommendedItemIdsByUser.GetValueOrDefault(result.UserId);
        foreach (var item in result.TrainingContrasts!.Take(EngineConstants.RandomNegativeSamplesPerUser))
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (item.ScoringFeatures is null || watched.Contains(item.ItemId) || series.Contains(item.ItemId) || (recommended?.Contains(item.ItemId) ?? false))
            {
                continue;
            }

            ctx.Examples.Add(new TrainingExample
            {
                Features = item.ScoringFeatures.Copy(),
                Label = 0.0,
                GeneratedAtUtc = result.GeneratedAt,
                SampleWeight = 0.5,
                UserId = result.UserId,
                ObservationId = TrainingExample.CreateTitleObservationId(result.UserId, item.ItemId, ctx.Lookups.TmdbIds.GetValueOrDefault(item.ItemId), string.Equals(item.ItemType, "Series", StringComparison.OrdinalIgnoreCase)),
                HasStableObservationId = true,
                IsObserved = false,
                IsContrastSample = true
            });
            emitted++;
        }

        return emitted;
    }

    private static TrainingExample BuildHistoricalNegativeExample(RecommendedItem item, Guid userId, DateTime predictionTime, TrainingContext ctx)
    {
        var history = HistoricalInputs(ctx, userId, predictionTime, new HashSet<Guid>());
        var contexts = new NegativeScoringContexts(
            ctx.SeriesEpisodeCounts is not null ? ContentScoring.BuildSeriesAffinityContext(history.Profile, ctx.SeriesEpisodeCounts) : null,
            ContentScoring.BuildGenreEngagementContext(history.Profile));
        return BuildRandomNegativeExample(item, history.Profile, history.Artifacts, history.Content, ctx.Lookups, predictionTime, contexts);
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
        DateTime predictionTime,
        NegativeScoringContexts scoringContexts)
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

        var (familiarityNeg, genreAvgCompletionNeg, genreAbandonRateNeg) = ContentScoring.ComputeGenreEngagement(negGenres, scoringContexts.GenreEngagement);
        var userRatingScoreNeg = ContentScoring.ComputeGenreRatingScore(negGenres, scoringContexts.GenreEngagement);
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
            SeriesAffinity = scoringContexts.SeriesAffinity is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, neg.ItemId, negGenres, scoringContexts.SeriesAffinity, lookups.CachedPeopleLookup)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, predictionTime),
            TagSimilarity = negTagSimilarity,
            LibraryAddedRecency = neg.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(neg.DateCreated.Value)
                : ContentScoring.ComputeRecencyScore(default),
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
            GeneratedAtUtc = predictionTime,
            SampleWeight = 0.5,
            UserId = userProfile.UserId,
            ObservationId = TrainingExample.CreateTitleObservationId(userProfile.UserId, neg.ItemId, lookups.TmdbIds.GetValueOrDefault(neg.ItemId), isSeries),
            HasStableObservationId = true,
            IsObserved = false,
            IsContrastSample = true
        };
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
        IReadOnlyDictionary<string, double>? GenreStudioIdf,
        Dictionary<WatchedItemInfo, CachedContentSets> ContentByRow,
        Dictionary<CachedContentSets, CachedContentSets> SharedContent,
        Dictionary<Guid, AvailablePreferenceMetadata> AvailableMetadata,
        Dictionary<Guid, int> TmdbIds);

    /// <summary>
    ///     Groups per user watched content sets and BoxSet counts.
    /// </summary>
    private readonly record struct WatchedContentSets(
        List<HashSet<string>> WatchedGenreSets,
        List<HashSet<string>> WatchedPeopleSets,
        List<HashSet<string>> WatchedStudioSets,
        Dictionary<Guid, int> WatchedBoxSetCounts);

    /// <summary>
    ///     Per-user scoring contexts built once and reused across a user's random-negative samples: the
    ///     series-affinity context (null when episode counts are unavailable) and the genre-engagement
    ///     context. Bundled so the negative-example builder takes them as one argument.
    /// </summary>
    private readonly record struct NegativeScoringContexts(
        ContentScoring.SeriesAffinityContext? SeriesAffinity,
        ContentScoring.GenreEngagementContext GenreEngagement);

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

        /// <summary>Gets the shared item-metadata lookups and genre/studio IDF table.</summary>
        public TrainingLookups Lookups { get; init; }

        /// <summary>Gets the deterministic timestamp anchor for organic items without a play date.</summary>
        public DateTime OrganicFallbackTimestamp { get; init; }

        /// <summary>Gets the mutable output list that all phases append training examples to.</summary>
        public List<TrainingExample> Examples { get; init; }

        /// <summary>Gets the cancellation token for the build operation.</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>Gets the bounded historical neighbour cache, owned exclusively by this build.</summary>
        public TrainingHistoryCache HistoryCache { get; init; }

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
    }

    private sealed record CachedContentSets(HashSet<string> Genres, HashSet<string> People, HashSet<string> Studios);

    private sealed record AvailablePreferenceMetadata(bool Franchises, bool Countries, bool InheritedTags, bool Writers);

    private sealed record ContentSetComparer : IEqualityComparer<CachedContentSets>
    {
        public bool Equals(CachedContentSets? x, CachedContentSets? y)
            => ReferenceEquals(x, y) || (x is not null && y is not null && x.Genres.SetEquals(y.Genres) && x.People.SetEquals(y.People) && x.Studios.SetEquals(y.Studios));

        public int GetHashCode(CachedContentSets obj) => HashCode.Combine(SetHash(obj.Genres), SetHash(obj.People), SetHash(obj.Studios));

        private static int SetHash(HashSet<string> values)
        {
            var hash = 0;
            foreach (var value in values)
            {
                hash ^= value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value);
            }

            return hash;
        }
    }
}
