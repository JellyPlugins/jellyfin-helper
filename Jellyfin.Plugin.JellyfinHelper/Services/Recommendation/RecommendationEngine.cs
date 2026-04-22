using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Pure C# recommendation engine using content-based filtering (genre/year similarity),
///     collaborative co-occurrence, and recency/rating boosts.
///     Supports pluggable scoring strategies (heuristic or learned ML).
/// </summary>
public sealed class RecommendationEngine : IRecommendationEngine
{
    /// <summary>
    ///     Minimum number of shared watched items required between two users
    ///     before collaborative filtering considers them similar.
    /// </summary>
    internal const int MinCollaborativeOverlap = 3;

    /// <summary>
    ///     Minimum weighted contribution before a specific reason (genre, collaborative) is shown.
    ///     Must be low enough to work across strategies whose weights differ
    ///     (e.g. collaborative weight 0.12–0.15 → max contribution 0.12–0.15).
    /// </summary>
    internal const double ReasonScoreThreshold = 0.05;

    /// <summary>
    ///     Minimum weighted rating contribution before "Highly rated" reason is shown.
    ///     Rating weights are typically 0.08–0.10, so a threshold of 0.04 requires
    ///     the normalised community rating to be at least ~0.5 (i.e. ≥ 5.0/10).
    /// </summary>
    internal const double HighRatingThreshold = 0.04;

    /// <summary>
    ///     Boost factor applied to genres from favorited items when building preferences.
    ///     Favorites count this many times more than regular watched items.
    /// </summary>
    internal const double FavoriteGenreBoostFactor = 3.0;

    /// <summary>
    ///     Exponential decay constant for recency scoring (~365 day half-life).
    /// </summary>
    internal const double RecencyDecayConstant = 0.0019;

    /// <summary>
    ///     Gaussian denominator for year proximity (σ² × 2 = 200, so σ ≈ 10 years).
    /// </summary>
    internal const double YearProximityDenominator = 200.0;

    /// <summary>
    ///     Maximum allowed recommendations per user (input validation clamp).
    /// </summary>
    internal const int MaxRecommendationsPerUser = 100;

    /// <summary>
    ///     MMR diversity trade-off parameter (0 = pure diversity, 1 = pure relevance).
    ///     A value of 0.7 gives strong relevance with meaningful diversity.
    /// </summary>
    internal const double MmrLambda = 0.7;

    /// <summary>
    ///     Minimum watch completion ratio below which an item is considered "abandoned".
    ///     Items abandoned by the user receive a penalty in scoring to avoid re-recommending
    ///     content the user already tried and didn't like.
    /// </summary>
    internal const double AbandonedCompletionThreshold = 0.25;

    /// <summary>
    ///     Soft label for watched items (not 1.0 to leave headroom and reduce label noise).
    /// </summary>
    internal const double WatchedLabel = 0.85;

    /// <summary>
    ///     Label for items the user started but abandoned (strong negative signal).
    /// </summary>
    internal const double AbandonedLabel = 0.0;

    /// <summary>
    ///     Label for previously recommended but unwatched items (exposure bias mitigation —
    ///     user saw the recommendation but didn't engage).
    /// </summary>
    internal const double ExposureLabel = 0.1;

    /// <summary>
    ///     Maximum candidate count before a performance warning is emitted.
    ///     Libraries with more items than this threshold may experience slower on-demand scoring.
    /// </summary>
    internal const int CandidateCountWarningThreshold = 5000;

    /// <summary>
    ///     Batch size for cancellation token checks inside the candidate scoring loop.
    ///     Checking every single iteration is wasteful; checking every N items balances
    ///     responsiveness with overhead.
    /// </summary>
    internal const int CancellationCheckBatchSize = 200;

    /// <summary>
    ///     Minimum number of genres a user must have in their preference vector
    ///     before genre pre-filtering is applied. Below this threshold, the user's
    ///     taste profile is too sparse for reliable filtering.
    /// </summary>
    internal const int GenrePreFilterMinPreferences = 3;

    /// <summary>
    ///     PersonKind types considered for people similarity scoring.
    ///     Only actors and directors are used — writers/producers are less predictive
    ///     of user preference and would add noise to the similarity signal.
    /// </summary>
    private static readonly PersonKind[] RelevantPersonKinds = [PersonKind.Actor, PersonKind.Director];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RecommendationEngine> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly IScoringStrategy _strategy;
    private readonly IWatchHistoryService _watchHistoryService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationEngine" /> class.
    /// </summary>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="strategy">The scoring strategy resolved via DI (based on configuration).</param>
    public RecommendationEngine(
        IWatchHistoryService watchHistoryService,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<RecommendationEngine> logger,
        IScoringStrategy strategy)
    {
        _watchHistoryService = watchHistoryService;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _strategy = strategy;
    }

    /// <inheritdoc />
    public RecommendationResult? GetRecommendations(Guid userId, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResults = Math.Clamp(maxResults, 1, MaxRecommendationsPerUser);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var userProfile = allProfiles.FirstOrDefault(p => p.UserId == userId);
        if (userProfile is null)
        {
            return null;
        }

        // Load candidates once
        var candidates = LoadCandidateItems();

        // Batch-load people data once for all candidates (performance: single pass over library)
        var peopleLookup = BuildCandidatePeopleLookup(candidates);

        return GenerateForUser(userProfile, allProfiles, candidates, peopleLookup, maxResults, _strategy, cancellationToken);
    }

