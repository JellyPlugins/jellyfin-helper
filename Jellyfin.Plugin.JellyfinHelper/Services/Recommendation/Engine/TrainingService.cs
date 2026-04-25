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

        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();

        // Include both played AND favorited items as positive interactions.
        // A favorited-but-not-played recommended item signals explicit interest
        // and should not be labeled as exposure/abandonment.
        var profileLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            profileLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems.Where(w => w.Played || w.IsFavorite).Select(w => w.ItemId));
        }

        var seriesLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            var seriesIds = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => (w.Played || w.IsFavorite) && w.SeriesId.HasValue)
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

        // Pre-compute itemId → studios and itemId → tags lookups ONCE from all previous results.
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
                    completionRatio = episodesForScoring.Count > 0
                        ? Math.Clamp((double)episodesForScoring.Count(e => e.Played) / episodesForScoring.Count, 0.0, 1.0)
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
                    TagSimilarity = tagSimilarity
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
                    // Check temporal proximity: was the item watched within the influence window
                    // after the recommendation was generated? If so, the recommendation likely
                    // influenced the watch — reward with a higher label.
                    var baseLabel = watchedItemForRec is { IsFavorite: true, Played: false }
                        ? 0.65 // Favorite-only: explicit interest signal even without playback
                        : watchedItemForRec is null && isSeries
                            ? 0.65 // Series-level favorite without episode data
                            : ContentScoring.ComputeEngagementLabel(features.CompletionRatio);
                    // Watched shortly after recommendation — boost label
                    label = watchedItemForRec?.LastPlayedDate is not null
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

            foreach (var w in userProfile.WatchedItems)
            {
                // Include played OR favorited items that were NEVER recommended (organic discoveries).
                // Favorites signal explicit interest even if not yet played — they provide
                // positive training signal that the model should learn from.
                if ((!w.Played && !w.IsFavorite) || recommendedItemIds.Contains(w.ItemId))
                {
                    continue;
                }

                // Skip series IDs already covered
                if (w.SeriesId.HasValue && recommendedItemIds.Contains(w.SeriesId.Value))
                {
                    continue;
                }

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

                // Treat episodes as series-level items when they have a SeriesId.
                // Organic watch history is typically episode rows, not series rows,
                // so checking only ItemType == "Series" would miss virtually all organic
                // series data. By also checking SeriesId, episodes contribute their
                // series progression boost and IsSeries feature correctly.
                var isSeries = string.Equals(w.ItemType, "Series", StringComparison.OrdinalIgnoreCase)
                    || w.SeriesId.HasValue;

                // For series-level lookups, use SeriesId when available (episode rows),
                // fall back to ItemId (actual series rows from favorites).
                var seriesLookupId = w.SeriesId ?? w.ItemId;

                // Compute PeopleSimilarity from cached data (organic item may have been previously recommended).
                // Try both the item's own ID and its SeriesId for people lookup matches.
                var peopleSimilarity = cachedPeopleLookup.TryGetValue(w.ItemId, out var organicPeople)
                    ? SimilarityComputer.ComputePeopleSimilarity(organicPeople, preferredPeopleOrganic)
                    : (w.SeriesId.HasValue && cachedPeopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople)
                        ? SimilarityComputer.ComputePeopleSimilarity(seriesPeople, preferredPeopleOrganic)
                        : 0.0);

                // Compute StudioMatch — look up organic item in precomputed studio/tag lookups.
                // Try both the item's own ID and its SeriesId.
                var studioMatch = false;
                var tagSimilarity = 0.0;

                // Check item's own ID first, then series ID for studios
                IReadOnlyList<string>? organicStudios = null;
                IReadOnlyList<string>? organicTags = null;
                if (itemStudiosLookup.TryGetValue(w.ItemId, out var s1))
                {
                    organicStudios = s1;
                }
                else if (w.SeriesId.HasValue && itemStudiosLookup.TryGetValue(w.SeriesId.Value, out var s2))
                {
                    organicStudios = s2;
                }

                if (itemTagsLookup.TryGetValue(w.ItemId, out var t1))
                {
                    organicTags = t1;
                }
                else if (w.SeriesId.HasValue && itemTagsLookup.TryGetValue(w.SeriesId.Value, out var t2))
                {
                    organicTags = t2;
                }

                if (organicStudios is { Count: > 0 })
                {
                    studioMatch = organicStudios.Any(s => preferredStudiosOrganic.Contains(s));
                }

                if (organicTags is { Count: > 0 })
                {
                    tagSimilarity = ComputeTagSimilarityFromCache(organicTags, preferredTagsOrganic);
                }

                // Series progression boost for organic series items.
                // Uses SeriesId for episode rows so the lookup actually finds matching entries
                // in the seriesEpisodeLookupOrganic dictionary (keyed by SeriesId).
                var seriesProgressionBoost = 0.0;
                if (isSeries && seriesEpisodeLookupOrganic.TryGetValue(seriesLookupId, out var organicEps))
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
                    TagSimilarity = tagSimilarity
                };

                // Genre exposure features: compute from cached per-user analysis (mirrors Phase 1)
                var (organicUnderexp, organicDomRatio, organicAffGap) =
                    PreferenceBuilder.ComputeGenreExposureFeatures(w.Genres ?? [], genreExposureOrganic);
                features.GenreUnderexposure = organicUnderexp;
                features.GenreDominanceRatio = organicDomRatio;
                features.GenreAffinityGap = organicAffGap;

                // Organic watches are strong positive signals — label based on completion.
                // Favorite-only items (not played) get an explicit positive label since
                // favoriting signals interest even without playback evidence.
                var label = !w.Played
                    ? 0.65
                    : ContentScoring.ComputeEngagementLabel(completionRatio);

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = w.LastPlayedDate ?? DateTime.UtcNow.AddDays(-90),
                    SampleWeight = 0.7 // Slightly lower weight than recommended items to avoid overwhelming
                });
                organicCount++;
            }
        }

        var positiveCount = examples.Count(e => e.Label > 0.5);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic watch examples added).",
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

        var trained = (strategy is ITrainableStrategy trainable) && trainable.Train(trainingExamples);

        if (trained)
        {
            // Compute ranking metrics on the full example set to evaluate recommendation quality.
            // Unlike MSE (which measures score accuracy), these metrics measure what matters:
            // whether items the user likes land in the top K predictions.
            // NOTE: These are training-set metrics (not held-out validation). They measure fit,
            // not generalization. Useful for trend monitoring, but expect optimistic values.
            var (precisionAtK, recallAtK, ndcgAtK) = Scoring.RankingMetrics.ComputeAll(
                trainingExamples, strategy, Scoring.RankingMetrics.DefaultK);

            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{strategy.Name}' training completed (training-set fit) — " +
                $"P@{Scoring.RankingMetrics.DefaultK}: {precisionAtK:F3}, " +
                $"R@{Scoring.RankingMetrics.DefaultK}: {recallAtK:F3}, " +
                $"NDCG@{Scoring.RankingMetrics.DefaultK}: {ndcgAtK:F3} " +
                $"({trainingExamples.Count} examples).",
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
    /// <param name="itemStudiosLookup">Precomputed itemId → studios mapping built once from all previous results.</param>
    private static HashSet<string> BuildStudioPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemStudiosLookup)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played && !w.IsFavorite)
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

            // Also look up studios by the item's series ID (episodes → series mapping)
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
    /// <param name="itemTagsLookup">Precomputed itemId → tags mapping built once from all previous results.</param>
    private static HashSet<string> BuildTagPreferenceSetFromCache(
        UserWatchProfile userProfile,
        Dictionary<Guid, IReadOnlyList<string>> itemTagsLookup)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played && !w.IsFavorite)
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

            // Also look up tags by the item's series ID (episodes → series mapping)
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
