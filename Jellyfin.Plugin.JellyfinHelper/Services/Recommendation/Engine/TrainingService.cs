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
        var profileLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            profileLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));
        }

        var seriesLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            seriesLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => w.Played && w.SeriesId.HasValue)
                    .Select(w => w.SeriesId!.Value));
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

        var examples = new List<TrainingExample>();

        // Pre-compute per-user artifacts once and cache them. These are reused across
        // Phase 1 (recommendation feedback) and Phase 2 (organic examples), avoiding
        // redundant BuildCollaborativeMap / BuildGenrePreferenceVector calls for the same user.
        var perUserCache = new Dictionary<Guid, (
            Dictionary<string, double> GenrePreferences,
            Dictionary<Guid, double> CoOccurrence,
            double CollaborativeMax,
            double AvgYear)>();

        foreach (var profile in allProfiles)
        {
            var gp = PreferenceBuilder.BuildGenrePreferenceVector(profile);
            var co = CollaborativeFilter.BuildCollaborativeMap(profile, allProfiles, precomputedUserSets);
            var cm = co.Count > 0 ? co.Values.Max() : 0;
            var ay = ContentScoring.ComputeAverageYear(profile);
            perUserCache[profile.UserId] = (gp, co, cm, ay);
        }

        foreach (var prevResult in previousResults)
        {
            if (!profileLookup.TryGetValue(prevResult.UserId, out var watchedIds))
            {
                continue;
            }

            seriesLookup.TryGetValue(prevResult.UserId, out var watchedSeriesIds);

            var userProfile = allProfiles.FirstOrDefault(p => p.UserId == prevResult.UserId);
            if (userProfile is null)
            {
                continue;
            }

            var (genrePreferences, coOccurrence, collaborativeMax, avgYear) = perUserCache[userProfile.UserId];

            // Build preferred people/studios/tags from the user's watch profile using cached data.
            // This mirrors what Engine.GenerateForUser() does with live BaseItem data.
            var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, cachedPeopleLookup);
            var preferredStudios = BuildStudioPreferenceSetFromCache(userProfile, previousResults);
            var preferredTags = BuildTagPreferenceSetFromCache(userProfile, previousResults);

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

                double label;
                if (wasWatched)
                {
                    label = ContentScoring.ComputeEngagementLabel(features.CompletionRatio);
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
        var recommendedItemIds = new HashSet<Guid>();
        foreach (var prevResult in previousResults)
        {
            foreach (var rec in prevResult.Recommendations)
            {
                recommendedItemIds.Add(rec.ItemId);
            }
        }

        var organicCount = 0;
        foreach (var userProfile in allProfiles)
        {
            var (genrePreferences, coOccurrence, collaborativeMax, avgYear) = perUserCache[userProfile.UserId];

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

                var features = new CandidateFeatures
                {
                    GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(w.Genres ?? [], genrePreferences),
                    CollaborativeScore = collabScore,
                    RatingScore = ratingScore,
                    RecencyScore = w.LastPlayedDate.HasValue
                        ? ContentScoring.ComputeRecencyScore(w.LastPlayedDate.Value)
                        : 0.5,
                    YearProximityScore = ContentScoring.ComputeYearProximity(w.Year, avgYear),
                    GenreCount = w.Genres?.Count ?? 0,
                    IsSeries = w.SeriesId.HasValue,
                    UserRatingScore = ContentScoring.ComputeUserRatingScore(w),
                    HasUserInteraction = true,
                    CompletionRatio = completionRatio,
                    PopularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3,
                    DayOfWeekAffinity = ComputeTrainingTemporalAffinity(w, w.Genres, userProfile, isDay: true),
                    HourOfDayAffinity = ComputeTrainingTemporalAffinity(w, w.Genres, userProfile, isDay: false),
                    IsWeekend = w.LastPlayedDate?.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                };

                // Organic watches are strong positive signals — label based on completion
                var label = ContentScoring.ComputeEngagementLabel(completionRatio);

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = w.LastPlayedDate ?? DateTime.UtcNow,
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
                var sampleCount = Math.Max(1, (int)(oldExamples.Count * EngineConstants.IncrementalOldSampleRatio));

                for (var i = 0; i < Math.Min(sampleCount, oldExamples.Count); i++)
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

        var trained = (strategy is ITrainableStrategy trainable) && trainable.Train(trainingExamples);

        if (trained)
        {
            // Compute ranking metrics on the full example set to evaluate recommendation quality.
            // Unlike MSE (which measures score accuracy), these metrics measure what matters:
            // whether items the user likes land in the top K predictions.
            var (precisionAtK, recallAtK, ndcgAtK) = Scoring.RankingMetrics.ComputeAll(
                trainingExamples, strategy, Scoring.RankingMetrics.DefaultK);

            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{strategy.Name}' training completed — " +
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
    ///     Builds a set of preferred studio names from cached recommendation results for a user.
    ///     Collects studios from items the user has watched (matched by item ID or series ID).
    ///     This mirrors <see cref="PreferenceBuilder.BuildStudioPreferenceSet"/> but uses cached data
    ///     instead of live BaseItem objects.
    /// </summary>
    private static HashSet<string> BuildStudioPreferenceSetFromCache(
        UserWatchProfile userProfile,
        IReadOnlyList<RecommendationResult> allResults)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watchedItemIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite).Select(w => w.ItemId));
        var watchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.SeriesId.HasValue).Select(w => w.SeriesId!.Value));

        // Collect studios from any recommendation result that references items the user watched or favorited
        foreach (var result in allResults)
        {
            foreach (var rec in result.Recommendations)
            {
                if (!watchedItemIds.Contains(rec.ItemId) && !watchedSeriesIds.Contains(rec.ItemId))
                {
                    continue;
                }

                foreach (var s in rec.Studios)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        studios.Add(s);
                    }
                }
            }
        }

        return studios;
    }

    /// <summary>
    ///     Builds a set of preferred tag names from cached recommendation results for a user.
    ///     This mirrors <see cref="PreferenceBuilder.BuildTagPreferenceSet"/> but uses cached data.
    /// </summary>
    private static HashSet<string> BuildTagPreferenceSetFromCache(
        UserWatchProfile userProfile,
        IReadOnlyList<RecommendationResult> allResults)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watchedItemIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite).Select(w => w.ItemId));
        var watchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.SeriesId.HasValue).Select(w => w.SeriesId!.Value));

        foreach (var result in allResults)
        {
            foreach (var rec in result.Recommendations)
            {
                if (!watchedItemIds.Contains(rec.ItemId) && !watchedSeriesIds.Contains(rec.ItemId))
                {
                    continue;
                }

                foreach (var t in rec.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        tags.Add(t);
                    }
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

            bool inBucket;
            if (isDay)
            {
                inBucket = w.LastPlayedDate.Value.DayOfWeek == watchDate.DayOfWeek;
            }
            else
            {
                inBucket = TemporalFeatures.GetTimeBucket(w.LastPlayedDate.Value.Hour)
                    == TemporalFeatures.GetTimeBucket(watchDate.Hour);
            }

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