    /// <inheritdoc />
    public bool TrainStrategy(IReadOnlyList<RecommendationResult> previousResults)
    {
        if (previousResults.Count == 0)
        {
            _pluginLog.LogInfo("Recommendations", "Training skipped — no previous recommendations available.", _logger);
            return false;
        }

        // Build current watch profiles to detect which recommended items have been watched since
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var profileLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            profileLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));
        }

        // Also collect watched series IDs (user may have watched episodes → series counts as watched)
        var seriesLookup = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var profile in allProfiles)
        {
            seriesLookup[profile.UserId] = new HashSet<Guid>(
                profile.WatchedItems
                    .Where(w => w.Played && w.SeriesId.HasValue)
                    .Select(w => w.SeriesId!.Value));
        }

        // Build training examples from previous recommendations
        var examples = new List<TrainingExample>();

        foreach (var prevResult in previousResults)
        {
            if (!profileLookup.TryGetValue(prevResult.UserId, out var watchedIds))
            {
                continue;
            }

            seriesLookup.TryGetValue(prevResult.UserId, out var watchedSeriesIds);

            // Find the user's current profile for feature recomputation
            var userProfile = allProfiles.FirstOrDefault(p => p.UserId == prevResult.UserId);
            if (userProfile is null)
            {
                continue;
            }

            var genrePreferences = BuildGenrePreferenceVector(userProfile);
            var coOccurrence = BuildCollaborativeMap(userProfile, allProfiles);
            var collaborativeMax = coOccurrence.Count > 0 ? (double)coOccurrence.Values.Max() : 0;
            var avgYear = ComputeAverageYear(userProfile);

            // Build O(1) lookup for watched items per user (Fix #4: replaces O(n) LINQ scan per rec)
            var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
            foreach (var w in userProfile.WatchedItems)
            {
                watchedItemLookup.TryAdd(w.ItemId, w);
            }

            foreach (var rec in prevResult.Recommendations)
            {
                // Determine label: did the user watch this item since it was recommended?
                var wasWatched = watchedIds.Contains(rec.ItemId)
                    || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);

                // Recompute features for this candidate (scores may have shifted)
                // Look up user-specific signals via O(1) dictionary instead of O(n) LINQ scan (Fix #4)
                watchedItemLookup.TryGetValue(rec.ItemId, out var watchedItemForRec);

                var features = new CandidateFeatures
                {
                    GenreSimilarity = ComputeGenreSimilarity(rec.Genres ?? [], genrePreferences),
                    CollaborativeScore = ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax),
                    RatingScore = NormalizeRating(rec.CommunityRating),
                    RecencyScore = rec.PremiereDate.HasValue
                        ? ComputeRecencyScore(rec.PremiereDate.Value)
                        : 0.5, // fallback for legacy stored recommendations without PremiereDate
                    YearProximityScore = ComputeYearProximity(rec.Year, avgYear),
                    GenreCount = rec.Genres?.Count ?? 0,
                    IsSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase),
                    UserRatingScore = ComputeUserRatingScore(watchedItemForRec),
                    CompletionRatio = ComputeCompletionRatio(watchedItemForRec)
                };

                // Soft labels with hard negative mining for abandoned items.
                // NOTE: The label must NOT depend on the community rating — that would cause
                // feature leakage because RatingScore is already a feature in the model.
                double label;
                if (wasWatched)
                {
                    // Binary engagement signal: the user chose to watch this item.
                    // We use WatchedLabel instead of 1.0 to leave headroom and reduce label noise.
                    label = WatchedLabel;
                }
                else if (features.CompletionRatio > 0 && features.CompletionRatio < AbandonedCompletionThreshold)
                {
                    // Hard negative: user started watching but abandoned before 25% completion.
                    // This is a stronger negative signal than simply not watching — the user
                    // actively tried the content and chose not to continue.
                    label = AbandonedLabel;
                }
                else
                {
                    // Exposure bias mitigation: items that were recommended but not watched
                    // get a softened negative label (0.1 instead of 0.0) because "not watched"
                    // doesn't necessarily mean "not interested" — the user may not have seen it yet.
                    label = ExposureLabel;
                }

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    GeneratedAtUtc = prevResult.GeneratedAt
                });
            }
        }

        var positiveCount = examples.Count(e => e.Label > 0.5);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users.",
            _logger);

        var trained = (_strategy is ITrainableStrategy trainable) && trainable.Train(examples);

        if (trained)
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{_strategy.Name}' training completed successfully.",
                _logger);
        }
        else
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{_strategy.Name}' training skipped (insufficient training data).",
                _logger);
        }

        return trained;
    }

    /// <inheritdoc />
    public IReadOnlyList<RecommendationResult> GetAllRecommendations(int maxResultsPerUser = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResultsPerUser = Math.Clamp(maxResultsPerUser, 1, MaxRecommendationsPerUser);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var results = new Collection<RecommendationResult>();

        // Load candidates once for ALL users (performance optimization)
        var candidates = LoadCandidateItems();

        // Batch-load people data once for all candidates, shared across ALL users (performance: avoids N×M queries)
        var peopleLookup = BuildCandidatePeopleLookup(candidates);

        _pluginLog.LogInfo(
            "Recommendations",
            $"Starting recommendation generation for {allProfiles.Count} users using strategy '{_strategy.Name}'...",
            _logger);

        foreach (var profile in allProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(GenerateForUser(profile, allProfiles, candidates, peopleLookup, maxResultsPerUser, _strategy, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw; // Do not swallow cancellation — propagate to caller
            }
            catch (Exception ex)
            {
                _pluginLog.LogWarning(
                    "Recommendations",
                    $"Failed to generate recommendations for user '{profile.UserName}'",
                    ex,
                    _logger);
            }
        }

        var totalRecs = results.Sum(r => r.Recommendations.Count);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Finished recommendation generation: {results.Count} users, {totalRecs} total recommendations.",
            _logger);

        return results;
    }

    /// <summary>
    ///     Loads all candidate items (movies and series) from the library.
    ///     Movies are queried directly (non-folder video items).
    ///     Series are queried separately (folder items of type Series).
    /// </summary>
    /// <returns>A list of candidate base items.</returns>
    internal List<BaseItem> LoadCandidateItems()
    {
        // Query movies directly via item type filter (avoids loading all videos and filtering in memory)
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IsFolder = false
        });

        // Query series separately (series are folders in Jellyfin)
        var series = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsFolder = true
        });

        var totalCount = movies.Count + series.Count;

        if (totalCount > CandidateCountWarningThreshold)
        {
            _pluginLog.LogWarning(
                "Recommendations",
                $"Large candidate set: {totalCount} items ({movies.Count} movies, {series.Count} series). " +
                "Consider using the scheduled task instead of on-demand generation for better performance.",
                logger: _logger);
        }

        var candidates = new List<BaseItem>(totalCount);
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
    /// <param name="peopleLookup">Pre-built people lookup (item ID → person names) for people similarity scoring.</param>
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <param name="strategy">The scoring strategy to use.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation in large libraries.</param>
    /// <returns>A recommendation result for the user.</returns>
    internal RecommendationResult GenerateForUser(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        List<BaseItem> allCandidates,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        int maxResults,
        IScoringStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        // Build a lookup of watched items by ID for O(1) access in scoring methods
        var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
        foreach (var w in userProfile.WatchedItems)
        {
            watchedItemLookup.TryAdd(w.ItemId, w);
        }

        // Build a lookup of watched episodes grouped by series ID for series-level aggregation (Fix #2)
        var seriesEpisodeLookup = new Dictionary<Guid, List<WatchedItemInfo>>();
        foreach (var w in userProfile.WatchedItems)
        {
            if (w.SeriesId.HasValue)
            {
                if (!seriesEpisodeLookup.TryGetValue(w.SeriesId.Value, out var list))
                {
                    list = new List<WatchedItemInfo>();
                    seriesEpisodeLookup[w.SeriesId.Value] = list;
                }

                list.Add(w);
            }
        }

        // Build the set of already-watched item IDs for this user (movies + episodes)
        var watchedIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));

        // Build the set of series IDs where the user has watched at least one episode
        var watchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems
                .Where(w => w.Played && w.SeriesId.HasValue)
                .Select(w => w.SeriesId!.Value));

        // Build the user's genre preference vector (normalized TF)
        var genrePreferences = BuildGenrePreferenceVector(userProfile);

        // Build O(1) candidate lookup by ID — shared across studio preference and future people lookups
        var candidateLookup = new Dictionary<Guid, BaseItem>(allCandidates.Count);
        foreach (var c in allCandidates)
        {
            candidateLookup.TryAdd(c.Id, c);
        }

        // Build the collaborative co-occurrence map
        var coOccurrence = BuildCollaborativeMap(userProfile, allProfiles);

        // Pre-compute collaborative max for normalization (performance: compute once)
        var collaborativeMax = coOccurrence.Count > 0 ? (double)coOccurrence.Values.Max() : 0;

        // Compute the user's average watched year for year-proximity scoring
        var avgYear = ComputeAverageYear(userProfile);

        // Build user's preferred studios set from watched items for StudioMatch feature (Fix #5)
        var preferredStudios = BuildStudioPreferenceSet(userProfile, candidateLookup, watchedIds, watchedSeriesIds);

        // Build user's preferred people set from watched items for PeopleSimilarity feature
        var preferredPeople = BuildPeoplePreferenceSet(userProfile, peopleLookup, watchedIds, watchedSeriesIds);

        // Score each candidate that the user has NOT watched
        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        var candidateIndex = 0;

        foreach (var candidate in allCandidates)
        {
            // Periodically check cancellation to stay responsive for large libraries
            if (++candidateIndex % CancellationCheckBatchSize == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Skip items the user has already watched directly
            if (watchedIds.Contains(candidate.Id))
            {
                continue;
            }

            // Skip fully-watched series but keep partially-watched ones for "continue watching" (Fix #3)
            if (candidate is Series && watchedSeriesIds.Contains(candidate.Id))
            {
                if (seriesEpisodeLookup.TryGetValue(candidate.Id, out var seriesEps))
                {
                    var playedCount = seriesEps.Count(e => e.Played);
                    // Only skip if user has watched ≥90% of episodes — series is effectively completed
                    if (seriesEps.Count > 0 && (double)playedCount / seriesEps.Count >= 0.9)
                    {
                        continue;
                    }
                }
                else
                {
                    // No episode data available, skip conservatively
                    continue;
                }
            }

            var genreScore = ComputeGenreSimilarity(candidate.Genres ?? [], genrePreferences);
            var collabScore = ComputeCollaborativeScore(candidate.Id, coOccurrence, collaborativeMax);
            var ratingScore = NormalizeRating(candidate.CommunityRating);
            var recencyScore = ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);
            var yearScore = ComputeYearProximity(candidate.ProductionYear, avgYear);

            // Compute user-specific signals for this candidate (O(1) via lookup)
            // For series candidates, aggregate from watched episodes (Fix #2)
            double userRatingScore;
            double completionRatio;

            bool hasUserInteraction;

            if (candidate is Series && seriesEpisodeLookup.TryGetValue(candidate.Id, out var episodesForScoring))
            {
                hasUserInteraction = true;

                // Aggregate user rating: average of rated episodes
                var ratedEpisodes = episodesForScoring.Where(e => e.UserRating is > 0).ToList();
                userRatingScore = ratedEpisodes.Count > 0
                    ? Math.Clamp(ratedEpisodes.Average(e => e.UserRating!.Value) / 10.0, 0.0, 1.0)
                    : 0.5;

                // Aggregate completion: fraction of episodes played
                var playedEps = episodesForScoring.Count(e => e.Played);
                completionRatio = episodesForScoring.Count > 0
                    ? Math.Clamp((double)playedEps / episodesForScoring.Count, 0.0, 1.0)
                    : 0.5;
            }
            else
            {
                watchedItemLookup.TryGetValue(candidate.Id, out var watchedItem);
                hasUserInteraction = watchedItem is not null;
                userRatingScore = ComputeUserRatingScore(watchedItem);
                completionRatio = hasUserInteraction ? ComputeCompletionRatio(watchedItem) : 0.5;
            }

            // Compute studio match (Fix #5)
            var studioMatch = candidate.Studios is { Length: > 0 }
                && candidate.Studios.Any(s => preferredStudios.Contains(s));

            // Compute people similarity via pre-built lookup (O(1) lookup + Jaccard)
            var peopleSimilarity = peopleLookup.TryGetValue(candidate.Id, out var candidatePeople)
                ? ComputePeopleSimilarity(candidatePeople, preferredPeople)
                : 0.0;

            // Compute series progression boost: reward next-season recommendations
            var seriesProgressionBoost = 0.0;
            if (candidate is Series candidateSeries && seriesEpisodeLookup.TryGetValue(candidateSeries.Id, out var progressionEps))
            {
                // User has watched some episodes → boost proportional to completion so they continue
                var playedEps = progressionEps.Count(e => e.Played);
                var totalEps = progressionEps.Count;
                if (totalEps > 0)
                {
                    var ratio = (double)playedEps / totalEps;
                    // Highest boost for partially-watched series (40–90% complete)
                    seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
                }
            }

            // Compute popularity score: proxy from collaborative scores (items watched by many users)
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
                DayOfWeekAffinity = ComputeDayOfWeekAffinity(candidate, userProfile),
                HourOfDayAffinity = ComputeHourOfDayAffinity(candidate, userProfile),
                IsWeekend = DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            };

            var explanation = strategy.ScoreWithExplanation(features);
            var totalScore = explanation.FinalScore;

            // Debug-log individual score breakdowns for transparency
            _pluginLog.LogDebug(
                "Recommendations",
                $"Score for '{candidate.Name}': {explanation}",
                _logger);

            // Determine the primary reason using the dominant signal from the explanation
            // This ensures the reason matches what actually drove the score (Point 9)
            var (reason, reasonKey, relatedItem) = DetermineReason(
                candidate, explanation, genrePreferences);

            scored.Add((candidate, totalScore, reason, reasonKey, relatedItem));
        }

        // Series deduplication: when the library contains individual episodes/seasons,
        // multiple items from the same series can dominate the top results.
        // Keep only the highest-scored item per series (identified by SeriesId).
        // Non-series items (movies, etc.) pass through unchanged.
        scored = DeduplicateSeries(scored);

        // Apply MMR (Maximal Marginal Relevance) diversity re-ranking.
        // This balances relevance with diversity to avoid recommending too many
        // similar items (e.g., all from the same genre cluster).
        var topItems = ApplyDiversityReranking(scored, maxResults)
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
                PrimaryImageTag = s.Item.HasImage(MediaBrowser.Model.Entities.ImageType.Primary)
                    ? s.Item.Id.ToString("N")
                    : null
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
            Profile = StripWatchedItemsForResponse(userProfile),
            Recommendations = new Collection<RecommendedItem>(topItems),
            GeneratedAt = DateTime.UtcNow,
            ScoringStrategy = strategy.Name,
            ScoringStrategyKey = strategy.NameKey
        };
    }

    /// <summary>
    ///     Builds a normalized genre preference vector from the user's watch history.
    ///     Each genre gets a weight based on how often the user has watched items of that genre.
    ///     Genres from favorited items receive an additional boost to better reflect true preferences.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A dictionary mapping genre names to normalized weights (0–1).</returns>
    internal static Dictionary<string, double> BuildGenrePreferenceVector(UserWatchProfile profile)
    {
        var vector = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (profile.GenreDistribution.Count == 0)
        {
            return vector;
        }

        // Start with the base genre distribution
        foreach (var (genre, count) in profile.GenreDistribution)
        {
            vector[genre] = count;
        }

        // Boost genres from favorited items — favorites signal strong preference
        foreach (var item in profile.WatchedItems)
        {
            if (!item.IsFavorite || item.Genres is null)
            {
                continue;
            }

            foreach (var genre in item.Genres)
            {
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    vector.TryGetValue(genre, out var current);
                    vector[genre] = current + FavoriteGenreBoostFactor;
                }
            }
        }

        // Normalize to 0–1 range
        if (vector.Count == 0)
        {
            return vector;
        }

        var maxWeight = vector.Values.Max();
        if (maxWeight <= 0)
        {
            return vector;
        }

        foreach (var genre in vector.Keys.ToList())
        {
            vector[genre] /= maxWeight;
        }

        return vector;
    }

    /// <summary>
    ///     Builds a collaborative co-occurrence map: for each unwatched item,
    ///     accumulates Jaccard-weighted similarity from OTHER users who share watch
    ///     overlap with this user. Uses true Jaccard similarity (0–1) instead of
    ///     discretized integer weights for better precision.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping item IDs to accumulated Jaccard-weighted scores.</returns>
    internal static Dictionary<Guid, double> BuildCollaborativeMap(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles)
    {
        var coOccurrence = new Dictionary<Guid, double>();
        var userWatchedIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));

        // Also include series IDs so that episode overlap can boost the parent series candidate
        var userWatchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems
                .Where(w => w.Played && w.SeriesId.HasValue)
                .Select(w => w.SeriesId!.Value));

        // Combined set for Jaccard computation (episodes + series)
        var userCombinedIds = new HashSet<Guid>(userWatchedIds);
        userCombinedIds.UnionWith(userWatchedSeriesIds);

        if (userCombinedIds.Count == 0)
        {
            return coOccurrence;
        }

        // Build a lookup of other users' watched-item sets for collaborative scoring.
        var otherUserSets = new Dictionary<Guid, HashSet<Guid>>();

        foreach (var otherProfile in allProfiles)
        {
            if (otherProfile.UserId == userProfile.UserId)
            {
                continue;
            }

            var otherCombinedIds = new HashSet<Guid>(
                otherProfile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));

            // Also include series IDs from other users' episode watches
            foreach (var w in otherProfile.WatchedItems)
            {
                if (w.Played && w.SeriesId.HasValue)
                {
                    otherCombinedIds.Add(w.SeriesId.Value);
                }
            }

            if (otherCombinedIds.Count == 0)
            {
                continue;
            }

            otherUserSets[otherProfile.UserId] = otherCombinedIds;
        }

        // Compute overlap + Jaccard per other user (cached to avoid recomputation)
        var jaccardCache = new Dictionary<Guid, double>();

        foreach (var (otherUserId, otherCombinedIds) in otherUserSets)
        {
            var overlap = 0;
            foreach (var id in userCombinedIds)
            {
                if (otherCombinedIds.Contains(id))
                {
                    overlap++;
                }
            }

            if (overlap < MinCollaborativeOverlap)
            {
                continue;
            }

            var union = userCombinedIds.Count + otherCombinedIds.Count - overlap;
            var weight = union > 0 ? (double)overlap / union : 0.0;
            jaccardCache[otherUserId] = weight;

            // Accumulate Jaccard-weighted co-occurrence for items the other user watched but we haven't
            // This now includes both episode IDs AND series IDs, so series candidates get collaborative scores
            foreach (var itemId in otherCombinedIds)
            {
                if (!userCombinedIds.Contains(itemId))
                {
                    coOccurrence.TryGetValue(itemId, out var current);
                    coOccurrence[itemId] = current + weight;
                }
            }
        }

        return coOccurrence;
    }

    /// <summary>
    ///     Computes genre similarity between a candidate item and the user's genre preference vector
    ///     using cosine similarity. This properly handles multi-genre items (e.g. Action + SciFi + Adventure)
    ///     without penalizing them for having many genres.
    /// </summary>
    /// <param name="candidateGenres">The genres of the candidate item.</param>
    /// <param name="genrePreferences">The user's genre preference vector.</param>
    /// <returns>A similarity score between 0 and 1.</returns>
    internal static double ComputeGenreSimilarity(
        IReadOnlyList<string> candidateGenres,
        Dictionary<string, double> genrePreferences)
    {
        if (candidateGenres.Count == 0 || genrePreferences.Count == 0)
        {
            return 0;
        }

        // Cosine similarity: dot(candidate, user) / (|candidate| * |user|)
        // Candidate vector: 1.0 for each genre present, 0.0 otherwise
        // User vector: preference weight for each genre
        var dotProduct = 0.0;
        foreach (var genre in candidateGenres)
        {
            if (genrePreferences.TryGetValue(genre, out var weight))
            {
                dotProduct += weight; // candidate component is 1.0
            }
        }

        if (dotProduct <= 0)
        {
            return 0;
        }

        // |candidate| = sqrt(number of genres) since each component is 1.0
        var candidateNorm = Math.Sqrt(candidateGenres.Count);

        // |user| = sqrt(sum of squared weights)
        var userNormSq = 0.0;
        foreach (var weight in genrePreferences.Values)
        {
            userNormSq += weight * weight;
        }

        var userNorm = Math.Sqrt(userNormSq);

        if (candidateNorm <= 0 || userNorm <= 0)
        {
            return 0;
        }

        return Math.Min(dotProduct / (candidateNorm * userNorm), 1.0);
    }

    /// <summary>
    ///     Returns a normalized collaborative score (0–1) for a candidate item.
    /// </summary>
    /// <param name="itemId">The candidate item ID.</param>
    /// <param name="coOccurrence">The collaborative co-occurrence map.</param>
    /// <param name="maxCoOccurrence">The pre-computed maximum co-occurrence value.</param>
    /// <returns>A normalized score between 0 and 1.</returns>
    internal static double ComputeCollaborativeScore(Guid itemId, Dictionary<Guid, double> coOccurrence, double maxCoOccurrence)
    {
        if (maxCoOccurrence <= 0 || !coOccurrence.TryGetValue(itemId, out var count))
        {
            return 0;
        }

        return count / maxCoOccurrence;
    }

    /// <summary>
    ///     Normalizes a community rating (typically 0–10) to a 0–1 score.
    /// </summary>
    /// <param name="communityRating">The community rating value.</param>
    /// <returns>A normalized rating between 0 and 1.</returns>
    internal static double NormalizeRating(float? communityRating)
    {
        if (!communityRating.HasValue || communityRating.Value <= 0)
        {
            return 0.5; // neutral default for unrated items
        }

        return Math.Min(communityRating.Value / 10.0, 1.0);
    }

    /// <summary>
    ///     Computes a recency score based on how recently the item was added or premiered.
    ///     Newer items get a slight boost.
    /// </summary>
    /// <param name="itemDate">The item's premiere or creation date.</param>
    /// <param name="now">
    ///     Reference point for "now" (defaults to <see cref="DateTime.UtcNow"/>).
    ///     Exposed for deterministic unit testing.
    /// </param>
    /// <returns>A recency score between 0 and 1.</returns>
    internal static double ComputeRecencyScore(DateTime itemDate, DateTime? now = null)
    {
        var ageInDays = ((now ?? DateTime.UtcNow) - itemDate).TotalDays;
        if (ageInDays <= 0)
        {
            return 1.0;
        }

        // Exponential decay: half-life of ~365 days
        return Math.Exp(-RecencyDecayConstant * ageInDays);
    }

    /// <summary>
    ///     Computes year proximity score: items closer to the user's average watched year score higher.
    /// </summary>
    /// <param name="candidateYear">The candidate item's production year.</param>
    /// <param name="averageYear">The user's average watched production year.</param>
    /// <returns>A proximity score between 0 and 1.</returns>
    internal static double ComputeYearProximity(int? candidateYear, double averageYear)
    {
        if (!candidateYear.HasValue || averageYear <= 0)
        {
            return 0.5; // neutral default
        }

        var diff = Math.Abs(candidateYear.Value - averageYear);

        // Gaussian-like decay with σ ≈ 10 years
        return Math.Exp(-diff * diff / YearProximityDenominator);
    }

    /// <summary>
    ///     Computes a normalized user rating score (0–1) for a candidate item.
    ///     If the user has not rated this item, returns 0.5 (neutral).
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A normalized user rating between 0 and 1.</returns>
    internal static double ComputeUserRatingScore(WatchedItemInfo? watchedItem)
    {
        if (watchedItem?.UserRating is null or <= 0)
        {
            return 0.5; // neutral default — no user rating available
        }

        // User ratings are typically 0–10, normalize to 0–1
        return Math.Clamp(watchedItem.UserRating.Value / 10.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the watch completion ratio for a candidate item.
    ///     Returns 0 if the user has never started the item (new candidate),
    ///     or a ratio of played ticks to runtime ticks for partially watched items.
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A completion ratio between 0 and 1.</returns>
    internal static double ComputeCompletionRatio(WatchedItemInfo? watchedItem)
    {
        if (watchedItem is null || watchedItem.RuntimeTicks <= 0)
        {
            return 0.0; // not started or no runtime info — neutral for candidates
        }

        return Math.Clamp((double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the average production year from the user's watched items.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>The average production year, or 0 if no years are available.</returns>
    internal static double ComputeAverageYear(UserWatchProfile profile)
    {
        long sum = 0;
        var count = 0;

        foreach (var w in profile.WatchedItems)
        {
            if (w.Played && w.Year.HasValue)
            {
                sum += w.Year.Value;
                count++;
            }
        }

        return count > 0 ? (double)sum / count : 0;
    }

    /// <summary>
    ///     Deduplicates series entries: when episodes or seasons from the same series
    ///     appear as separate candidates, keeps only the highest-scored entry per series.
    ///     Non-series items (movies, etc.) are passed through unchanged.
    /// </summary>
    /// <param name="scored">The scored candidate list (may contain duplicate series).</param>
    /// <returns>A deduplicated list with at most one entry per series.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        DeduplicateSeries(
            List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> scored)
    {
        var result = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(scored.Count);
        var bestPerSeries = new Dictionary<Guid, int>(); // seriesId → index in result

        foreach (var entry in scored)
        {
            // Determine the series ID: for Episode/Season use the parent series ID,
            // for Series items use their own ID.
            Guid? seriesId = entry.Item switch
            {
                Episode ep => ep.SeriesId != Guid.Empty ? ep.SeriesId : null,
                Season season => season.SeriesId != Guid.Empty ? season.SeriesId : null,
                Series s => s.Id,
                _ => null
            };

            if (seriesId is null)
            {
                // Non-series item (movie, etc.) — always include
                result.Add(entry);
                continue;
            }

            if (bestPerSeries.TryGetValue(seriesId.Value, out var existingIdx))
            {
                // Keep the higher-scored entry
                if (entry.Score > result[existingIdx].Score)
                {
                    result[existingIdx] = entry;
                }
            }
            else
            {
                bestPerSeries[seriesId.Value] = result.Count;
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    ///     Applies MMR (Maximal Marginal Relevance) re-ranking to balance relevance with diversity.
    ///     This greedily selects items that maximize: λ × relevance - (1 - λ) × max_similarity_to_selected.
    ///     Genre-set Jaccard similarity is used as the inter-item similarity measure.
    /// </summary>
    /// <param name="candidates">All scored candidates.</param>
    /// <param name="count">Number of items to select.</param>
    /// <returns>The diversity-reranked top items.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>
        ApplyDiversityReranking(
            List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> candidates,
            int count)
    {
        if (candidates.Count <= count)
        {
            return candidates.OrderByDescending(c => c.Score).ToList();
        }

        // Pre-filter to top 3× candidates for efficiency (MMR is O(n×k))
        var pool = candidates.OrderByDescending(c => c.Score).Take(count * 3).ToList();
        var selected = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(count);
        var remaining = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(pool);

        // Cache genre HashSets to avoid repeated allocation per Jaccard call (Point 10)
        var genreSetCache = new Dictionary<Guid, HashSet<string>>();
        HashSet<string> GetOrCreateGenreSet(BaseItem item)
        {
            if (!genreSetCache.TryGetValue(item.Id, out var set))
            {
                set = item.Genres is { Length: > 0 }
                    ? new HashSet<string>(item.Genres, StringComparer.OrdinalIgnoreCase)
                    : [];
                genreSetCache[item.Id] = set;
            }

            return set;
        }

        while (selected.Count < count && remaining.Count > 0)
        {
            var bestIdx = -1;
            var bestMmrScore = double.MinValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var relevance = remaining[i].Score;
                var candidateSet = GetOrCreateGenreSet(remaining[i].Item);

                // Compute max similarity to any already-selected item
                var maxSimilarity = 0.0;
                foreach (var sel in selected)
                {
                    var selectedSet = GetOrCreateGenreSet(sel.Item);
                    var sim = ComputeJaccardFromSets(candidateSet, selectedSet);
                    if (sim > maxSimilarity)
                    {
                        maxSimilarity = sim;
                    }
                }

                // MMR score: λ × relevance - (1 - λ) × max_similarity
                var mmrScore = (MmrLambda * relevance) - ((1.0 - MmrLambda) * maxSimilarity);

                if (mmrScore > bestMmrScore)
                {
                    bestMmrScore = mmrScore;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                selected.Add(remaining[bestIdx]);

                // Swap-remove: O(1) instead of O(n) for RemoveAt in the middle of a list.
                // Order of remaining doesn't matter since we scan all elements each iteration.
                var lastIdx = remaining.Count - 1;
                if (bestIdx < lastIdx)
                {
                    remaining[bestIdx] = remaining[lastIdx];
                }

                remaining.RemoveAt(lastIdx);
            }
            else
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>
    ///     Computes Jaccard similarity from pre-built HashSets (avoids repeated allocation).
    ///     Used by the MMR loop where genre sets are cached.
    /// </summary>
    /// <param name="setA">First genre set.</param>
    /// <param name="setB">Second genre set.</param>
    /// <returns>Jaccard similarity (0–1).</returns>
    internal static double ComputeJaccardFromSets(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        // Iterate over the smaller set for efficiency
        var (smaller, larger) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        foreach (var g in smaller)
        {
            if (larger.Contains(g))
            {
                intersection++;
            }
        }

        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }

    /// <summary>
    ///     Builds a set of studio names the user prefers, derived from their watched items.
    ///     Looks up the actual BaseItem objects from the candidate lookup to access Studios metadata.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="candidateLookup">Pre-built candidate lookup by item ID (shared across calls for performance).</param>
    /// <param name="watchedIds">Set of item IDs the user has watched.</param>
    /// <param name="watchedSeriesIds">Set of series IDs the user has watched episodes of.</param>
    /// <returns>A HashSet of preferred studio names (case-insensitive).</returns>
    internal static HashSet<string> BuildStudioPreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, BaseItem> candidateLookup,
        HashSet<Guid> watchedIds,
        HashSet<Guid> watchedSeriesIds)
    {
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect studios from watched movies and series
        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played)
            {
                continue;
            }

            // Try direct item match (movies)
            if (candidateLookup.TryGetValue(w.ItemId, out var item) && item.Studios is { Length: > 0 })
            {
                foreach (var s in item.Studios)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        studios.Add(s);
                    }
                }
            }

            // Also try series match (episodes → parent series)
            if (w.SeriesId.HasValue && candidateLookup.TryGetValue(w.SeriesId.Value, out var seriesItem)
                && seriesItem.Studios is { Length: > 0 })
            {
                foreach (var s in seriesItem.Studios)
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
    ///     Batch-loads people (actors/directors) for all candidate items into a lookup dictionary.
    ///     Called once per recommendation run and shared across all users for performance.
    ///     Only stores person names for relevant types (Actor, Director) to keep memory compact.
    /// </summary>
    /// <param name="candidates">All candidate base items.</param>
    /// <returns>A dictionary mapping item IDs to their associated person name sets (case-insensitive).</returns>
    internal Dictionary<Guid, HashSet<string>> BuildCandidatePeopleLookup(List<BaseItem> candidates)
    {
        var lookup = new Dictionary<Guid, HashSet<string>>(candidates.Count);

        foreach (var candidate in candidates)
        {
            try
            {
                var people = _libraryManager.GetPeople(candidate);
                if (people is null || people.Count == 0)
                {
                    continue;
                }

                HashSet<string>? names = null;
                foreach (var person in people)
                {
                    if (string.IsNullOrWhiteSpace(person.Name))
                    {
                        continue;
                    }

                    // Only include actors and directors — other types add noise without predictive value
                    if (!Array.Exists(RelevantPersonKinds, k => k == person.Type))
                    {
                        continue;
                    }

                    names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names.Add(person.Name);
                }

                if (names is { Count: > 0 })
                {
                    lookup[candidate.Id] = names;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Do not swallow cancellation — propagate to caller
            }
            catch (Exception ex)
            {
                // Graceful fallback: skip this candidate's people data rather than failing the entire lookup.
                // Some item types or corrupted metadata may cause GetPeople to throw.
                _logger.LogDebug(ex, "Failed to load people for candidate {ItemId}, skipping", candidate.Id);
            }
        }

        _pluginLog.LogDebug(
            "Recommendations",
            $"Built people lookup for {lookup.Count}/{candidates.Count} candidates.",
            _logger);

        return lookup;
    }

    /// <summary>
    ///     Builds a set of preferred person names (actors/directors) from the user's watched items.
    ///     Uses the pre-built people lookup to avoid additional library queries.
    ///     Includes people from both directly watched items and series the user has watched episodes of.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Pre-built candidate people lookup (item ID → person names).</param>
    /// <param name="watchedIds">Set of item IDs the user has watched.</param>
    /// <param name="watchedSeriesIds">Set of series IDs the user has watched episodes of.</param>
    /// <returns>A HashSet of preferred person names (case-insensitive).</returns>
    internal static HashSet<string> BuildPeoplePreferenceSet(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        HashSet<Guid> watchedIds,
        HashSet<Guid> watchedSeriesIds)
    {
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played)
            {
                continue;
            }

            // Direct item match (movies, episodes)
            if (peopleLookup.TryGetValue(w.ItemId, out var itemPeople))
            {
                people.UnionWith(itemPeople);
            }

            // Series match (episodes → parent series)
            if (w.SeriesId.HasValue && peopleLookup.TryGetValue(w.SeriesId.Value, out var seriesPeople))
            {
                people.UnionWith(seriesPeople);
            }
        }

        return people;
    }

    /// <summary>
    ///     Computes people similarity between a candidate's cast/directors and the user's
    ///     preferred people set using Overlap coefficient: |A ∩ B| / min(|A|, |B|).
    ///     This is preferred over Jaccard for people similarity because the user's preferred
    ///     people set is typically much larger than a single candidate's cast, which would
    ///     make Jaccard converge towards zero. Overlap coefficient focuses on what fraction
    ///     of the smaller set is shared, giving a meaningful signal.
    /// </summary>
    /// <param name="candidatePeople">The candidate item's person names.</param>
    /// <param name="preferredPeople">The user's preferred person names.</param>
    /// <returns>An overlap coefficient between 0 and 1.</returns>
    internal static double ComputePeopleSimilarity(
        HashSet<string> candidatePeople,
        HashSet<string> preferredPeople)
    {
        if (candidatePeople.Count == 0 || preferredPeople.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        // Iterate over the smaller set for efficiency
        var (smaller, larger) = candidatePeople.Count <= preferredPeople.Count
            ? (candidatePeople, preferredPeople)
            : (preferredPeople, candidatePeople);
        foreach (var name in smaller)
        {
            if (larger.Contains(name))
            {
                intersection++;
            }
        }

        var minSize = Math.Min(candidatePeople.Count, preferredPeople.Count);
        return minSize > 0 ? (double)intersection / minSize : 0;
    }

    /// <summary>
    ///     Computes day-of-week affinity: how well a candidate's genre matches
    ///     the user's viewing patterns for the current day of week.
    ///     Returns 0.5 (neutral) if insufficient data.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <returns>An affinity score between 0 and 1.</returns>
    internal static double ComputeDayOfWeekAffinity(BaseItem candidate, UserWatchProfile userProfile)
    {
        if (candidate.Genres is not { Length: > 0 } || userProfile.WatchedItems.Count < 10)
        {
            return 0.5; // not enough data
        }

        var today = DateTime.UtcNow.DayOfWeek;

        // Count how many watched items with matching genres were watched on this day of week
        var matchCount = 0;
        var totalToday = 0;
        var candidateGenreSet = new HashSet<string>(candidate.Genres, StringComparer.OrdinalIgnoreCase);

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            if (w.LastPlayedDate.Value.DayOfWeek != today)
            {
                continue;
            }

            totalToday++;
            if (w.Genres is not null && w.Genres.Any(g => candidateGenreSet.Contains(g)))
            {
                matchCount++;
            }
        }

        if (totalToday < 3)
        {
            return 0.5; // not enough data for this day
        }

        return Math.Clamp((double)matchCount / totalToday, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes hour-of-day affinity: how well a candidate's genre matches
    ///     the user's viewing patterns for the current time-of-day bucket.
    ///     Uses 4 buckets: morning (6–12), afternoon (12–18), evening (18–24), night (0–6).
    ///     Returns 0.5 (neutral) if insufficient data.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <returns>An affinity score between 0 and 1.</returns>
    internal static double ComputeHourOfDayAffinity(BaseItem candidate, UserWatchProfile userProfile)
    {
        if (candidate.Genres is not { Length: > 0 } || userProfile.WatchedItems.Count < 10)
        {
            return 0.5; // not enough data
        }

        var currentHour = DateTime.UtcNow.Hour;
        var currentBucket = GetTimeBucket(currentHour);
        var candidateGenreSet = new HashSet<string>(candidate.Genres, StringComparer.OrdinalIgnoreCase);

        var matchCount = 0;
        var totalInBucket = 0;

        foreach (var w in userProfile.WatchedItems)
        {
            if (!w.Played || !w.LastPlayedDate.HasValue)
            {
                continue;
            }

            if (GetTimeBucket(w.LastPlayedDate.Value.Hour) != currentBucket)
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
            return 0.5; // not enough data for this time bucket
        }

        return Math.Clamp((double)matchCount / totalInBucket, 0.0, 1.0);
    }

    /// <summary>
    ///     Maps an hour (0–23) to a time-of-day bucket for temporal affinity computation.
    ///     Buckets: 0 = night (0–5), 1 = morning (6–11), 2 = afternoon (12–17), 3 = evening (18–23).
    /// </summary>
    /// <param name="hour">The hour of day (0–23).</param>
    /// <returns>A bucket index (0–3).</returns>
    internal static int GetTimeBucket(int hour) => hour switch
    {
        < 6 => 0,   // night
        < 12 => 1,  // morning
        < 18 => 2,  // afternoon
        _ => 3      // evening
    };

    /// <summary>
    ///     Determines the most relevant human-readable reason for a recommendation
    ///     based on the dominant signal from the score explanation.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) DetermineReason(
        BaseItem candidate,
        ScoreExplanation explanation,
        Dictionary<string, double> genrePreferences)
    {
        var dominant = explanation.DominantSignal;

        if (string.Equals(dominant, "Collaborative", StringComparison.OrdinalIgnoreCase)
            && explanation.CollaborativeContribution > ReasonScoreThreshold)
        {
            return ("Popular with similar viewers", "reasonCollaborative", null);
        }

        if (string.Equals(dominant, "Genre", StringComparison.OrdinalIgnoreCase)
            && explanation.GenreContribution > ReasonScoreThreshold
            && candidate.Genres is { Length: > 0 })
        {
            var topGenre = candidate.Genres
                .Where(g => genrePreferences.ContainsKey(g))
                .OrderByDescending(g => genrePreferences.GetValueOrDefault(g, 0))
                .FirstOrDefault();

            if (topGenre is not null)
            {
                return ($"Because you enjoy {topGenre}", "reasonGenre", topGenre);
            }
        }

        if (string.Equals(dominant, "Rating", StringComparison.OrdinalIgnoreCase)
            && explanation.RatingContribution > HighRatingThreshold)
        {
            return ("Highly rated", "reasonHighlyRated", null);
        }

        if (string.Equals(dominant, "UserRating", StringComparison.OrdinalIgnoreCase))
        {
            return ("Matches your personal ratings", "reasonUserRating", null);
        }

        if (string.Equals(dominant, "Recency", StringComparison.OrdinalIgnoreCase))
        {
            return ("Recently released", "reasonRecent", null);
        }

        if (string.Equals(dominant, "Interaction", StringComparison.OrdinalIgnoreCase)
            && explanation.InteractionContribution > ReasonScoreThreshold)
        {
            return ("Matches your viewing patterns", "reasonInteraction", null);
        }

        if (string.Equals(dominant, "People", StringComparison.OrdinalIgnoreCase))
        {
            return ("Features actors/directors you enjoy", "reasonPeople", null);
        }

        if (string.Equals(dominant, "Studio", StringComparison.OrdinalIgnoreCase))
        {
            return ("From a studio you enjoy", "reasonStudio", null);
        }

        return ("Recommended for you", "reasonDefault", null);
    }

    /// <summary>
    ///     Creates a copy of the profile without the full watched items list (for the API response),
    ///     keeping only aggregated stats.
    /// </summary>
    /// <param name="profile">The original user watch profile.</param>
    /// <returns>A copy of the profile with an empty watched items list.</returns>
    private static UserWatchProfile StripWatchedItemsForResponse(UserWatchProfile profile)
    {
        return new UserWatchProfile
        {
            UserId = profile.UserId,
            UserName = profile.UserName,
            WatchedMovieCount = profile.WatchedMovieCount,
            WatchedEpisodeCount = profile.WatchedEpisodeCount,
            WatchedSeriesCount = profile.WatchedSeriesCount,
            TotalWatchTimeTicks = profile.TotalWatchTimeTicks,
            LastActivityDate = profile.LastActivityDate,
            GenreDistribution = profile.GenreDistribution,
            FavoriteCount = profile.FavoriteCount,
            AverageCommunityRating = profile.AverageCommunityRating,
            WatchedItems = [] // Don't send the full list to the client
        };
    }
}