using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Handles training of scoring strategies using implicit feedback
///     from previous recommendation results and current watch data.
/// </summary>
internal sealed class TrainingService
{
    /// <summary>
    ///     Non-blocking gate to prevent concurrent Train() invocations.
    ///     The scheduled task serializes calls, but this guard ensures correctness
    ///     if Train() is ever invoked from multiple paths simultaneously.
    /// </summary>
    private static readonly SemaphoreSlim TrainGate = new(1, 1);

    private readonly IPluginLogService _pluginLog;
    private readonly ILogger _logger;
    private readonly IWatchHistoryService _watchHistoryService;

    internal TrainingService(
        IWatchHistoryService watchHistoryService,
        IPluginLogService pluginLog,
        ILogger logger)
    {
        _watchHistoryService = watchHistoryService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Trains the active scoring strategy using implicit feedback from previous recommendations.
    ///     Compares previously recommended items against current watch data.
    /// </summary>
    /// <param name="strategy">The scoring strategy to train.</param>
    /// <param name="previousResults">The recommendation results from the previous run.</param>
    /// <param name="incremental">When true, subsample older examples for efficiency.</param>
    /// <param name="cancellationToken">Token to cancel the training operation.</param>
    /// <returns>True if training was performed, false if skipped.</returns>
    internal bool Train(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        bool incremental = false,
        CancellationToken cancellationToken = default)
    {
        if (previousResults.Count == 0)
        {
            _pluginLog.LogInfo("Recommendations", "Training skipped — no previous recommendations available.", _logger);
            return false;
        }

        // Non-blocking guard: skip if another training run is already in progress.
        if (!TrainGate.Wait(0, CancellationToken.None))
        {
            _pluginLog.LogInfo("Recommendations", "Training skipped — another training run is already in progress.", _logger);
            return false;
        }

        try
        {
            return TrainCore(strategy, previousResults, incremental, cancellationToken);
        }
        finally
        {
            TrainGate.Release();
        }
    }

    /// <summary>
    ///     Core training logic, called under the <see cref="TrainGate"/> semaphore.
    /// </summary>
    private bool TrainCore(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        bool incremental,
        CancellationToken cancellationToken)
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();

        // Include both played AND favorited items as positive interactions.
        // A favorited-but-not-played recommended item signals explicit interest
        // and should not be labeled as exposure/abandonment.
        var profileLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            profileLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems.Where(w => w.Played || w.IsFavorite || w.PlayCount > 0 || w.PlaybackPositionTicks > 0).Select(w => w.ItemId));
        }

        var seriesLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            var seriesIds = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => (w.Played || w.IsFavorite || w.PlayCount > 0 || w.PlaybackPositionTicks > 0) && w.SeriesId.HasValue)
                    .Select(w => w.SeriesId!.Value));

            // Also include series-level favorites (user favorited the series itself, not individual episodes)
            foreach (var favSeriesId in profile.FavoriteSeriesIds)
            {
                seriesIds.Add(favSeriesId);
            }

            seriesLookup[profile.UserId] = seriesIds;
        }

        // Pre-compute collaborative data for all users (needed for full feature vectors)
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        // Build a people lookup from cached recommendation data (PeopleNames stored on RecommendedItem).
        // This allows computing PeopleSimilarity during training without re-querying the library.
        var cachedPeopleLookup = new Dictionary<Guid, HashSet<string>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                if (rec.PeopleNames.Count > 0 && !cachedPeopleLookup.ContainsKey(rec.ItemId))
                {
                    cachedPeopleLookup[rec.ItemId] = new HashSet<string>(rec.PeopleNames, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        // Pre-compute itemId ? studios and itemId ? tags lookups ONCE from all previous results.
        // This avoids O(users × results × recommendations) rescanning in BuildStudioPreferenceSetFromCache
        // and BuildTagPreferenceSetFromCache — each user's preference set is now O(watchedItems) instead.
        var itemStudiosLookup = new Dictionary<Guid, IReadOnlyList<string>>();
        var itemTagsLookup = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                if (!itemStudiosLookup.ContainsKey(rec.ItemId) && rec.Studios.Count > 0)
                {
                    itemStudiosLookup[rec.ItemId] = rec.Studios;
                }

                if (!itemTagsLookup.ContainsKey(rec.ItemId) && rec.Tags.Count > 0)
                {
                    itemTagsLookup[rec.ItemId] = rec.Tags;
                }
            }
        }

        var examples = new List<TrainingExample>();

        // Pre-compute per-user artifacts once and cache them. These are reused across
        // Phase 1 (recommendation feedback) and Phase 2 (organic examples), avoiding
        // redundant BuildCollaborativeMap / BuildGenrePreferenceVector calls for the same user.
        var perUserCache = new Dictionary<Guid, (
            Dictionary<string, double> GenrePreferences,
            Dictionary<Guid, double> CoOccurrence,
            double CollaborativeMax,
            double AvgYear,
            PreferenceBuilder.GenreExposureAnalysis GenreExposure)>();

        // Build a lookup for O(1) profile access by user ID (avoids O(N) FirstOrDefault per result)
        var profileById = new Dictionary<Guid, UserWatchProfile>(allProfiles.Count);

        foreach (var profile in allProfiles)
        {
            var gp = PreferenceBuilder.BuildGenrePreferenceVector(profile);
            var co = CollaborativeFilter.BuildCollaborativeMap(profile, allProfiles, precomputedUserSets);
            var cm = co.Count > 0 ? co.Values.Max() : 0;
            var ay = ContentScoring.ComputeAverageYear(profile);
            var ge = PreferenceBuilder.BuildGenreExposureAnalysis(gp, profile);
            perUserCache[profile.UserId] = (gp, co, cm, ay, ge);
            profileById[profile.UserId] = profile;
        }

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

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposure) = perUserCache[userProfile.UserId];

            // Build preferred people/studios/tags from the user's watch profile using cached data.
            // This mirrors what Engine.GenerateForUser() does with live BaseItem data.
            var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
            var preferredStudios = BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
            var preferredTags = BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

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

            foreach (var rec in prevResult.Recommendations)
            {
                var wasWatched = watchedIds.Contains(rec.ItemId)
                    || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);

                watchedItemLookup.TryGetValue(rec.ItemId, out var watchedItemForRec);

                var isSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

                // Compute user-specific signals matching Engine.ScoreCandidate() logic
                double userRatingScore;
                double completionRatio;
                bool hasUserInteraction;

                if (isSeries && seriesEpisodeLookup.TryGetValue(rec.ItemId, out var episodesForScoring))
                {
                    // For series, watchedItemLookup is keyed by episode IDs so rec.ItemId (series ID)
                    // usually misses. Use the most-recently-watched episode so temporal features get real timestamps.
                    watchedItemForRec = episodesForScoring
                        .OrderByDescending(e => e.LastPlayedDate)
                        .FirstOrDefault();

                    hasUserInteraction = true;
                    var ratedEpisodes = episodesForScoring.Where(e => e.UserRating is > 0).ToList();
                    userRatingScore = ratedEpisodes.Count > 0
                        ? Math.Clamp(ratedEpisodes.Average(e => e.UserRating!.Value) / 10.0, 0.0, 1.0)
                        : 0.5;
                    // Average per-episode completion ratios
                    completionRatio = episodesForScoring.Count > 0
                        ? Math.Clamp(
                            episodesForScoring.Average(e => ContentScoring.ComputeCompletionRatio(e)),
                            0.0,
                            1.0)
                        : 0.5;
                }
                else if (isSeries && wasWatched && watchedItemForRec is null)
                {
                    // Series-level favorite without watched episodes: the user favorited
                    // the series itself but hasn't played any episodes yet.
                    // Treat as explicit positive interaction with favorite-appropriate defaults.
                    hasUserInteraction = true;
                    userRatingScore = 0.5;
                    completionRatio = 0.0;
                }
                else
                {
                    hasUserInteraction = watchedItemForRec is not null;
                    userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItemForRec);
                    completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItemForRec) : 0.5;
                }

                // Compute collaborative score for this specific item
                var collabScore = ContentScoring.ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax);

                // Popularity proxy matching Engine.ScoreCandidate() logic
                var ratingScore = ContentScoring.NormalizeRating(rec.CommunityRating);
                var popularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3;

                // Series progression boost
                var seriesProgressionBoost = 0.0;
                if (isSeries && seriesEpisodeLookup.TryGetValue(rec.ItemId, out var progressionEps))
                {
                    var playedEps = progressionEps.Count(e => e.Played);
                    if (progressionEps.Count > 0)
                    {
                        var ratio = (double)playedEps / progressionEps.Count;
                        seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
                    }
                }

                // Compute PeopleSimilarity from cached data (matches Engine.ScoreCandidate() logic)
                var peopleSimilarity = cachedPeopleLookup.TryGetValue(rec.ItemId, out var candidatePeople)
                    ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeople)
                    : 0.0;

                // Compute StudioMatch from cached data (matches Engine.ScoreCandidate() logic)
                var studioMatch = rec.Studios.Count > 0
                    && rec.Studios.Any(s => preferredStudios.Contains(s));

                // Compute TagSimilarity from cached data (matches Engine.ScoreCandidate() logic)
                var tagSimilarity = ComputeTagSimilarityFromCache(rec.Tags, preferredTags);

                // Build the COMPLETE feature vector matching Engine.ScoreCandidate() logic
                var features = new CandidateFeatures
                {
                    GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(rec.Genres ?? [], genrePreferences),
                    CollaborativeScore = collabScore,
                    RatingScore = ratingScore,
                    RecencyScore = rec.PremiereDate.HasValue
                        ? ContentScoring.ComputeRecencyScore(rec.PremiereDate.Value)
                        : 0.5,
                    YearProximityScore = ContentScoring.ComputeYearProximity(rec.Year, avgYear),
                    GenreCount = rec.Genres?.Count ?? 0,
                    IsSeries = isSeries,
                    UserRatingScore = userRatingScore,
                    HasUserInteraction = hasUserInteraction,
                    CompletionRatio = completionRatio,
                    PeopleSimilarity = peopleSimilarity,
                    StudioMatch = studioMatch,
                    SeriesProgressionBoost = seriesProgressionBoost,
                    PopularityScore = popularityScore,
                    DayOfWeekAffinity = ComputeTrainingTemporalAffinity(watchedItemForRec, rec.Genres, userProfile, isDay: true),
                    HourOfDayAffinity = ComputeTrainingTemporalAffinity(watchedItemForRec, rec.Genres, userProfile, isDay: false),
                    IsWeekend = watchedItemForRec?.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                    TagSimilarity = tagSimilarity,
                    LibraryAddedRecency = 0.5
                };

                // Genre exposure features: compute from cached per-user analysis
                var (underexposure, dominanceRatio, affinityGap) =
                    PreferenceBuilder.ComputeGenreExposureFeatures(rec.Genres ?? [], genreExposure);
                features.GenreUnderexposure = underexposure;
                features.GenreDominanceRatio = dominanceRatio;
                features.GenreAffinityGap = affinityGap;

                double label;
                if (wasWatched)
                {
                    // Determine base label based on interaction type:
                    // 1. Favorite-only (no playback): explicit interest signal → 0.65
                    // 2. Abandoned (started but stopped early): strong negative signal → 0.0
                    // 3. Normal watch: engagement-proportional label (0.5–0.85)
                    double baseLabel;
                    if (watchedItemForRec is { IsFavorite: true, Played: false })
                    {
                        baseLabel = 0.65; // Favorite-only: explicit interest without playback
                    }
                    else if (watchedItemForRec is null && isSeries)
                    {
                        baseLabel = 0.65; // Series-level favorite without episode data
                    }
                    else if (features.CompletionRatio > 0
                             && features.CompletionRatio < EngineConstants.AbandonedCompletionThreshold)
                    {
                        // User started the item but abandoned it early — this is a stronger
                        // negative signal than "never seen" (exposure). Active rejection > passive ignore.
                        baseLabel = EngineConstants.AbandonedLabel;
                    }
                    else
                    {
                        baseLabel = ContentScoring.ComputeEngagementLabel(features.CompletionRatio);
                    }

                    // Watched shortly after recommendation — boost label (but not abandoned items)
                    label = baseLabel > EngineConstants.AbandonedLabel
                        && watchedItemForRec?.LastPlayedDate is not null
                        && (watchedItemForRec.LastPlayedDate.Value - prevResult.GeneratedAt).TotalDays
                            <= EngineConstants.RecommendationInfluenceWindowDays
                        && watchedItemForRec.LastPlayedDate.Value >= prevResult.GeneratedAt
                        ? Math.Max(baseLabel, EngineConstants.RecommendationInfluencedLabel)
                        : baseLabel;
                }
                else if (features.CompletionRatio > 0 && features.CompletionRatio < EngineConstants.AbandonedCompletionThreshold)
                {
                    label = EngineConstants.AbandonedLabel;
                }
                else
                {
                    label = EngineConstants.ExposureLabel;
                }

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = prevResult.GeneratedAt
                });
            }
        }

        // === Phase 2: Add organic watch examples (watched-but-never-recommended items) ===
        // Items the user found and watched on their own provide strong positive signal
        // that the recommendation-only approach misses. This reduces training bias.
        //
        // Build per-user recommended item sets so that an item recommended to user A
        // does not suppress user B's organic discovery of the same item.
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

        // Stable timestamp anchor for organic items without LastPlayedDate.
        // Using the earliest recommendation GeneratedAt provides a deterministic value
        // that doesn't drift across runs (unlike DateTime.UtcNow.AddDays(-90)).
        var organicFallbackTimestamp = previousResults.Min(r => r.GeneratedAt);

        var organicCount = 0;
        foreach (var userProfile in allProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureOrganic) = perUserCache[userProfile.UserId];

            // Resolve the per-user recommended set; users with no previous results get an empty set
            if (!recommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Build per-user preference sets for organic feature computation (mirrors Phase 1).
            var preferredPeopleOrganic = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
            var preferredStudiosOrganic = BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
            var preferredTagsOrganic = BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

            // Build series episode lookup for series progression boost
            var seriesEpisodeLookupOrganic = new Dictionary<Guid, List<WatchedItemInfo>>();
            foreach (var ep in userProfile.WatchedItems)
            {
                if (!ep.SeriesId.HasValue)
                {
                    continue;
                }

                if (!seriesEpisodeLookupOrganic.TryGetValue(ep.SeriesId.Value, out var epList))
                {
                    epList = [];
                    seriesEpisodeLookupOrganic[ep.SeriesId.Value] = epList;
                }

                epList.Add(ep);
            }

            // Pre-compute which series have organic episode rows available.
            // This prevents standalone series-type rows from winning the aggregatedSeriesIds
            // race when they appear before episode rows in the iteration. If episode data
            // exists, the episode-based aggregation path should always be preferred because
            // it produces richer training signals (per-episode completion, temporal features).
            var seriesWithOrgEpisodes = new HashSet<Guid>();
            foreach (var candidate in userProfile.WatchedItems.Where(candidate =>
                         candidate.SeriesId.HasValue
                         && (candidate.Played || candidate.IsFavorite || candidate.PlayCount > 0 || candidate.PlaybackPositionTicks > 0)
                         && !recommendedItemIds.Contains(candidate.ItemId)
                         && !recommendedItemIds.Contains(candidate.SeriesId.Value)))
            {
                seriesWithOrgEpisodes.Add(candidate.SeriesId!.Value);
            }

            // === Series aggregation: collapse episodes into one example per series ===
            // Without aggregation, a series with 50 episodes produces 50 training examples,
            // massively skewing the dataset toward that series. Instead, group episodes by
            // SeriesId and emit a single aggregated TrainingExample per series. Standalone
            // items (movies, series-level favorites without SeriesId) are emitted 1:1 as before.
            var aggregatedSeriesIds = new HashSet<Guid>();

            foreach (var w in userProfile.WatchedItems)
            {
                // Include played OR favorited items that were NEVER recommended (organic discoveries).
                var hasAnyInteraction = w.Played || w.IsFavorite || w.PlayCount > 0 || w.PlaybackPositionTicks > 0;
                if (!hasAnyInteraction || recommendedItemIds.Contains(w.ItemId))
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
                        AddAggregatedSeriesExample(
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
                            preferredPeopleOrganic,
                            itemStudiosLookup,
                            preferredStudiosOrganic,
                            itemTagsLookup,
                            preferredTagsOrganic,
                            seriesEpisodeLookupOrganic,
                            organicFallbackTimestamp);
                        organicCount++;
                    }

                    continue;
                }

                // === Standalone items (movies, series-level favorites without SeriesId) ===
                // Note: w.SeriesId is guaranteed null here because the if (w.SeriesId.HasValue)
                // block above always exits with `continue`. Only non-series items reach this point.
                var collabScore = ContentScoring.ComputeCollaborativeScore(w.ItemId, coOccurrence, collaborativeMax);
                var ratingScore = ContentScoring.NormalizeRating(w.CommunityRating);
                // Gate completion fallback on w.Played to avoid mis-labeling favorite-only items
                // as fully watched. Favorites without playback evidence get 0.0 completion.
                double completionRatio;
                if (w.Played)
                {
                    completionRatio = 1.0;
                }
                else if (w.RuntimeTicks > 0)
                {
                    completionRatio = Math.Clamp((double)w.PlaybackPositionTicks / w.RuntimeTicks, 0.0, 1.0);
                }
                else
                {
                    completionRatio = 0.0;
                }

                var isSeries = string.Equals(w.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

                // If this standalone series has episode rows in the organic set, skip it —
                // the episode-based aggregation path (above) produces richer training signals.
                // Without this guard, iteration order could cause the standalone row to "win"
                // the aggregatedSeriesIds race and suppress episode-level aggregation.
                if (isSeries && seriesWithOrgEpisodes.Contains(w.ItemId))
                {
                    continue;
                }

                // Guard: if this standalone item is a Series object (w.SeriesId == null, w.ItemType == "Series")
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

                // Compute PeopleSimilarity from cached data (organic item may have been previously recommended).
                var peopleSimilarity = cachedPeopleLookup.TryGetValue(w.ItemId, out var organicPeople)
                    ? SimilarityComputer.ComputePeopleSimilarity(organicPeople, preferredPeopleOrganic)
                    : 0.0;

                // Compute StudioMatch and TagSimilarity from precomputed lookups (by item ID only).
                var studioMatch = false;
                var tagSimilarity = 0.0;

                if (itemStudiosLookup.TryGetValue(w.ItemId, out var organicStudios) && organicStudios.Count > 0)
                {
                    studioMatch = organicStudios.Any(s => preferredStudiosOrganic.Contains(s));
                }

                if (itemTagsLookup.TryGetValue(w.ItemId, out var organicTags) && organicTags.Count > 0)
                {
                    tagSimilarity = ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
                }

                // Series progression boost: for standalone items without SeriesId,
                // use ItemId for the lookup (actual series rows from favorites).
                var seriesProgressionBoost = 0.0;
                if (isSeries && seriesEpisodeLookupOrganic.TryGetValue(w.ItemId, out var organicEps))
                {
                    var playedEps = organicEps.Count(e => e.Played);
                    if (organicEps.Count > 0)
                    {
                        var ratio = (double)playedEps / organicEps.Count;
                        seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
                    }
                }

                var features = new CandidateFeatures
                {
                    GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(w.Genres ?? [], genrePreferences),
                    CollaborativeScore = collabScore,
                    RatingScore = ratingScore,
                    // Use content release year for recency (not watch date) to match Phase 1 semantics.
                    // Phase 1 uses rec.PremiereDate; organic items lack premiere metadata so
                    // approximate via ProductionYear, falling back to neutral 0.5.
                    RecencyScore = w.Year is int recY and >= 1 and <= 9999
                        ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1))
                        : 0.5,
                    YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
                    GenreCount = w.Genres?.Count ?? 0,
                    IsSeries = isSeries,
                    UserRatingScore = ContentScoring.ComputeUserRatingScore(w),
                    HasUserInteraction = true,
                    CompletionRatio = completionRatio,
                    PeopleSimilarity = peopleSimilarity,
                    StudioMatch = studioMatch,
                    SeriesProgressionBoost = seriesProgressionBoost,
                    PopularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3,
                    DayOfWeekAffinity = ComputeTrainingTemporalAffinity(w, w.Genres, userProfile, isDay: true),
                    HourOfDayAffinity = ComputeTrainingTemporalAffinity(w, w.Genres, userProfile, isDay: false),
                    IsWeekend = w.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                    TagSimilarity = tagSimilarity,
                    LibraryAddedRecency = 0.5
                };

                // Genre exposure features: compute from cached per-user analysis (mirrors Phase 1)
                var (organicUnderexp, organicDomRatio, organicAffGap) =
                    PreferenceBuilder.ComputeGenreExposureFeatures(w.Genres ?? [], genreExposureOrganic);
                features.GenreUnderexposure = organicUnderexp;
                features.GenreDominanceRatio = organicDomRatio;
                features.GenreAffinityGap = organicAffGap;

                // Organic watches are strong positive signals — label based on completion.
                // Favorite-only items (not played, no playback progress) get an explicit positive label.
                // Items started but abandoned (not played, but has playback progress) get a negative label.
                double label;
                if (!w.Played && w.PlaybackPositionTicks > 0 && completionRatio < EngineConstants.AbandonedCompletionThreshold)
                {
                    // Started but abandoned — active rejection signal
                    label = EngineConstants.AbandonedLabel;
                }
                else if (!w.Played && w.PlaybackPositionTicks <= 0)
                {
                    // Favorite-only: explicit interest without playback
                    label = 0.65;
                }
                else
                {
                    label = ContentScoring.ComputeEngagementLabel(completionRatio);
                }

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = w.LastPlayedDate ?? organicFallbackTimestamp,
                    SampleWeight = 0.7 // Slightly lower weight than recommended items to avoid overwhelming
                });
                organicCount++;
            }
        }

        // === Phase 3: Random negative sampling (cross-user items the user never interacted with) ===
        // Phase 1 negatives are only items the system recommended to THIS user (exposure bias).
        // Phase 2 only adds positives (organic watches). Without true negatives, the model lacks
        // a "baseline irrelevant" class and may overfit to its own recommendation distribution.
        // Cross-user negatives sample items recommended to OTHER users that this user never touched,
        // providing genuine "irrelevant for this user" examples with full metadata available.
        var randomNegativeCount = 0;
        var allRecommendedItems = new List<RecommendedItem>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                allRecommendedItems.Add(rec);
            }
        }

        if (allRecommendedItems.Count > 0)
        {
            var rngNeg = Random.Shared;
            foreach (var userProfile in allProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!profileLookup.TryGetValue(userProfile.UserId, out var userWatchedIds))
                {
                    continue;
                }

                seriesLookup.TryGetValue(userProfile.UserId, out var userWatchedSeriesIds);

                if (!recommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var userRecommendedIds))
                {
                    userRecommendedIds = new HashSet<Guid>();
                }

                var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureNeg) = perUserCache[userProfile.UserId];

                // Build per-user preference sets for negative feature computation (mirrors Phase 1/2).
                // Without these, PeopleSimilarity/StudioMatch/TagSimilarity would default to 0.0/false
                // for all negatives, creating a systematic bias (the model learns "zero = irrelevant").
                var preferredPeopleNeg = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
                var preferredStudiosNeg = BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
                var preferredTagsNeg = BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

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
                    (candidateNegatives[s], candidateNegatives[swapIdx]) = (candidateNegatives[swapIdx], candidateNegatives[s]);

                    var neg = candidateNegatives[s];
                    var collabScore = ContentScoring.ComputeCollaborativeScore(neg.ItemId, coOccurrence, collaborativeMax);
                    var ratingScore = ContentScoring.NormalizeRating(neg.CommunityRating);
                    var isSeries = string.Equals(neg.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

                    // Compute PeopleSimilarity from cached data (cross-user negative may have metadata).
                    var negPeopleSimilarity = cachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
                        ? SimilarityComputer.ComputePeopleSimilarity(negPeople, preferredPeopleNeg)
                        : 0.0;

                    // Compute StudioMatch and TagSimilarity from cached data (mirrors Phase 1/2).
                    var negStudioMatch = neg.Studios.Count > 0
                        && neg.Studios.Any(s => preferredStudiosNeg.Contains(s));
                    var negTagSimilarity = ComputeTagSimilarityFromCache(neg.Tags, preferredTagsNeg);

                    var features = new CandidateFeatures
                    {
                        GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(neg.Genres ?? [], genrePreferences),
                        CollaborativeScore = collabScore,
                        RatingScore = ratingScore,
                        RecencyScore = neg.PremiereDate.HasValue
                            ? ContentScoring.ComputeRecencyScore(neg.PremiereDate.Value)
                            : 0.5,
                        YearProximityScore = ContentScoring.ComputeYearProximity(neg.Year, avgYear),
                        GenreCount = neg.Genres?.Count ?? 0,
                        IsSeries = isSeries,
                        UserRatingScore = 0.5,
                        HasUserInteraction = false,
                        CompletionRatio = 0.5,
                        PeopleSimilarity = negPeopleSimilarity,
                        StudioMatch = negStudioMatch,
                        // SeriesProgressionBoost stays 0.0 — for cross-user negatives, the user
                        // has no episode history for that series, so 0 is the correct value.
                        PopularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3,
                        DayOfWeekAffinity = 0.5,
                        HourOfDayAffinity = 0.5,
                        IsWeekend = false,
                        TagSimilarity = negTagSimilarity,
                        LibraryAddedRecency = 0.5
                    };

                    // Genre exposure features
                    var (negUnderexp, negDomRatio, negAffGap) =
                        PreferenceBuilder.ComputeGenreExposureFeatures(neg.Genres ?? [], genreExposureNeg);
                    features.GenreUnderexposure = negUnderexp;
                    features.GenreDominanceRatio = negDomRatio;
                    features.GenreAffinityGap = negAffGap;

                    examples.Add(new TrainingExample
                    {
                        Features = features,
                        Label = 0.0,
                        GeneratedAtUtc = organicFallbackTimestamp,
                        SampleWeight = 0.5 // Lower weight than real interactions — we infer irrelevance, not observe it
                    });
                    randomNegativeCount++;
                }
            }
        }

        var positiveCount = examples.Count(e => e.Label > 0.5);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic, {randomNegativeCount} random negatives).",
            _logger);

        List<TrainingExample> trainingExamples = examples;
        if (incremental && examples.Count >= EngineConstants.IncrementalMinExamplesThreshold)
        {
            var latestGeneratedAt = previousResults.Max(r => r.GeneratedAt);
            var cutoff = latestGeneratedAt.AddDays(-1);

            var newExamples = new List<TrainingExample>();
            var oldExamples = new List<TrainingExample>();

            foreach (var ex in examples)
            {
                if (ex.GeneratedAtUtc >= cutoff)
                {
                    newExamples.Add(ex);
                }
                else
                {
                    oldExamples.Add(ex);
                }
            }

            if (oldExamples.Count > 0)
            {
                var rng = Random.Shared;
                var sampleCount = Math.Clamp(
                    (int)(oldExamples.Count * EngineConstants.IncrementalOldSampleRatio),
                    1,
                    oldExamples.Count);

                for (var i = 0; i < sampleCount; i++)
                {
                    var j = rng.Next(i, oldExamples.Count);
                    (oldExamples[i], oldExamples[j]) = (oldExamples[j], oldExamples[i]);
                }

                var sampledOld = oldExamples.GetRange(0, sampleCount);
                var combined = new List<TrainingExample>(newExamples.Count + sampleCount);
                combined.AddRange(newExamples);
                combined.AddRange(sampledOld);
                trainingExamples = combined;

                _pluginLog.LogInfo(
                    "Recommendations",
                    $"Incremental training: {newExamples.Count} new + {sampleCount} sampled old " +
                    $"(from {oldExamples.Count} total old) = {trainingExamples.Count} examples.",
                    _logger);
            }
            else
            {
                trainingExamples = newExamples;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // === Held-out validation split ===
        // Reserve the most recent 10% of examples (by GeneratedAtUtc) as a held-out validation set.
        // Train only on the remaining 90%. This provides honest generalization metrics
        // instead of optimistic training-set fit numbers.
        // Fallback: if <20 examples, skip the split and train on all (metrics will be training-set).
        const int minExamplesForHeldOut = 20;
        const double heldOutFraction = 0.10;

        List<TrainingExample> trainSplit;
        List<TrainingExample> heldOutSplit;

        if (trainingExamples.Count >= minExamplesForHeldOut)
        {
            // Sort by GeneratedAtUtc descending to pick the most recent as held-out
            var sorted = trainingExamples.OrderByDescending(e => e.GeneratedAtUtc).ToList();
            var heldOutCount = Math.Max(2, (int)(sorted.Count * heldOutFraction));
            heldOutSplit = sorted.GetRange(0, heldOutCount);
            trainSplit = sorted.GetRange(heldOutCount, sorted.Count - heldOutCount);
        }
        else
        {
            trainSplit = trainingExamples;
            heldOutSplit = [];
        }

        var trained = (strategy is ITrainableStrategy trainable) && trainable.Train(trainSplit);

        if (trained)
        {
            // Compute ranking metrics on the held-out set for honest generalization assessment.
            // When no held-out split is available (small dataset), fall back to training-set metrics.
            var metricsSource = heldOutSplit.Count >= 2 ? heldOutSplit : trainSplit;
            var metricsLabel = heldOutSplit.Count >= 2 ? "validation-set" : "training-set fit";

            var (precisionAtK, recallAtK, ndcgAtK) = Scoring.RankingMetrics.ComputeAll(
                metricsSource, strategy, Scoring.RankingMetrics.DefaultK);

            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{strategy.Name}' training completed ({metricsLabel}) — " +
                $"P@{Scoring.RankingMetrics.DefaultK}: {precisionAtK:F3}, " +
                $"R@{Scoring.RankingMetrics.DefaultK}: {recallAtK:F3}, " +
                $"NDCG@{Scoring.RankingMetrics.DefaultK}: {ndcgAtK:F3} " +
                $"(trained on {trainSplit.Count}, evaluated on {metricsSource.Count} examples).",
                _logger);
        }
        else
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{strategy.Name}' training skipped (insufficient training data).",
                _logger);
        }

        return trained;
    }

    /// <summary>
    ///     Builds a set of preferred studio names for a user from a precomputed item-to-studios lookup.
    ///     Collects studios from items the user has watched (matched by item ID or series ID).
    ///     This mirrors <see cref="PreferenceBuilder.BuildStudioPreferenceSet"/> but uses cached data
    ///     instead of live BaseItem objects.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="itemStudiosLookup">Precomputed itemId ? studios mapping built once from all previous results.</param>
    private static HashSet<string> BuildStudioPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Match the same interaction criteria used by profileLookup (line 95):
            // Played, IsFavorite, PlayCount > 0, or PlaybackPositionTicks > 0.
            if (!w.Played && !w.IsFavorite && w.PlayCount <= 0 && w.PlaybackPositionTicks <= 0)
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
            if (w.SeriesId.HasValue && itemStudiosLookup.TryGetValue(w.SeriesId.Value, out var seriesStudios))
            {
                foreach (var s in seriesStudios.Where(static s => !string.IsNullOrWhiteSpace(s)))
                {
                    studios.Add(s);
                }
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
    private static HashSet<string> BuildTagPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            // Match the same interaction criteria used by profileLookup (line 95):
            // Played, IsFavorite, PlayCount > 0, or PlaybackPositionTicks > 0.
            if (!w.Played && !w.IsFavorite && w.PlayCount <= 0 && w.PlaybackPositionTicks <= 0)
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
            if (w.SeriesId.HasValue && itemTagsLookup.TryGetValue(w.SeriesId.Value, out var seriesTags))
            {
                foreach (var t in seriesTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                {
                    tags.Add(t);
                }
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
    private static double ComputeTrainingTemporalAffinity(
        WatchHistory.WatchedItemInfo? watchedItem,
        IReadOnlyList<string>? candidateGenres,
        WatchHistory.UserWatchProfile userProfile,
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
            if (!w.Played || !w.LastPlayedDate.HasValue)
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
            if (w.Genres is not null && w.Genres.Any(g => candidateGenreSet.Contains(g)))
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
    private static void AddAggregatedSeriesExample(
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
        HashSet<string> preferredPeople,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup,
        HashSet<string> preferredStudios,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup,
        HashSet<string> preferredTags,
        Dictionary<Guid, List<WatchedItemInfo>> seriesEpisodeLookup,
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
                episodes.Average(e => ContentScoring.ComputeCompletionRatio(e)),
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
        var ratingScore = ContentScoring.NormalizeRating(mostRecent?.CommunityRating);

        // Series progression boost (same formula as Phase 1 and Engine.ScoreCandidate)
        var seriesProgressionBoost = 0.0;
        if (episodes.Count > 0)
        {
            var ratio = (double)playedEps / episodes.Count;
            seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
        }

        // PeopleSimilarity: try seriesId first (most likely hit for series-level metadata)
        var peopleSimilarity = cachedPeopleLookup.TryGetValue(seriesId, out var seriesPeople)
            ? SimilarityComputer.ComputePeopleSimilarity(seriesPeople, preferredPeople)
            : 0.0;

        // StudioMatch and TagSimilarity: look up by seriesId
        var studioMatch = false;
        var tagSimilarity = 0.0;

        if (itemStudiosLookup.TryGetValue(seriesId, out var seriesStudios) && seriesStudios.Count > 0)
        {
            studioMatch = seriesStudios.Any(s => preferredStudios.Contains(s));
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
            if (ep.Genres is not null)
            {
                foreach (var g in ep.Genres)
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
            RatingScore = ratingScore,
            // Use production year for recency (not watch date) to match Phase 1 semantics
            RecencyScore = representativeYear is int recY and >= 1 and <= 9999
                ? ContentScoring.ComputeRecencyScore(new DateTime(recY, 7, 1))
                : 0.5,
            YearProximityScore = ContentScoring.ComputeYearProximity(representativeYear, avgYear),
            GenreCount = genreList.Count,
            IsSeries = true,
            UserRatingScore = userRatingScore,
            HasUserInteraction = true,
            CompletionRatio = completionRatio,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
            PopularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3,
            DayOfWeekAffinity = ComputeTrainingTemporalAffinity(mostRecent, genreList, userProfile, isDay: true),
            HourOfDayAffinity = ComputeTrainingTemporalAffinity(mostRecent, genreList, userProfile, isDay: false),
            IsWeekend = mostRecent?.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            TagSimilarity = tagSimilarity,
            LibraryAddedRecency = 0.5
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
        double label;
        if (playedEps == 0 && episodes.Any(e => e.PlaybackPositionTicks > 0))
        {
            // Started some episodes but completed none — series-level abandonment
            label = completionRatio < EngineConstants.AbandonedCompletionThreshold
                ? EngineConstants.AbandonedLabel
                : ContentScoring.ComputeEngagementLabel(completionRatio);
        }
        else if (playedEps == 0)
        {
            label = 0.65; // Favorite-only: explicit interest without playback
        }
        else
        {
            label = ContentScoring.ComputeEngagementLabel(completionRatio);
        }

        examples.Add(new TrainingExample
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
    private static double ComputeTagSimilarityFromCache(
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
}
