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
    private const string SeriesItemType = "Series";

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
        return BuildExamplesCore(new(previousResults, allProfiles, discoveryFeedback), new(seriesEpisodeCounts, genreStudioIdf, libraryItemMetadata, featureMeans), DateTime.UtcNow, cancellationToken);
    }

    /// <summary>Builds deterministic historical choice admission and contrast budgets against one supplied clock.</summary>
    /// <param name="previousResults">Original prediction snapshots.</param>
    /// <param name="allProfiles">Captured source outcomes.</param>
    /// <param name="referenceTime">One fixed fitting boundary, never a replacement for prediction timestamps.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Examples and phase counts.</returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount, int DiscoveryCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        DateTime referenceTime,
        CancellationToken cancellationToken)
        => BuildExamplesCore(new(previousResults, allProfiles, null), new(null, null, null, null), DateTimeNormalization.ToUtc(referenceTime), cancellationToken);

    private static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount, int DiscoveryCount) BuildExamplesCore(
        TrainingSources sources,
        TrainingFeatureInputs featureInputs,
        DateTime referenceTime,
        CancellationToken cancellationToken)
    {
        var (previousResults, allProfiles, discoveryFeedback) = sources;
        var (seriesEpisodeCounts, genreStudioIdf, libraryItemMetadata, featureMeans) = featureInputs;
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
            new Dictionary<CachedContentSets, CachedContentSets>(new ContentSetComparer()),
            allProfiles.ToDictionary(profile => profile.UserId, profile => new AvailablePreferenceMetadata(
                profile.WatchedItems.Any(row => row.TmdbCollectionId > 0 || !string.IsNullOrWhiteSpace(row.TmdbCollectionName)),
                profile.WatchedItems.Any(row => row.ProductionCountries.Count > 0),
                profile.WatchedItems.Any(row => row.InheritedTags.Count > 0),
                profile.WatchedItems.Any(row => row.WriterNames.Count > 0))),
            BuildTitleIdLookup(previousResults, allProfiles, libraryItemMetadata),
            BuildCollectionIdLookup(previousResults, allProfiles, libraryItemMetadata))
        {
            Continuation = libraryItemMetadata?.Continuation ?? CollectionContinuationCatalog.Empty,
            BoxSetProviderIds = libraryItemMetadata?.BoxSetProviderIds,
            AllowedCollectionItems = CollectionItemScope(libraryItemMetadata),
            Credits = BuildCreditMetadataLookup(previousResults, allProfiles, libraryItemMetadata)
        };

        var examples = new List<TrainingExample>();

        var profileById = allProfiles.ToDictionary(profile => profile.UserId);

        var recommendedItemIdsByUser = BuildRecommendedItemIdsByUser(previousResults);

        var organicFallbackTimestamp = ResolveOrganicFallbackTimestamp(previousResults, allProfiles);

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
            ReferenceTime = referenceTime,
            Examples = examples,
            CancellationToken = cancellationToken,
            HistoryCache = new TrainingHistoryCache(allProfiles, cancellationToken),
            SeriesEpisodeCounts = seriesEpisodeCounts,
            Choices = []
        };

        EmitRecommendationFeedbackExamples(ctx);

        var organicCount = EmitOrganicExamples(ctx);

        AttachRecommendationChoices(ctx);

        EmitRandomNegativeExamples(ctx);

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

        NormalizeChoiceContrasts(ctx);
        if (examples.Any(example => example.IsPassiveSample))
        {
            var observed = examples.Where(example => example.IsObserved).Select(example => (example.UserId, example.ObservationId)).ToHashSet();
            observed.UnionWith(ctx.ProfileById.Values.SelectMany(profile => RecommendationChoiceBuilder.EngagedTitles(profile, ctx.Lookups.TmdbIds)
                .Select(title => (profile.UserId, title))));
            var removed = examples.RemoveAll(example => example.IsPassiveSample && observed.Contains((example.UserId, example.ObservationId)));
            discoveryCount -= removed;
        }

        var randomNegativeCount = examples.Count(example => example.IsContrastSample);
        return (examples, organicCount, randomNegativeCount, discoveryCount);
    }

    private static void NormalizeChoiceContrasts(TrainingContext ctx)
    {
        if (ctx.Choices.Count == 0)
        {
            // No current opportunity can qualify any fitting comparison. Avoid
            // hashing every history title while still rejecting unshown candidates.
            ctx.Examples.RemoveAll(example => example.IsContrastSample);
            return;
        }

        var engaged = ctx.ProfileById.Values.SelectMany(profile => RecommendationChoiceBuilder.EngagedTitles(profile, ctx.Lookups.TmdbIds)
            .Select(title => (profile.UserId, title))).ToHashSet();
        RecommendationChoiceHistory.ApplyOpportunityContrasts(ctx.Examples, ctx.ReferenceTime, engaged);
    }

    /// <summary>Uses the earliest known source instant, never the training clock, for undated organic outcomes.</summary>
    /// <param name="previous">The available prediction timestamps.</param>
    /// <param name="profiles">The source playback histories.</param>
    /// <returns>A stable conservative cutoff; the epoch marks unknown age and prevents fabricated recent training weight.</returns>
    private static DateTime ResolveOrganicFallbackTimestamp(IReadOnlyList<RecommendationResult> previous, Collection<UserWatchProfile> profiles)
    {
        if (previous.Count > 0)
        {
            return previous.Min(result => DateTimeNormalization.ToUtc(result.GeneratedAt));
        }

        return profiles.SelectMany(profile => profile.WatchedItems)
            .Select(row => row.LastPlayedDate)
            .OfType<DateTime>()
            .Select(DateTimeNormalization.ToUtc)
            .DefaultIfEmpty(DateTime.UnixEpoch)
            .Min();
    }

    private static Dictionary<Guid, int> BuildCollectionIdLookup(IReadOnlyList<RecommendationResult> previous, Collection<UserWatchProfile> profiles, LibraryItemMetadata? library)
    {
        var ids = new Dictionary<Guid, int>();
        foreach (var item in previous.SelectMany(result => result.Recommendations).Where(item => item.TmdbCollectionId > 0))
        {
            ids[item.ItemId] = item.TmdbCollectionId;
        }

        foreach (var item in profiles.SelectMany(profile => profile.WatchedItems).Where(item => item.TmdbCollectionId > 0))
        {
            ids[item.ItemId] = item.TmdbCollectionId;
        }

        if (library?.TmdbCollectionIds is { } current)
        {
            foreach (var (item, id) in current)
            {
                ids[item] = id;
            }
        }

        return ids;
    }

    internal static Dictionary<Guid, int> BuildTitleIdLookup(IReadOnlyList<RecommendationResult> previous, Collection<UserWatchProfile> profiles, LibraryItemMetadata? library)
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
        out Dictionary<Guid, TitleMetadataSnapshot?> itemStudiosLookup,
        out Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        out Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup)
    {
        // Build id lookups once from all previous results.
        itemStudiosLookup = new Dictionary<Guid, TitleMetadataSnapshot?>();
        itemTagsLookup = new Dictionary<Guid, IReadOnlyList<string>>();
        itemBoxSetIdsLookup = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                itemStudiosLookup.TryAdd(rec.ItemId, rec.TitleMetadata);

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
        Dictionary<Guid, TitleMetadataSnapshot?> itemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup)
    {
        if (libraryItemMetadata is not { } metadata)
        {
            return;
        }

        foreach (var itemId in metadata.Studios.Keys)
        {
            // An unqualified native array cannot launder a producer identity. A
            // current observed null also withdraws an old cached company snapshot.
            itemStudiosLookup[itemId] = metadata.ProducerMetadata?.GetValueOrDefault(itemId);
        }

        foreach (var (itemId, tags) in metadata.Tags)
        {
            itemTagsLookup[itemId] = tags;
        }

        foreach (var (itemId, boxSetIds) in metadata.BoxSetIds)
        {
            itemBoxSetIdsLookup[itemId] = metadata.BoxSetMembershipsComplete
                ? boxSetIds
                : itemBoxSetIdsLookup.GetValueOrDefault(itemId, []).Concat(boxSetIds).Distinct().ToArray();
        }
    }

    private static PerUserArtifacts BuildArtifacts(
        UserWatchProfile profile,
        Dictionary<Guid, double> collaborative,
        TrainingLookups lookups,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        DateTime predictionTimeUtc,
        IEnumerable<WatchedItemInfo>? countryRows = null)
    {
        var genres = PreferenceBuilder.BuildGenrePreferenceVector(profile, seriesEpisodeCounts);
        var maximum = collaborative.Values.Where(v => double.IsFinite(v) && v > 0.0).DefaultIfEmpty(0.0).Max();
        var available = lookups.AvailableMetadata[profile.UserId];
        return (genres, collaborative, maximum, ContentScoring.ComputeAverageYear(profile),
            PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile),
            lookups.CachedPeopleLookup.Count == 0 && profile.PeopleProfile.Count == 0 ? new Dictionary<string, double>() : PreferenceBuilder.BuildPeoplePreferenceWeights(profile, lookups.CachedPeopleLookup, seriesEpisodeCounts),
            TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, [], lookups.ItemStudiosLookup, predictionTimeUtc),
            TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, lookups.ItemTagsLookup),
            available.Franchises ? PreferenceBuilder.BuildFranchisePreferenceVector(profile, predictionTimeUtc) : new Dictionary<string, double>(),
            available.Countries ? PreferenceBuilder.BuildProductionCountryPreferenceVector(profile, predictionTimeUtc, countryRows) : new Dictionary<string, double>(),
            available.InheritedTags ? PreferenceBuilder.BuildInheritedTagPreferenceSet(profile) : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            available.Writers ? PreferenceBuilder.BuildWriterPreferenceWeights(profile, predictionTimeUtc) : new Dictionary<string, double>());
    }

    private static Dictionary<Guid, CreditMetadataSnapshot> BuildCreditMetadataLookup(
        IReadOnlyList<RecommendationResult> previous, Collection<UserWatchProfile> profiles, LibraryItemMetadata? library)
    {
        var result = new Dictionary<Guid, CreditMetadataSnapshot>();
        void Add(Guid id, CreditMetadataSnapshot? snapshot)
        {
            if (snapshot is not null && (snapshot.SourceItemId == Guid.Empty || snapshot.SourceItemId == id))
            {
                result[id] = snapshot.WithFallback(result.GetValueOrDefault(id));
            }
        }

        foreach (var item in previous.SelectMany(row => row.Recommendations))
        {
            Add(item.ItemId, item.Credits ?? item.ScoringFeatures?.Credits);
        }

        foreach (var row in profiles.SelectMany(profile => profile.WatchedItems))
        {
            Add(row.ItemId, row.Credits);
            if (row.SeriesId is { } series)
            {
                Add(series, row.SeriesCredits);
            }
        }

        foreach (var (id, snapshot) in library?.Credits ?? new Dictionary<Guid, CreditMetadataSnapshot>())
        {
            Add(id, snapshot);
        }

        return result;
    }

    private static CreditMetadataSnapshot? CandidateCredits(Guid id, CreditMetadataSnapshot? captured, TrainingLookups lookups, DateTime cutoff)
    {
        var stored = captured is not null && (captured.SourceItemId == Guid.Empty || captured.SourceItemId == id) ? captured.At(cutoff) : null;
        return lookups.Credits.GetValueOrDefault(id)?.At(cutoff).WithFallback(stored) ?? stored;
    }

    private static (UserWatchProfile Profile, PerUserArtifacts Artifacts, WatchedContentSets Content) HistoricalInputs(
        TrainingContext ctx, Guid userId, DateTime beforeUtc, HashSet<Guid> excluded)
    {
        var predictionTime = DateTimeNormalization.ToUtc(beforeUtc);
        var history = ctx.HistoryCache.Get(userId, predictionTime, excluded);
        var profile = history.Profile;
        var scoped = HistoricalCollectionRows(ctx, userId, profile, excluded);
        var artifacts = BuildArtifacts(profile, CollaborativeFilter.BuildCollaborativeMap(profile.UserId, history.Collaborative), ctx.Lookups, ctx.SeriesEpisodeCounts, predictionTime, scoped);
        var credits = CreditPreferenceProfile.Create(profile, ctx.Lookups.Credits, predictionTime);
        var sets = BuildWatchedContentSets(profile, ctx.Lookups, credits, predictionTime);
        var boxes = BuildWatchedBoxSetCounts(scoped, ctx.Lookups.ItemBoxSetIdsLookup, ctx.Lookups.BoxSetProviderIds);

        return (profile, artifacts, new WatchedContentSets(sets.Genre, sets.People, sets.Studio, boxes)
        {
            ContentFit = ContentFitProfile.Create(profile, ctx.Lookups.ItemTagsLookup.GetValueOrDefault, sourceCache: ctx.Lookups.ContentSources, metadataCutoff: beforeUtc),
            CreditPreferences = credits,
            TypedPeopleLookup = ctx.Lookups.Credits.ToDictionary(pair => pair.Key, pair => credits.Keys(pair.Value.At(predictionTime))),
            CollectionContinuation = ctx.Lookups.Continuation.BuildSupport(profile)
        });
    }

    private static IEnumerable<WatchedItemInfo> HistoricalCollectionRows(TrainingContext ctx, Guid userId, UserWatchProfile profile, HashSet<Guid> excluded)
    {
        if (ctx.Lookups.ItemBoxSetIdsLookup.Count == 0 && !ctx.Lookups.AvailableMetadata[userId].Countries)
        {
            // No collection or country coordinate can consume these rows.
            // Do not rebuild canonical membership dictionaries for every organic prefix.
            return profile.WatchedItems;
        }

        var source = ctx.ProfileById[userId];
        var excludedTitles = CollectionMembershipResolver.EngagedTitles(source.WatchedItems.Where(row => excluded.Contains(row.ItemId)
            || (row.SeriesId is { } parent && excluded.Contains(parent)))).Select(title => title.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var recorded in ctx.PreviousResults.Where(result => result.UserId == userId).SelectMany(result => result.Recommendations).Where(item => excluded.Contains(item.ItemId)))
        {
            excludedTitles.Add(CollectionMembershipResolver.EngagedTitle(new WatchedItemInfo { ItemId = recorded.ItemId, ItemType = recorded.ItemType, TmdbId = recorded.TmdbId }).Key);
        }

        // The current physical-copy intersection precedes canonical title deduplication and typed target exclusion.
        var allowed = profile.WatchedItems.Where(row => source.AccessibleItemIds?.Contains(row.ItemId) != false
            && source.AccessibleItemIds?.Contains(CollectionMembershipResolver.EngagedTitle(row).ItemId) != false
            && ctx.Lookups.AllowedCollectionItems?.Contains(CollectionMembershipResolver.EngagedTitle(row).ItemId) != false).ToArray();
        var identities = CollectionMembershipResolver.EngagedTitles(allowed).ToDictionary(title => title.ItemId, title => title.Key);
        return allowed.Where(row => !identities.TryGetValue(CollectionMembershipResolver.EngagedTitle(row).ItemId, out var key) || !excludedTitles.Contains(key)).ToArray();
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
            var plan = BuildRecommendationChoicePlan(ctx, prevResult, userCtx);
            foreach (var choice in plan.Choices)
            {
                ctx.Choices[(prevResult.UserId, choice.Key, choice.Value.RecommendedAtUtc)] = choice.Value;
            }

            userCtx = userCtx with { FittingChoiceAlternatives = plan.FittingAlternatives };

            foreach (var rec in prevResult.Recommendations)
            {
                var featureContext = GetRecommendationFeatureContext(rec, prevResult, userCtx, ctx);

                ctx.Examples.Add(
                    BuildRecommendationFeedbackExample(rec, prevResult, featureContext, ctx.Lookups));
            }
        }
    }

    private static RecommendationChoiceBuilder.Plan BuildRecommendationChoicePlan(TrainingContext ctx, RecommendationResult result, Phase1UserContext userCtx)
    {
        TrainingExample Capture(WatchedItemInfo watched, RecommendedItem? recorded)
        {
            var item = recorded ?? ProjectHistoricalChoice(watched, result.GeneratedAt);
            var historical = GetRecommendationFeatureContext(item, result, userCtx, ctx);
            return BuildRecommendationFeedbackExample(item, result, historical, ctx.Lookups);
        }

        return RecommendationChoiceBuilder.Build(new(result, userCtx.UserProfile, ctx.Lookups.TmdbIds, ctx.ReferenceTime), Capture);
    }

    private static RecommendedItem ProjectHistoricalChoice(WatchedItemInfo row, DateTime predictionAt)
    {
        row = row.AtMetadataCutoff(DateTimeNormalization.ToUtc(predictionAt));
        var seriesId = row.SeriesId.GetValueOrDefault();
        return new RecommendedItem
        {
            ItemId = seriesId != Guid.Empty ? seriesId : row.ItemId,
            ItemType = seriesId != Guid.Empty ? SeriesItemType : row.ItemType,
            TmdbId = seriesId != Guid.Empty ? row.SeriesTmdbId : row.TmdbId,
            Genres = row.Genres,
            Tags = row.SeriesTags ?? row.Tags,
            Studios = row.SeriesStudios ?? row.Studios,
            PeopleNames = row.PeopleNames,
            PeopleWeights = row.PeopleWeights,
            Credits = CreditPreferenceProfile.Resolve(row),
            CommunityRating = row.CommunityRating,
            OfficialRating = row.OfficialRating,
            Year = row.Year,
            DateCreated = row.DateCreated ?? DateTime.UnixEpoch,
            AudioLanguages = row.AudioLanguages ?? [],
            SubtitleLanguages = row.SubtitleLanguages ?? [],
            TmdbCollectionId = row.TmdbCollectionId,
            TmdbCollectionName = row.TmdbCollectionName,
            ProductionCountries = row.ProductionCountries,
            InheritedTags = row.InheritedTags,
            SeriesStatus = row.SeriesStatus,
            EndDate = row.EndDate,
            WriterNames = row.WriterNames,
            TitleMetadata = row.TitleMetadata?.FetchedAtUtc <= predictionAt ? row.TitleMetadata : null
        };
    }

    private static void AttachRecommendationChoices(TrainingContext ctx)
    {
        foreach (var pair in ctx.Examples.Where(example => example.IsObserved && example.Label > 0.5)
            .Select(example => (Example: example, Choice: ctx.Choices.GetValueOrDefault((example.UserId, example.ObservationId, example.GeneratedAtUtc))))
            .Where(pair => pair.Choice is not null))
        {
            pair.Example.Choice = pair.Choice!.Copy();
        }

        var referenceTime = ctx.ReferenceTime;
        foreach (var (key, choice) in ctx.Choices)
        {
            if (ctx.Examples.Any(row => row.UserId == key.User && row.ObservationId == key.Observation && row.Choice?.RecommendedAtUtc == key.Prediction))
            {
                continue;
            }

            var observed = ctx.Examples.Where(row => row.IsObserved && row.UserId == key.User && row.ObservationId == key.Observation)
                .OrderByDescending(row => row.GeneratedAtUtc).FirstOrDefault();
            if (observed is null || observed.Label <= .5)
            {
                continue;
            }

            var view = observed.Copy();
            view.Features = choice.ChosenFeatures.Copy();
            view.GeneratedAtUtc = choice.RecommendedAtUtc;
            view.Choice = choice.Copy();
            RecommendationChoiceHistory.AddChoiceView(ctx.Examples, view, referenceTime);
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
        BuildWatchedContentSets(UserWatchProfile userProfile, TrainingLookups lookups, CreditPreferenceProfile credits, DateTime? producerAtUtc = null)
    {
        var watchedGenreSets = new List<HashSet<string>>();
        var watchedPeopleSets = new List<HashSet<string>>();
        var watchedStudioSets = new List<HashSet<string>>();
        var unique = new HashSet<CachedContentSets>();
        foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
        {
            var content = BuildRowContentSets(w, lookups, credits, producerAtUtc);

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

    private static CachedContentSets BuildRowContentSets(WatchedItemInfo row, TrainingLookups lookups, CreditPreferenceProfile credits, DateTime? cutoff)
    {
        var reusable = CanReuseUnqualifiedContent(row, lookups);
        if (reusable && lookups.ContentByRow.TryGetValue(row, out var cached))
        {
            return cached;
        }

        var genres = GenreNormalizer.Set(row.Genres);
        var people = credits.Keys(CreditPreferenceProfile.Resolve(row, lookups.Credits, cutoff));
        var studios = new HashSet<string>(ProducerEvidence.ForWatch(row, lookups.ItemStudiosLookup, cutoff) ?? [], StringComparer.Ordinal);

        var content = new CachedContentSets(genres, people, studios);
        if (lookups.SharedContent.TryGetValue(content, out var existing))
        {
            if (reusable && lookups.ContentByRow.Count < TrainingHistoryCache.MaxCachedRows)
            {
                lookups.ContentByRow[row] = existing;
            }

            return existing;
        }

        lookups.SharedContent.Add(content, content);
        if (reusable && lookups.ContentByRow.Count < TrainingHistoryCache.MaxCachedRows)
        {
            lookups.ContentByRow[row] = content;
        }

        return content;
    }

    private static bool CanReuseUnqualifiedContent(WatchedItemInfo row, TrainingLookups lookups)
    {
        var title = row.SeriesId.GetValueOrDefault() is var parent && parent != Guid.Empty ? parent : row.ItemId;
        return row.TitleMetadata is null && row.Credits is null && row.SeriesCredits is null
            && lookups.ItemStudiosLookup.GetValueOrDefault(title) is null
            && !lookups.Credits.ContainsKey(row.ItemId) && !lookups.Credits.ContainsKey(title);
    }

    /// <summary>
    ///     Builds the per-user watched-BoxSet count map by resolving each watched id through the global
    ///     BoxSet lookup. Shared by Phase 1 and Phase 3.
    /// </summary>
    private static Dictionary<Guid, int> BuildWatchedBoxSetCounts(
        IEnumerable<WatchedItemInfo> history,
        Dictionary<Guid, IReadOnlyList<Guid>> itemBoxSetIdsLookup,
        IReadOnlyDictionary<Guid, int>? providerIds = null)
        => CollectionMembershipResolver.WatchedCounts(history, itemBoxSetIdsLookup, providerIds);

    private static IReadOnlySet<Guid>? CollectionItemScope(LibraryItemMetadata? metadata)
    {
        if (metadata is not { } current)
        {
            return null;
        }

        return current.AllowedItemIds ?? current.Studios.Keys.Concat(current.Tags.Keys).Concat(current.BoxSetIds.Keys)
            .Concat(current.TmdbIds?.Keys ?? []).ToHashSet();
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
        var predictionTime = DateTimeNormalization.ToUtc(prevResult.GeneratedAt);
        var isSeries = string.Equals(rec.ItemType, SeriesItemType, StringComparison.OrdinalIgnoreCase);
        var outcome = ResolveRecommendationOutcome(rec.ItemId, isSeries, prevResult, userCtx);
        var observationId = TrainingExample.CreateTitleObservationId(prevResult.UserId, rec.ItemId, lookups.TmdbIds.GetValueOrDefault(rec.ItemId), isSeries);
        var isChoiceContrast = outcome.IsContrast && userCtx.FittingChoiceAlternatives?.Contains(observationId) == true;
        var sampleWeight = isChoiceContrast ? RecommendationChoiceBuilder.ContrastWeightBudget / userCtx.FittingChoiceAlternatives!.Count : 1.0;

        if (rec.ScoringFeatures is not null)
        {
            return new TrainingExample
            {
                Features = rec.ScoringFeatures.Copy(),
                Label = outcome.Label,
                GeneratedAtUtc = prevResult.GeneratedAt,
                UserId = prevResult.UserId,
                ObservationId = observationId,
                HasStableObservationId = true,
                IsObserved = outcome.IsObserved,
                IsContrastSample = isChoiceContrast,
                SampleWeight = sampleWeight
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
            ? ContentScoring.ComputeSeriesAffinity(isSeries, rec.ItemId, rec.Genres, userCtx.SeriesAffinity, contentSets.TypedPeopleLookup, excludeSeriesId: rec.ItemId)
            : 0.0;

        var peopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(rec.ItemId, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights)
            : 0.0;

        var candidateStudios = ProducerEvidence.Keys(lookups.ItemStudiosLookup.TryGetValue(rec.ItemId, out var producers) ? producers : rec.TitleMetadata, predictionTime) ?? [];
        var candidateTags = lookups.ItemTagsLookup.GetValueOrDefault(rec.ItemId) ?? rec.Tags;
        var candidateCollections = lookups.ItemBoxSetIdsLookup.GetValueOrDefault(rec.ItemId) ?? rec.BoxSetIds;
        var candidateCredits = CandidateCredits(rec.ItemId, rec.Credits, lookups, predictionTime);
        var studioMatch = candidateStudios.Count > 0 && candidateStudios.Any(preferredStudios.Contains);

        var tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(candidateTags, preferredTags);
        var recGenreSet = GenreNormalizer.Set(rec.Genres);

        double recencyScore;
        if (rec.PremiereDate.HasValue)
        {
            recencyScore = ContentScoring.ComputeRecencyScore(rec.PremiereDate.Value, predictionTime);
        }
        else if (rec.DateCreated.HasValue)
        {
            recencyScore = ContentScoring.ComputeRecencyScore(rec.DateCreated.Value, predictionTime);
        }
        else
        {
            recencyScore = 0.5;
        }

        var features = new CandidateFeatures
        {
            FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion,
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(rec.Genres, genrePreferences),
            HasGenreEvidence = recGenreSet.Count > 0 && genrePreferences.Count > 0,
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = recencyScore,
            YearProximityScore = ContentScoring.ComputeYearProximity(rec.Year, avgYear),
            GenreCount = GenreNormalizer.Set(rec.Genres).Count,
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
                isDay: true,
                predictionTimeUtc: predictionTime),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
                new WatchedItemInfo { ItemId = rec.ItemId, LastPlayedDate = prevResult.GeneratedAt },
                recGenreSet,
                userProfile,
                isDay: false,
                predictionTimeUtc: predictionTime),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, prevResult.GeneratedAt),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = rec.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(rec.DateCreated.Value, predictionTime)
                : ContentScoring.ComputeRecencyScore(default, predictionTime),
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                rec.Genres,
                contentSets.CreditPreferences.Keys(candidateCredits).ToArray(),
                candidateStudios,
                contentSets.WatchedGenreSets,
                contentSets.WatchedPeopleSets,
                contentSets.WatchedStudioSets),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(rec.AudioLanguages, userProfile),
            HasAudioLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(rec.AudioLanguages, userProfile.LanguageProfile),
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(candidateCollections, contentSets.WatchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(rec.SubtitleLanguages, userProfile),
            HasSubtitleLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(rec.SubtitleLanguages, userProfile.SubtitleLanguageProfile),
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(lookups.TmdbCollectionIds.GetValueOrDefault(rec.ItemId, rec.TmdbCollectionId), rec.TmdbCollectionName, preferredFranchises),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(rec.ProductionCountries, preferredCountries),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(rec.InheritedTags, preferredInheritedTags),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, rec.SeriesStatus, rec.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(rec.WriterNames, preferredWriterWeights),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(rec.PeopleNames, rec.PeopleWeights), preferredPeopleWeights),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(rec.Genres, candidateStudios, lookups.GenreStudioIdf)
        };

        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(rec.Genres, genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;
        contentSets.CreditPreferences.Apply(features, candidateCredits);

        contentSets.ContentFit.Apply(features, rec.Genres, candidateTags, rec.OfficialRating, genrePreferences, rec.TitleMetadata);
        features.CollectionContinuationSupport = contentSets.CollectionContinuation.TryGetValue(rec.ItemId, out var continuation) ? continuation : null;

        return new TrainingExample
        {
            Features = features,
            Label = outcome.Label,
            GeneratedAtUtc = prevResult.GeneratedAt,
            UserId = userProfile.UserId,
            ObservationId = TrainingExample.CreateTitleObservationId(userProfile.UserId, rec.ItemId, lookups.TmdbIds.GetValueOrDefault(rec.ItemId), isSeries),
            HasStableObservationId = true,
            IsObserved = outcome.IsObserved,
            IsContrastSample = isChoiceContrast,
            SampleWeight = sampleWeight
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

        var isSeries = string.Equals(w.ItemType, SeriesItemType, StringComparison.OrdinalIgnoreCase);

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
            history.Content.TypedPeopleLookup,
            artifacts.PeopleWeights,
            [],
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
            ctx.Lookups.TmdbIds.GetValueOrDefault(seriesId),
            ComputeCollectionProgressionBoostWithCounts(ctx.Lookups.ItemBoxSetIdsLookup.GetValueOrDefault(seriesId) ?? [], history.Content.WatchedBoxSetCounts),
            new TrainingFeatureComputer.SeriesMetadata(
                history.Content.ContentFit,
                ctx.Lookups.ItemStudiosLookup,
                history.Content.CreditPreferences,
                CandidateCredits(seriesId, episodes.Select(episode => episode.SeriesCredits).FirstOrDefault(snapshot => snapshot is not null), ctx.Lookups, DateTimeNormalization.ToUtc(before)),
                new TrainingFeatureComputer.NeighborInputs(history.Content.WatchedGenreSets, history.Content.WatchedPeopleSets, history.Content.WatchedStudioSets)));
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
        var predictionTime = DateTimeNormalization.ToUtc(w.LastPlayedDate ?? ctx.OrganicFallbackTimestamp);
        w = w.AtMetadataCutoff(predictionTime);
        var history = HistoricalInputs(ctx, userCtx.UserProfile.UserId, predictionTime, excluded);
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
        var organicStudios = ProducerEvidence.ForWatch(w, lookups.ItemStudiosLookup, predictionTime);

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
        var wGenreSet = GenreNormalizer.Set(wGenres);

        var features = new CandidateFeatures
        {
            FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion,
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(wGenres, genrePreferences),
            HasGenreEvidence = GenreNormalizer.Set(wGenres).Count > 0 && genrePreferences.Count > 0,
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = w.Year is { } recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1, 0, 0, 0, DateTimeKind.Utc), predictionTime)
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
            GenreCount = GenreNormalizer.Set(wGenres).Count,
            IsSeries = isSeries,
            UserRatingScore = userRatingScore2,
            HasUserInteraction = familiarity2 > 0.0,
            CompletionRatio = genreAvgCompletion2,
            IsAbandoned = genreAbandonRate2,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesAffinity = ctx.SeriesEpisodeCounts is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, w.ItemId, wGenres, ContentScoring.BuildSeriesAffinityContext(userProfile, ctx.SeriesEpisodeCounts), history.Content.TypedPeopleLookup, excludeSeriesId: w.ItemId)
                : 0.0,
            CollectionProgressionBoost = lookups.ItemBoxSetIdsLookup.TryGetValue(w.ItemId, out var orgBoxSetIds2)
                ? ComputeCollectionProgressionBoostWithCounts(orgBoxSetIds2, watchedBoxSetCountsOrganic)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: true, predictionTimeUtc: predictionTime),
            HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenreSet, userProfile, isDay: false, predictionTimeUtc: predictionTime),
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, w.LastPlayedDate ?? organicFallbackTimestamp),
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = w.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value, predictionTime)
                : ContentScoring.ComputeRecencyScore(default, predictionTime),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(w.AudioLanguages ?? [], userProfile),
            HasAudioLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(w.AudioLanguages, userProfile.LanguageProfile, w.HasAudioLanguageMetadata),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(w.SubtitleLanguages ?? [], userProfile),
            HasSubtitleLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(w.SubtitleLanguages, userProfile.SubtitleLanguageProfile, w.HasSubtitleLanguageMetadata),
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                wGenres, w.PeopleNames, organicStudios ?? [], history.Content.WatchedGenreSets, history.Content.WatchedPeopleSets, history.Content.WatchedStudioSets),
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(lookups.TmdbCollectionIds.GetValueOrDefault(w.ItemId, w.TmdbCollectionId), w.TmdbCollectionName, preferredFranchisesOrganic),
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
        var organicCredits = CandidateCredits(w.ItemId, w.Credits, lookups, predictionTime);
        history.Content.CreditPreferences.Apply(features, organicCredits);
        features.ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            wGenres, history.Content.CreditPreferences.Keys(organicCredits).ToArray(), organicStudios ?? [], history.Content.WatchedGenreSets, history.Content.WatchedPeopleSets, history.Content.WatchedStudioSets);

        var metadataAtPrediction = w.TitleMetadata is { } metadata && metadata.FetchedAtUtc <= predictionTime ? metadata : null;
        history.Content.ContentFit.Apply(features, wGenres, organicTags ?? w.Tags, w.OfficialRating, genrePreferences, metadataAtPrediction);
        features.CollectionContinuationSupport = history.Content.CollectionContinuation.TryGetValue(w.ItemId, out var continuation) ? continuation : null;

        // Organic watches are positives. Favorites without playback get a fixed positive label.
        var label = ComputeOrganicStandaloneLabel(w, completionRatio);

        return new TrainingExample
        {
            Features = features,
            Label = label,
            GeneratedAtUtc = w.LastPlayedDate ?? organicFallbackTimestamp,
            SampleWeight = 0.7,
            UserId = userProfile.UserId,
            ObservationId = TrainingExample.CreateTitleObservationId(
                userProfile.UserId,
                w.ItemId,
                OrganicProviderId(w, lookups.TmdbIds.GetValueOrDefault(w.ItemId)),
                isSeries),
            HasStableObservationId = true,
            IsObserved = HasReliableOutcome(w, isSeries)
        };
    }

    private static int OrganicProviderId(WatchedItemInfo item, int mappedId)
        => string.Equals(item.ItemType, "Episode", StringComparison.OrdinalIgnoreCase) ? 0 : mappedId;

    private static double ComputeOrganicStandaloneLabel(WatchedItemInfo item, double completionRatio)
        => item.UserRating is > 0 && double.IsFinite(item.UserRating.Value)
            ? Math.Clamp(item.UserRating.Value / 10.0, 0.0, 1.0)
            : item switch
            {
                { Played: false, PlaybackPositionTicks: <= 0, IsFavorite: true } => 0.65,
                _ => ContentScoring.ComputeEngagementLabel(completionRatio)
            };

    /// <summary>
    ///     Captures bounded candidate comparisons before chronological qualification; these are never independent observed rejections.
    /// </summary>
    private static void EmitRandomNegativeExamples(TrainingContext ctx)
    {
        // Phase 1 retains observed outcomes and unobserved opportunities; Phase 2 preserves independent watches.
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

                ProcessUserRandomNegatives(userProfile, allRecommendedItems, predictionTimes, ctx);
            }
        }
    }

    /// <summary>
    ///     Captures optional candidate comparisons for a user with an actual chronological choice; final qualification determines fitting eligibility.
    /// </summary>
    private static void ProcessUserRandomNegatives(
        UserWatchProfile userProfile,
        List<RecommendedItem> allRecommendedItems,
        Dictionary<Guid, DateTime> predictionTimes,
        TrainingContext ctx)
    {
        if (!ctx.Choices.Keys.Any(choice => choice.User == userProfile.UserId))
        {
            // Publication and unrelated users' watches cannot authorize fitting
            // negatives. Avoid even reconstructing their historical feature vectors.
            return;
        }

        var ownResult = ctx.PreviousResults.Where(result => result.UserId == userProfile.UserId).MaxBy(result => result.GeneratedAt);
        if (ownResult?.TrainingContrasts is not null)
        {
            EmitRecordedContrasts(ownResult, ctx);
            return;
        }

        if (ownResult?.Recommendations.Any(item => item.ScoringFeatures is not null) == true)
        {
            // Old snapshot-aware caches lack contrast predictions. Phase 1's recorded unwatched
            // recommendations already provide contrasts; do not add mismatched full-history reconstructions.
            return;
        }

        // Per-user deterministic RNG: same cache + same user => same negatives.
        // Keeps training reproducible without coupling across users.
        var rngNeg = new Random(Engine.ComputeStableSeed(userProfile.UserId, ctx.PreviousResults.Count));

        if (!ctx.ProfileLookup.TryGetValue(userProfile.UserId, out var userWatchedIds))
        {
            return;
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
            if (userProfile.AccessibleItemIds?.Contains(rec.ItemId) == false || userWatchedIds.Contains(rec.ItemId)
                || userRecommendedIds.Contains(rec.ItemId)
                || (userWatchedSeriesIds?.Contains(rec.ItemId) ?? false))
            {
                continue;
            }

            candidateNegatives.Add(rec);
        }

        var sampleCount = Math.Min(EngineConstants.RandomNegativeSamplesPerUser, candidateNegatives.Count);
        for (var s = 0; s < sampleCount; s++)
        {
            // Pick without replacement.
            var swapIdx = rngNeg.Next(s, candidateNegatives.Count);
            (candidateNegatives[s], candidateNegatives[swapIdx]) =
                (candidateNegatives[swapIdx], candidateNegatives[s]);

            var neg = candidateNegatives[s];
            ctx.Examples.Add(BuildHistoricalNegativeExample(neg, userProfile.UserId, predictionTimes[neg.ItemId], ctx));
        }
    }

    private static void EmitRecordedContrasts(RecommendationResult result, TrainingContext ctx)
    {
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
                ObservationId = TrainingExample.CreateTitleObservationId(result.UserId, item.ItemId, ctx.Lookups.TmdbIds.GetValueOrDefault(item.ItemId), string.Equals(item.ItemType, SeriesItemType, StringComparison.OrdinalIgnoreCase)),
                HasStableObservationId = true,
                IsObserved = false,
                IsContrastSample = true
            });
        }
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
        var isSeries = string.Equals(neg.ItemType, SeriesItemType, StringComparison.OrdinalIgnoreCase);

        var negPeopleSimilarity = lookups.CachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(negPeople, preferredPeopleWeightsNeg)
            : 0.0;

        var negGenres = neg.Genres ?? Array.Empty<string>();
        var negStudios = ProducerEvidence.Keys(lookups.ItemStudiosLookup.TryGetValue(neg.ItemId, out var negativeProducers) ? negativeProducers : neg.TitleMetadata, predictionTime) ?? [];
        var negTags = lookups.ItemTagsLookup.GetValueOrDefault(neg.ItemId) ?? neg.Tags;
        var negStudioMatch = negStudios.Count > 0
                             && negStudios.Any(preferredStudiosNeg.Contains);
        var negTagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(negTags, preferredTagsNeg);

        double negRecencyScore;
        if (neg.PremiereDate.HasValue)
        {
            negRecencyScore = ContentScoring.ComputeRecencyScore(neg.PremiereDate.Value, predictionTime);
        }
        else if (neg.DateCreated.HasValue)
        {
            negRecencyScore = ContentScoring.ComputeRecencyScore(neg.DateCreated.Value, predictionTime);
        }
        else
        {
            negRecencyScore = 0.5;
        }

        var (familiarityNeg, genreAvgCompletionNeg, genreAbandonRateNeg) = ContentScoring.ComputeGenreEngagement(negGenres, scoringContexts.GenreEngagement);
        var userRatingScoreNeg = ContentScoring.ComputeGenreRatingScore(negGenres, scoringContexts.GenreEngagement);
        var features = new CandidateFeatures
        {
            FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion,
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(negGenres, genrePreferences),
            HasGenreEvidence = GenreNormalizer.Set(negGenres).Count > 0 && genrePreferences.Count > 0,
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
            RecencyScore = negRecencyScore,
            YearProximityScore = ContentScoring.ComputeYearProximity(neg.Year, avgYear),
            GenreCount = GenreNormalizer.Set(negGenres).Count,
            IsSeries = isSeries,
            UserRatingScore = userRatingScoreNeg,
            HasUserInteraction = familiarityNeg > 0.0,
            CompletionRatio = genreAvgCompletionNeg,
            IsAbandoned = genreAbandonRateNeg,
            PeopleSimilarity = negPeopleSimilarity,
            StudioMatch = negStudioMatch,
            SeriesAffinity = scoringContexts.SeriesAffinity is not null
                ? ContentScoring.ComputeSeriesAffinity(isSeries, neg.ItemId, negGenres, scoringContexts.SeriesAffinity, contentSets.TypedPeopleLookup)
                : 0.0,
            PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
            DayOfWeekAffinity = 0.5,
            HourOfDayAffinity = 0.5,
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile, predictionTime),
            TagSimilarity = negTagSimilarity,
            LibraryAddedRecency = neg.DateCreated.HasValue
                ? ContentScoring.ComputeRecencyScore(neg.DateCreated.Value, predictionTime)
                : ContentScoring.ComputeRecencyScore(default, predictionTime),
            ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
                negGenres,
                neg.PeopleNames,
                negStudios,
                contentSets.WatchedGenreSets,
                contentSets.WatchedPeopleSets,
                contentSets.WatchedStudioSets),
            LanguageAffinity = TrainingFeatureComputer.ComputeLanguageAffinityFromCache(neg.AudioLanguages, userProfile),
            HasAudioLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(neg.AudioLanguages, userProfile.LanguageProfile),
            CollectionProgressionBoost = ComputeCollectionProgressionBoostWithCounts(lookups.ItemBoxSetIdsLookup.GetValueOrDefault(neg.ItemId) ?? neg.BoxSetIds, contentSets.WatchedBoxSetCounts),
            SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(neg.SubtitleLanguages, userProfile),
            HasSubtitleLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(neg.SubtitleLanguages, userProfile.SubtitleLanguageProfile),
            FranchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(lookups.TmdbCollectionIds.GetValueOrDefault(neg.ItemId, neg.TmdbCollectionId), neg.TmdbCollectionName, preferredFranchisesNeg),
            ProductionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(neg.ProductionCountries, preferredCountriesNeg),
            InheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(neg.InheritedTags, preferredInheritedTagsNeg),
            SeriesCompletability = EngineConstants.ComputeSeriesCompletability(isSeries, neg.SeriesStatus, neg.EndDate.HasValue),
            WriterAffinity = SimilarityComputer.ComputeWriterAffinity(neg.WriterNames, preferredWriterWeightsNeg),
            BillingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
                TrainingFeatureComputer.BuildBillingMapFromCache(neg.PeopleNames, neg.PeopleWeights), preferredPeopleWeightsNeg),
            GenreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(neg.Genres, negStudios, lookups.GenreStudioIdf)
        };

        var (negUnderexp, negDomRatio, negAffGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(negGenres, genreExposureNeg);
        features.GenreUnderexposure = negUnderexp;
        features.GenreDominanceRatio = negDomRatio;
        features.GenreAffinityGap = negAffGap;
        var negativeCredits = CandidateCredits(neg.ItemId, neg.Credits, lookups, predictionTime);
        contentSets.CreditPreferences.Apply(features, negativeCredits);
        features.ContentNearestNeighborScore = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(
            negGenres, contentSets.CreditPreferences.Keys(negativeCredits).ToArray(), negStudios, contentSets.WatchedGenreSets, contentSets.WatchedPeopleSets, contentSets.WatchedStudioSets);

        contentSets.ContentFit.Apply(features, negGenres, negTags, neg.OfficialRating, genrePreferences, neg.TitleMetadata);
        features.CollectionContinuationSupport = contentSets.CollectionContinuation.TryGetValue(neg.ItemId, out var continuation) ? continuation : null;

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
        Dictionary<Guid, TitleMetadataSnapshot?> ItemStudiosLookup,
        Dictionary<Guid, IReadOnlyList<string>> ItemTagsLookup,
        Dictionary<Guid, IReadOnlyList<Guid>> ItemBoxSetIdsLookup,
        IReadOnlyDictionary<string, double>? GenreStudioIdf,
        Dictionary<CachedContentSets, CachedContentSets> SharedContent,
        Dictionary<Guid, AvailablePreferenceMetadata> AvailableMetadata,
        Dictionary<Guid, int> TmdbIds,
        Dictionary<Guid, int> TmdbCollectionIds)
    {
        /// <summary>Gets per-cycle memoized string/identity transforms; historical preference aggregates are always rebuilt.</summary>
        public ContentFitProfile.SourceCache ContentSources { get; init; } = new();

        /// <summary>Gets already observed credits, independent of recommendation publication.</summary>
        public Dictionary<Guid, CreditMetadataSnapshot> Credits { get; init; } = [];

        /// <summary>Gets bounded build-owned transforms only for rows without time-varying credit or producer metadata.</summary>
        public Dictionary<WatchedItemInfo, CachedContentSets> ContentByRow { get; } = [];

        /// <summary>Gets shared, immutable membership/order metadata; user completion is always evaluated historically.</summary>
        public CollectionContinuationCatalog Continuation { get; init; } = CollectionContinuationCatalog.Empty;

        /// <summary>Gets captured provider identities for BoxSet aliases, without inferring them from watched-title collection fields.</summary>
        public IReadOnlyDictionary<Guid, int>? BoxSetProviderIds { get; init; }

        /// <summary>Gets the current permitted physical library population for collection/country evidence.</summary>
        public IReadOnlySet<Guid>? AllowedCollectionItems { get; init; }
    }

    /// <summary>
    ///     Groups per user watched content sets and BoxSet counts.
    /// </summary>
    private readonly record struct WatchedContentSets(
        List<HashSet<string>> WatchedGenreSets,
        List<HashSet<string>> WatchedPeopleSets,
        List<HashSet<string>> WatchedStudioSets,
        Dictionary<Guid, int> WatchedBoxSetCounts)
    {
        /// <summary>Gets semantic evidence from this historical, self-excluded user view.</summary>
        public ContentFitProfile ContentFit { get; init; } = ContentFitProfile.Empty;

        /// <summary>Gets this historical self-excluded user's person identity context.</summary>
        public CreditPreferenceProfile CreditPreferences { get; init; } = CreditPreferenceProfile.Create(new UserWatchProfile());

        /// <summary>Gets cutoff-qualified person keys for the existing series-affinity formula.</summary>
        public Dictionary<Guid, HashSet<string>> TypedPeopleLookup { get; init; } = [];

        /// <summary>Gets next collection titles from this self-excluded historical user's completed progress only.</summary>
        public Dictionary<Guid, double> CollectionContinuation { get; init; } = [];
    }

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
        /// <summary>Gets the common choice-admission and normalization clock.</summary>
        public DateTime ReferenceTime { get; init; }

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

        /// <summary>Gets captured revealed choices keyed by the same stable user/title identities as outcomes.</summary>
        public Dictionary<(Guid User, Guid Observation, DateTime Prediction), RecommendationChoiceEvidence> Choices { get; init; }
    }

    private readonly record struct TrainingSources(IReadOnlyList<RecommendationResult> Previous, Collection<UserWatchProfile> Profiles, IReadOnlyList<DiscoveryFeedbackResult>? Discovery);

    private readonly record struct TrainingFeatureInputs(IReadOnlyDictionary<Guid, int>? EpisodeCounts, IReadOnlyDictionary<string, double>? GenreIdf, LibraryItemMetadata? Metadata, IReadOnlyList<double>? FeatureMeans);

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

        /// <summary>Gets alternatives tied only to genuine fitting-partition choices, never held-out choices or publication alone.</summary>
        public HashSet<Guid>? FittingChoiceAlternatives { get; init; }
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
