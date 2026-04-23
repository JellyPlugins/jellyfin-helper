using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Recommendation engine orchestrator. Delegates to specialized components.
/// </summary>
public sealed class Engine : IRecommendationEngine
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Engine> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly IScoringStrategy _strategy;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly SimilarityComputer _similarityComputer;
    private readonly TrainingService _trainingService;

    // Short-lived cache — populated during GetAllRecommendations and reused by on-demand
    // GetRecommendations calls until next batch run invalidates it.
    // Stored as a single immutable snapshot to prevent concurrent readers from mixing data across batches.
    private volatile CandidateSnapshot? _cachedSnapshot;

    /// <summary>Initializes a new instance of the <see cref="Engine"/> class.</summary>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="strategy">The scoring strategy resolved via DI.</param>
    public Engine(
        IWatchHistoryService watchHistoryService,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<Engine> logger,
        IScoringStrategy strategy)
    {
        _watchHistoryService = watchHistoryService;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _strategy = strategy;
        _similarityComputer = new SimilarityComputer(libraryManager, pluginLog, logger);
        _trainingService = new TrainingService(watchHistoryService, pluginLog, logger);
    }

    /// <inheritdoc />
    public RecommendationResult? GetRecommendations(Guid userId, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResults = Math.Clamp(maxResults, 1, EngineConstants.MaxRecommendationsPerUserLimit);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var userProfile = allProfiles.FirstOrDefault(p => p.UserId == userId);
        if (userProfile is null)
        {
            // User not found in any watch profile — return null so the controller can 404.
            return null;
        }

        if (userProfile.WatchedItems.Count == 0)
        {
            // Cold-start: user exists but has no watch history — return popular/trending items
            return GenerateColdStartRecommendations(userId, maxResults, userProfile.UserName);
        }

        // Reuse cached candidates/people from last batch run if available, otherwise load fresh
        var snapshot = _cachedSnapshot;
        var candidates = snapshot?.Candidates ?? LoadCandidateItems();
        var peopleLookup = snapshot?.PeopleLookup ?? _similarityComputer.BuildCandidatePeopleLookup(candidates);
        return GenerateForUser(userProfile, allProfiles, candidates, peopleLookup, maxResults, _strategy, null, cancellationToken);
    }

    /// <inheritdoc />
    public bool TrainStrategy(IReadOnlyList<RecommendationResult> previousResults, bool incremental = false, CancellationToken cancellationToken = default)
        => _trainingService.Train(_strategy, previousResults, incremental, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<RecommendationResult> GetAllRecommendations(int maxResultsPerUser = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResultsPerUser = Math.Clamp(maxResultsPerUser, 1, EngineConstants.MaxRecommendationsPerUserLimit);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var candidates = LoadCandidateItems();
        var peopleLookup = _similarityComputer.BuildCandidatePeopleLookup(candidates);

        // Cache for on-demand single-user calls that may follow
        _cachedSnapshot = new CandidateSnapshot(candidates, peopleLookup);

        // Pre-compute all user watched-item sets ONCE for collaborative filtering.
        // Reduces O(U²×M) to O(U×M) by sharing sets across BuildCollaborativeMap calls.
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        _pluginLog.LogInfo(
            "Recommendations",
            $"Starting recommendation generation for {allProfiles.Count} users using strategy '{_strategy.Name}'...",
            _logger);

        // Process users in parallel — each user's scoring is CPU-bound and independent.
        // ConcurrentBag collects results safely; shared read-only data (candidates, peopleLookup,
        // precomputedUserSets) is never mutated so no locking needed.
        var concurrentResults = new ConcurrentBag<RecommendationResult>();

        Parallel.ForEach(
            allProfiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
            },
            profile =>
            {
                try
                {
                    var result = profile.WatchedItems.Count == 0
                        ? GenerateColdStartRecommendations(profile.UserId, maxResultsPerUser, profile.UserName, candidates)
                        : GenerateForUser(
                            profile,
                            allProfiles,
                            candidates,
                            peopleLookup,
                            maxResultsPerUser,
                            _strategy,
                            precomputedUserSets,
                            cancellationToken);
                    concurrentResults.Add(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _pluginLog.LogWarning(
                        "Recommendations",
                        $"Failed to generate recommendations for user '{profile.UserName}'",
                        ex,
                        _logger);
                }
            });

        var results = new Collection<RecommendationResult>(concurrentResults.ToList());

        _pluginLog.LogInfo(
            "Recommendations",
            $"Finished: {results.Count} users, {results.Sum(r => r.Recommendations.Count)} total recommendations.",
            _logger);
        return results;
    }

    /// <summary>
    ///     Generates cold-start recommendations for users with no watch history.
    ///     Uses community ratings and recency as proxy signals since no personal preferences exist.
    ///     Returns highly-rated recent items across diverse genres.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="maxResults">Maximum recommendations to return.</param>
    /// <param name="userName">Optional user display name for the result metadata.</param>
    /// <param name="preloadedCandidates">
    ///     Optional pre-loaded candidate list from the batch path.
    ///     When null, candidates are loaded fresh via <see cref="LoadCandidateItems"/>.
    /// </param>
    /// <returns>A recommendation result with popular/trending items.</returns>
    internal RecommendationResult GenerateColdStartRecommendations(
        Guid userId,
        int maxResults,
        string? userName = null,
        List<BaseItem>? preloadedCandidates = null)
    {
        var candidates = preloadedCandidates ?? LoadCandidateItems();

        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        foreach (var candidate in candidates)
        {
            var ratingScore = ContentScoring.NormalizeRating(candidate.CommunityRating);
            var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);
            // Cold-start formula: 60% rating, 40% recency — prioritize quality + freshness
            var score = (0.6 * ratingScore) + (0.4 * recencyScore);
            scored.Add((candidate, score, "Popular and highly rated", "reasonPopular", null));
        }

        var topItems = DiversityReranker.ApplyDiversityReranking(scored, maxResults)
            .Select(s => new RecommendedItem
            {
                ItemId = s.Item.Id,
                Name = s.Item.Name ?? string.Empty,
                ItemType = s.Item.GetType().Name,
                Score = Math.Round(s.Score, 4),
                Reason = s.Reason,
                ReasonKey = s.ReasonKey,
                Genres = s.Item.Genres ?? [],
                Year = s.Item.ProductionYear,
                CommunityRating = s.Item.CommunityRating,
                OfficialRating = s.Item.OfficialRating,
                PremiereDate = s.Item.PremiereDate,
                PrimaryImageTag = s.Item.HasImage(ImageType.Primary) ? s.Item.Id.ToString("N") : null,
                PeopleNames = [],
                Studios = s.Item.Studios ?? [],
                Tags = s.Item.Tags ?? []
            })
            .ToList();

        _pluginLog.LogInfo("Recommendations", $"Generated {topItems.Count} cold-start recommendations for user '{userId}' (no watch history)", _logger);

        return new RecommendationResult
        {
            UserId = userId,
            UserName = userName ?? string.Empty,
            Recommendations = new Collection<RecommendedItem>(topItems),
            GeneratedAt = DateTime.UtcNow,
            ScoringStrategy = "Cold Start (Popular + Recent)",
            ScoringStrategyKey = "strategyColdStart"
        };
    }

    /// <summary>
    ///     Loads all candidate items (movies and series) from the library.
    /// </summary>
    /// <returns>A list of candidate base items.</returns>
    internal List<BaseItem> LoadCandidateItems()
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IsFolder = false
        });

        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsFolder = true
        });

        if (movies.Count + series.Count > EngineConstants.CandidateCountWarningThreshold)
        {
            _pluginLog.LogWarning(
                "Recommendations",
                $"Large candidate set: {movies.Count + series.Count} items. Consider using the scheduled task.",
                logger: _logger);
        }

        var candidates = new List<BaseItem>(movies.Count + series.Count);
        candidates.AddRange(movies);
        candidates.AddRange(series);
        return candidates;
    }

    /// <summary>
    ///     Generates recommendations for a single user by scoring all unwatched items.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles for collaborative filtering.</param>
    /// <param name="allCandidates">Pre-loaded candidate items from the library.</param>
    /// <param name="peopleLookup">Pre-built people lookup (item ID → person names).</param>
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <param name="strategy">The scoring strategy to use.</param>
    /// <param name="precomputedUserSets">
    ///     Optional pre-computed user watch sets for collaborative filtering performance.
    ///     Pass null for single-user mode (sets will be built on-the-fly).
    /// </param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A recommendation result for the user.</returns>
    internal RecommendationResult GenerateForUser(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        List<BaseItem> allCandidates,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        int maxResults,
        IScoringStrategy strategy,
        Dictionary<Guid, HashSet<Guid>>? precomputedUserSets = null,
        CancellationToken ct = default)
    {
        // Build a lookup of watched items by ID for O(1) access in scoring methods
        var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
        foreach (var w in userProfile.WatchedItems)
        {
            watchedItemLookup.TryAdd(w.ItemId, w);
        }

        // Build a lookup of watched episodes grouped by series ID for series-level aggregation
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

        // Exclude both played AND favorited items from candidates — the user already knows these items.
        // Their genre/studio/tag/people signals still flow into preferences via PreferenceBuilder.
        var watchedIds = new HashSet<Guid>(userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite).Select(w => w.ItemId));
        var watchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.SeriesId.HasValue).Select(w => w.SeriesId!.Value));

        // Also include series-level favorites (user favorited the series itself, not individual episodes)
        foreach (var favSeriesId in userProfile.FavoriteSeriesIds)
        {
            watchedSeriesIds.Add(favSeriesId);
        }

        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(userProfile);

        // Build O(1) candidate lookup by ID — shared across studio/tag preference building
        var candidateLookup = new Dictionary<Guid, BaseItem>(allCandidates.Count);
        foreach (var c in allCandidates)
        {
            candidateLookup.TryAdd(c.Id, c);
        }

        // Build the collaborative co-occurrence map (uses precomputed sets in batch mode)
        var coOccurrence = CollaborativeFilter.BuildCollaborativeMap(userProfile, allProfiles, precomputedUserSets);
        var collaborativeMax = coOccurrence.Count > 0 ? coOccurrence.Values.Max() : 0;
        var averageYear = ContentScoring.ComputeAverageYear(userProfile);
        var preferredStudios = PreferenceBuilder.BuildStudioPreferenceSet(userProfile, candidateLookup);
        var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, peopleLookup);
        var preferredTags = PreferenceBuilder.BuildTagPreferenceSet(userProfile, candidateLookup);

        // Score each unwatched candidate
        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        var candidateIndex = 0;
        foreach (var candidate in allCandidates)
        {
            // Periodically check cancellation to stay responsive for large libraries
            if (++candidateIndex % EngineConstants.CancellationCheckBatchSize == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (watchedIds.Contains(candidate.Id))
            {
                continue;
            }

            // Skip favorited and fully-watched series from recommendations.
            // Series-level favorites are always excluded — the user already knows and loves this series.
            // Partially-watched (non-favorited) series are kept for "continue watching" style recommendations.
            if (candidate is Series && watchedSeriesIds.Contains(candidate.Id))
            {
                // Always exclude series the user explicitly favorited at the series level
                if (userProfile.FavoriteSeriesIds.Contains(candidate.Id))
                {
                    continue;
                }

                // For non-favorited series: skip if ≥90% watched, keep if partially watched
                if (seriesEpisodeLookup.TryGetValue(candidate.Id, out var eps))
                {
                    if (eps.Count > 0 && (double)eps.Count(e => e.Played) / eps.Count >= 0.9)
                    {
                        continue;
                    }
                }
                else
                {
                    continue;
                }
            }

            scored.Add(ScoreCandidate(
                candidate,
                userProfile,
                strategy,
                genrePreferences,
                coOccurrence,
                collaborativeMax,
                averageYear,
                watchedItemLookup,
                seriesEpisodeLookup,
                preferredStudios,
                preferredPeople,
                preferredTags,
                peopleLookup));
        }

        scored = DiversityReranker.DeduplicateSeries(scored);

        var topItems = DiversityReranker.ApplyDiversityReranking(scored, maxResults)
            .Select(s => new RecommendedItem
            {
                ItemId = s.Item.Id,
                Name = s.Item.Name ?? string.Empty,
                ItemType = s.Item.GetType().Name,
                Score = Math.Round(s.Score, 4),
                Reason = s.Reason,
                ReasonKey = s.ReasonKey,
                RelatedItemName = s.RelatedItem,
                Genres = s.Item.Genres ?? [],
                Year = s.Item.ProductionYear,
                CommunityRating = s.Item.CommunityRating,
                OfficialRating = s.Item.OfficialRating,
                PremiereDate = s.Item.PremiereDate,
                PrimaryImageTag = s.Item.HasImage(ImageType.Primary) ? s.Item.Id.ToString("N") : null,
                PeopleNames = peopleLookup.TryGetValue(s.Item.Id, out var people) ? [.. people] : [],
                Studios = s.Item.Studios ?? [],
                Tags = s.Item.Tags ?? []
            })
            .ToList();

        _pluginLog.LogInfo(
            "Recommendations",
            $"Generated {topItems.Count} recommendations for user '{userProfile.UserName}' using strategy '{strategy.Name}'",
            _logger);

        return new RecommendationResult
        {
            UserId = userProfile.UserId,
            UserName = userProfile.UserName,
            Profile = ReasonResolver.StripWatchedItemsForResponse(userProfile),
            Recommendations = new Collection<RecommendedItem>(topItems),
            GeneratedAt = DateTime.UtcNow,
            ScoringStrategy = strategy.Name,
            ScoringStrategyKey = strategy.NameKey
        };
    }

    /// <summary>
    ///     Scores a single candidate item against the user's preferences.
    ///     Computes all feature signals and delegates to the scoring strategy.
    /// </summary>
    private (BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem) ScoreCandidate(
        BaseItem candidate,
        UserWatchProfile userProfile,
        IScoringStrategy strategy,
        Dictionary<string, double> genrePreferences,
        Dictionary<Guid, double> coOccurrence,
        double collaborativeMax,
        double averageYear,
        Dictionary<Guid, WatchedItemInfo> watchedItemLookup,
        Dictionary<Guid, List<WatchedItemInfo>> seriesEpisodeLookup,
        HashSet<string> preferredStudios,
        HashSet<string> preferredPeople,
        HashSet<string> preferredTags,
        Dictionary<Guid, HashSet<string>> peopleLookup)
    {
        var genreScore = SimilarityComputer.ComputeGenreSimilarity(candidate.Genres ?? [], genrePreferences);
        var collabScore = ContentScoring.ComputeCollaborativeScore(candidate.Id, coOccurrence, collaborativeMax);
        var ratingScore = ContentScoring.NormalizeRating(candidate.CommunityRating);
        var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);
        var yearScore = ContentScoring.ComputeYearProximity(candidate.ProductionYear, averageYear);

        // Compute user-specific signals — for series candidates, aggregate from watched episodes
        double userRatingScore;
        double completionRatio;
        bool hasUserInteraction;

        if (candidate is Series && seriesEpisodeLookup.TryGetValue(candidate.Id, out var episodesForScoring))
        {
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
            watchedItemLookup.TryGetValue(candidate.Id, out var watchedItem);
            hasUserInteraction = watchedItem is not null;
            userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItem);
            completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItem) : 0.5;
        }

        var studioMatch = candidate.Studios is { Length: > 0 } && candidate.Studios.Any(s => preferredStudios.Contains(s));
        var peopleSimilarity = peopleLookup.TryGetValue(candidate.Id, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeople) : 0.0;

        // Series progression boost: reward next-season recommendations
        var seriesProgressionBoost = 0.0;
        if (candidate is Series candidateSeries && seriesEpisodeLookup.TryGetValue(candidateSeries.Id, out var progressionEps))
        {
            var playedEps = progressionEps.Count(e => e.Played);
            if (progressionEps.Count > 0)
            {
                var ratio = (double)playedEps / progressionEps.Count;
                seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
            }
        }

        // Popularity proxy from collaborative scores
        var popularityScore = collabScore > 0 ? Math.Clamp(collabScore * 0.8, 0.0, 1.0) : ratingScore * 0.3;

        // Build feature vector and delegate scoring to strategy
        var features = new CandidateFeatures
        {
            GenreSimilarity = genreScore,
            CollaborativeScore = collabScore,
            RatingScore = ratingScore,
            RecencyScore = recencyScore,
            YearProximityScore = yearScore,
            GenreCount = candidate.Genres?.Length ?? 0,
            IsSeries = candidate is Series,
            UserRatingScore = userRatingScore,
            HasUserInteraction = hasUserInteraction,
            CompletionRatio = completionRatio,
            PeopleSimilarity = peopleSimilarity,
            StudioMatch = studioMatch,
            SeriesProgressionBoost = seriesProgressionBoost,
            PopularityScore = popularityScore,
            DayOfWeekAffinity = TemporalFeatures.ComputeDayOfWeekAffinity(candidate, userProfile),
            HourOfDayAffinity = TemporalFeatures.ComputeHourOfDayAffinity(candidate, userProfile),
            IsWeekend = DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            TagSimilarity = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags)
        };

        var explanation = strategy.ScoreWithExplanation(features);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _pluginLog.LogDebug("Recommendations", $"Score for '{candidate.Name}': {explanation}", _logger);
        }

        var (reason, reasonKey, relatedItem) = ReasonResolver.DetermineReason(candidate, explanation, genrePreferences);

        return (candidate, explanation.FinalScore, reason, reasonKey, relatedItem);
    }

    /// <summary>
    ///     Immutable snapshot of candidate items and their people lookup.
    ///     Published/read as a single reference so concurrent readers always see
    ///     a consistent pair (candidates from the same batch as the people lookup).
    /// </summary>
    private sealed record CandidateSnapshot(
        List<BaseItem> Candidates,
        Dictionary<Guid, HashSet<string>> PeopleLookup);
}
