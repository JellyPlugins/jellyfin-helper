using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

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
    /// <returns>A tuple of training examples, organic count, and random negative count.</returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        CancellationToken cancellationToken)
    {
        return BuildExamples(previousResults, allProfiles, discoveryFeedback: null, cancellationToken);
    }

    /// <summary>
    ///     Builds all training examples from previous results, user profiles, and optional discovery feedback.
    /// </summary>
    /// <param name="previousResults">The recommendation results from previous runs.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="discoveryFeedback">Optional discovery feedback data for Phase 4.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A tuple of training examples, organic count, and random negative count.</returns>
    internal static (List<TrainingExample> Examples, int OrganicCount, int RandomNegativeCount) BuildExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        Collection<UserWatchProfile> allProfiles,
        IReadOnlyList<DiscoveryFeedbackResult>? discoveryFeedback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        cancellationToken.ThrowIfCancellationRequested();

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

        // Pre-compute collaborative data for all users (needed for full feature vectors)
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        // Build a people lookup from cached recommendation data (PeopleNames stored on RecommendedItem).
        // This allows computing PeopleSimilarity during training without re-querying the library.
        var cachedPeopleLookup = new Dictionary<Guid, HashSet<string>>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations.Where(
                         r => r.PeopleNames.Count > 0 && !cachedPeopleLookup.ContainsKey(r.ItemId)))
            {
                cachedPeopleLookup[rec.ItemId] = new HashSet<string>(
                    rec.PeopleNames,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        // Pre-compute itemId ? studios and itemId ? tags lookups ONCE from all previous results.
        // This avoids O(users × results × recommendations) rescanning in BuildStudioPreferenceSetFromCache
        // and BuildTagPreferenceSetFromCache - each user's preference set is now O(watchedItems) instead.
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

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposure) =
                perUserCache[userProfile.UserId];

            // Build preferred people/studios/tags from the user's watch profile using cached data.
            // This mirrors what Engine.GenerateForUser() does with live BaseItem data.
            var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
            var preferredStudios = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
            var preferredTags = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

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

            // Build watched genre/people/studio sets for ContentNearestNeighborScore computation.
            // Mirrors Engine.GenerateForUser() logic: parallel lists indexed by watched item.
            var watchedGenreSets = new List<HashSet<string>>();
            var watchedPeopleSets = new List<HashSet<string>>();
            var watchedStudioSets = new List<HashSet<string>>();
            foreach (var w in userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite))
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

            // Build per-user watchedBoxSetCounts from cached recommendations (mirrors Engine.BuildWatchedBoxSetCounts).
            // For each recommendation that the user actually watched, accumulate its BoxSet membership counts.
            // This enables ComputeCollectionProgressionBoostFromCache to use the same diminishing-returns
            // formula as ComputeCollectionProgressionBoostLive, achieving training/inference parity.
            var watchedBoxSetCounts = new Dictionary<Guid, int>();
            foreach (var rec in prevResult.Recommendations)
            {
                var isWatched = watchedIds.Contains(rec.ItemId)
                                || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);
                if (!isWatched || rec.BoxSetIds.Count == 0)
                {
                    continue;
                }

                foreach (var boxSetId in rec.BoxSetIds)
                {
                    watchedBoxSetCounts.TryGetValue(boxSetId, out var count);
                    watchedBoxSetCounts[boxSetId] = count + 1;
                }
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

                switch (isSeries)
                {
                    case true when seriesEpisodeLookup.TryGetValue(rec.ItemId, out var episodesForScoring):
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
                                    episodesForScoring.Average(ContentScoring.ComputeCompletionRatio),
                                    0.0,
                                    1.0)
                                : 0.5;
                            break;
                        }

                    case true when wasWatched && watchedItemForRec is null:
                        // Series-level favorite without watched episodes: the user favorited
                        // the series itself but hasn't played any episodes yet.
                        // Treat as explicit positive interaction with favorite-appropriate defaults.
                        hasUserInteraction = true;
                        userRatingScore = 0.5;
                        completionRatio = 0.0;
                        break;
                    default:
                        hasUserInteraction = watchedItemForRec is not null;
                        userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItemForRec);
                        completionRatio = hasUserInteraction
                            ? ContentScoring.ComputeCompletionRatio(watchedItemForRec)
                            : 0.5;
                        break;
                }

                // Compute collaborative score for this specific item
                var collabScore = ContentScoring.ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax);

                // Popularity proxy matching Engine.ScoreCandidate() logic
                var combinedCriticScore =
                    ContentScoring.ComputeCombinedCriticScore(rec.CommunityRating, rec.CriticRating);
                var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

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
                                  && rec.Studios.Any(preferredStudios.Contains);

                // Compute TagSimilarity from cached data (matches Engine.ScoreCandidate() logic)
                var tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(rec.Tags, preferredTags);

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
                        rec.Genres,
                        userProfile,
                        isDay: true),
                    HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(
                        watchedItemForRec,
                        rec.Genres,
                        userProfile,
                        isDay: false),
                    IsWeekend = watchedItemForRec?.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
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
                    SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(rec.SubtitleLanguages, userProfile)
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
                    // 1. Favorite-only (no playback): explicit interest signal → 0.65
                    // 2. Abandoned (started but stopped early): strong negative signal → 0.0
                    // 3. Normal watch: engagement-proportional label (0.5–0.85)
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

                examples.Add(
                    new TrainingExample
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
        // Guard: if previousResults is empty (no prior recommendation runs), use a
        // conservative fallback 90 days ago. This path is defensive only - BuildExamples()
        // callers always pass non-empty results from the recommendation store.
        var organicFallbackTimestamp = previousResults.Count > 0
            ? previousResults.Min(r => r.GeneratedAt)
            : DateTime.UtcNow.AddDays(-90);

        var organicCount = 0;
        foreach (var userProfile in allProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureOrganic) =
                perUserCache[userProfile.UserId];

            // Resolve the per-user recommended set; users with no previous results get an empty set
            if (!recommendedItemIdsByUser.TryGetValue(userProfile.UserId, out var recommendedItemIds))
            {
                recommendedItemIds = [];
            }

            // Build per-user preference sets for organic feature computation (mirrors Phase 1).
            var preferredPeopleOrganic = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
            var preferredStudiosOrganic = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
            var preferredTagsOrganic = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

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
                         && candidate.HasMeaningfulInteraction()
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
                            preferredPeopleOrganic,
                            itemStudiosLookup,
                            preferredStudiosOrganic,
                            itemTagsLookup,
                            preferredTagsOrganic,
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
                    studioMatch = organicStudios.Any(preferredStudiosOrganic.Contains);
                }

                if (itemTagsLookup.TryGetValue(w.ItemId, out var organicTags) && organicTags.Count > 0)
                {
                    tagSimilarity = TrainingFeatureComputer.ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
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

                // Null-safe genre access for deserialized cache objects
                var wGenres = w.Genres ?? Array.Empty<string>();

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
                    UserRatingScore = ContentScoring.ComputeUserRatingScore(w),
                    HasUserInteraction = true,
                    CompletionRatio = completionRatio,
                    PeopleSimilarity = peopleSimilarity,
                    StudioMatch = studioMatch,
                    SeriesProgressionBoost = seriesProgressionBoost,
                    PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
                    DayOfWeekAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenres, userProfile, isDay: true),
                    HourOfDayAffinity = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(w, wGenres, userProfile, isDay: false),
                    IsWeekend = w.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                    TagSimilarity = tagSimilarity,
                    LibraryAddedRecency = w.DateCreated.HasValue
                        ? ContentScoring.ComputeRecencyScore(w.DateCreated.Value)
                        : 0.5,
                    // Organic standalone items lack per-item stream metadata (AudioLanguages/SubtitleLanguages
                    // are only cached on RecommendedItem from Phase 1). Use neutral 0.5 to match the live
                    // scoring path which also returns 0.5 when stream data is unavailable, preventing
                    // train/serve skew on these two dimensions.
                    LanguageAffinity = 0.5,
                    SubtitleLanguageAffinity = 0.5
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

                examples.Add(
                    new TrainingExample
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

                var (genrePreferences, coOccurrence, collaborativeMax, avgYear, genreExposureNeg) =
                    perUserCache[userProfile.UserId];

                // Build per-user preference sets for negative feature computation (mirrors Phase 1/2).
                // Without these, PeopleSimilarity/StudioMatch/TagSimilarity would default to 0.0/false
                // for all negatives, creating a systematic bias (the model learns "zero = irrelevant").
                var preferredPeopleNeg = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
                var preferredStudiosNeg = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(userProfile, itemStudiosLookup);
                var preferredTagsNeg = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(userProfile, itemTagsLookup);

                // Build watched genre/people/studio sets for ContentNearestNeighborScore (mirrors Phase 1).
                var watchedGenreSetsNeg = new List<HashSet<string>>();
                var watchedPeopleSetsNeg = new List<HashSet<string>>();
                var watchedStudioSetsNeg = new List<HashSet<string>>();
                foreach (var w in userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite))
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
                    var collabScore = ContentScoring.ComputeCollaborativeScore(
                        neg.ItemId,
                        coOccurrence,
                        collaborativeMax);
                    var combinedCriticScore =
                        ContentScoring.ComputeCombinedCriticScore(neg.CommunityRating, neg.CriticRating);
                    var isSeries = string.Equals(neg.ItemType, "Series", StringComparison.OrdinalIgnoreCase);

                    // Compute PeopleSimilarity from cached data (cross-user negative may have metadata).
                    var negPeopleSimilarity = cachedPeopleLookup.TryGetValue(neg.ItemId, out var negPeople)
                        ? SimilarityComputer.ComputePeopleSimilarity(negPeople, preferredPeopleNeg)
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
                        CompletionRatio = 0.5,
                        PeopleSimilarity = negPeopleSimilarity,
                        StudioMatch = negStudioMatch,
                        // SeriesProgressionBoost stays 0.0 - for cross-user negatives, the user
                        // has no episode history for that series, so 0 is the correct value.
                        PopularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore),
                        DayOfWeekAffinity = 0.5,
                        HourOfDayAffinity = 0.5,
                        IsWeekend = false,
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
                        CollectionProgressionBoost = ComputeCollectionProgressionBoostFromCache(neg.BoxSetIds, userWatchedIds),
                        SubtitleLanguageAffinity = TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(neg.SubtitleLanguages, userProfile)
                    };

                    // Genre exposure features
                    var (negUnderexp, negDomRatio, negAffGap) =
                        PreferenceBuilder.ComputeGenreExposureFeatures(negGenres, genreExposureNeg);
                    features.GenreUnderexposure = negUnderexp;
                    features.GenreDominanceRatio = negDomRatio;
                    features.GenreAffinityGap = negAffGap;

                    examples.Add(
                        new TrainingExample
                        {
                            Features = features,
                            Label = 0.0,
                            GeneratedAtUtc = organicFallbackTimestamp,
                            SampleWeight =
                                0.5 // Lower weight than real interactions - we infer irrelevance, not observe it
                        });
                    randomNegativeCount++;
                }
            }
        }

        // === Phase 4: Discovery feedback examples (shown/dismissed/requested/watched) ===
        // Discovery items are external (not in library). Their interactions provide valuable
        // explicit signals: requests are strong positives, dismissals are negatives.
        // Only added when discovery feedback is available (non-null, non-empty).
        if (discoveryFeedback is { Count: > 0 })
        {
            var (discoveryExamples, discoveryCount) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
                discoveryFeedback,
                profileById,
                cancellationToken);

            if (discoveryExamples.Count > 0)
            {
                examples.AddRange(discoveryExamples);
                organicCount += discoveryCount; // Discovery examples counted alongside organic for unified reporting (see Phase 4 design note)
            }
        }

        return (examples, organicCount, randomNegativeCount);
    }

    /// <summary>
    ///     Computes CollectionProgressionBoost using the same diminishing-returns formula as
    ///     <see cref="Engine.ComputeCollectionProgressionBoostLive"/>. Uses a pre-built
    ///     <paramref name="watchedBoxSetCounts"/> dictionary (built once per user from cached
    ///     recommendations) to achieve training/inference parity.
    /// </summary>
    /// <param name="boxSetIds">The cached BoxSet IDs for the candidate item.</param>
    /// <param name="watchedBoxSetCounts">Pre-computed BoxSet ID → watched member count mapping.</param>
    /// <returns>A collection progression boost between 0.0 and 1.0, matching the inference formula.</returns>
    private static double ComputeCollectionProgressionBoostWithCounts(
        IReadOnlyList<Guid>? boxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        if (boxSetIds is null || boxSetIds.Count == 0)
        {
            return 0.0;
        }

        if (watchedBoxSetCounts.Count == 0)
        {
            return 0.0;
        }

        // Find the best progression signal across all BoxSets the candidate belongs to.
        // Formula matches Engine.ComputeCollectionProgressionBoostLive exactly:
        // Scale: 1 watched sibling = 0.3, 2 = 0.5, 3+ = 0.7+
        var bestBoost = 0.0;
        foreach (var boxSetId in boxSetIds)
        {
            if (watchedBoxSetCounts.TryGetValue(boxSetId, out var watchedCount) && watchedCount > 0)
            {
                var boost = Math.Clamp(0.3 + ((watchedCount - 1) * 0.2), 0.0, 1.0);
                bestBoost = Math.Max(bestBoost, boost);
            }
        }

        return bestBoost;
    }

    /// <summary>
    ///     Computes CollectionProgressionBoost from cached BoxSet IDs stored on <see cref="RecommendedItem"/>.
    ///     Used by Phase 3 (cross-user random negatives) where per-BoxSet sibling counts are not
    ///     available. For items the user has never interacted with, collection membership alone
    ///     provides a baseline signal.
    ///     Checks whether the user has watched any other items that share the same BoxSet membership.
    ///     Returns 0.5 when a BoxSet ID itself appears in the user's watched set (strong progression),
    ///     0.3 when the item belongs to a BoxSet but no direct sibling match is found (membership signal),
    ///     or 0.0 when the item is not in any collection.
    ///     <para>
    ///         <b>Training vs Inference parity note:</b> The live scoring path
    ///         (<see cref="Engine.ComputeCollectionProgressionBoostLive"/>) has access to the full
    ///         pre-computed watchedBoxSetCounts dictionary and uses a diminishing-returns formula
    ///         (0.3 + (watchedCount-1) × 0.2, clamped to [0,1]). Training cannot replicate this
    ///         because cached BoxSetIds only store which BoxSets a candidate belongs to, not the
    ///         sibling item IDs within each BoxSet. This flat heuristic (0.0 / 0.3 / 0.5) is the
    ///         best approximation available from cached data and intentionally differs from inference.
    ///     </para>
    /// </summary>
    /// <param name="boxSetIds">The cached BoxSet IDs for the candidate item.</param>
    /// <param name="watchedIds">Set of item IDs the user has watched.</param>
    /// <returns>A collection progression boost between 0.0 and 0.5.</returns>
    private static double ComputeCollectionProgressionBoostFromCache(
        IReadOnlyList<Guid>? boxSetIds,
        HashSet<Guid> watchedIds)
    {
        if (boxSetIds is null || boxSetIds.Count == 0)
        {
            return 0.0;
        }

        // Check if any of the BoxSet IDs themselves are in the user's watched set.
        // This can happen when the user has watched the BoxSet "container" or when
        // the BoxSet ID was stored as a watched item ID by the system.
        if (boxSetIds.Any(watchedIds.Contains))
        {
            // User has interacted with the collection itself - strong progression signal
            return 0.5;
        }

        // The item belongs to at least one BoxSet. Give a moderate base boost (0.3)
        // indicating collection membership. Without full child enumeration from cached data,
        // we cannot compute the exact progression ratio, but collection membership alone
        // is a meaningful signal that the neural network can weight appropriately.
        return 0.3;
    }
}
