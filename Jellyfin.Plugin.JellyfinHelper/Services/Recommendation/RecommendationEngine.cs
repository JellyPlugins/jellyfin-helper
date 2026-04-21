using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
    ///     Minimum collaborative or genre score before a specific reason is shown.
    /// </summary>
    internal const double ReasonScoreThreshold = 0.15;

    /// <summary>
    ///     Minimum rating score before "Highly rated" reason is shown.
    /// </summary>
    internal const double HighRatingThreshold = 0.7;

    /// <summary>
    ///     Genre similarity threshold below which items receive a penalty multiplier.
    ///     Items with no meaningful genre overlap with the user's preferences are penalized.
    /// </summary>
    internal const double GenreMismatchThreshold = 0.1;

    /// <summary>
    ///     Penalty multiplier applied to items below the genre mismatch threshold.
    ///     Strongly reduces the score of items that don't match the user's genre preferences.
    /// </summary>
    internal const double GenreMismatchPenalty = 0.15;

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

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RecommendationEngine> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly IWatchHistoryService _watchHistoryService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationEngine" /> class.
    /// </summary>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public RecommendationEngine(
        IWatchHistoryService watchHistoryService,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<RecommendationEngine> logger)
    {
        _watchHistoryService = watchHistoryService;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public RecommendationResult? GetRecommendations(Guid userId, int maxResults = 20)
    {
        maxResults = Math.Clamp(maxResults, 1, MaxRecommendationsPerUser);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var userProfile = allProfiles.FirstOrDefault(p => p.UserId == userId);
        if (userProfile is null)
        {
            return null;
        }

        // Load candidates once
        var candidates = LoadCandidateItems();
        var strategy = ResolveStrategy();
        return GenerateForUser(userProfile, allProfiles, candidates, maxResults, strategy);
    }

    /// <inheritdoc />
    public bool TrainStrategy(Collection<RecommendationResult> previousResults)
    {
        var strategy = ResolveStrategy();
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

            foreach (var rec in prevResult.Recommendations)
            {
                // Determine label: did the user watch this item since it was recommended?
                var wasWatched = watchedIds.Contains(rec.ItemId)
                    || (watchedSeriesIds?.Contains(rec.ItemId) ?? false);

                // Recompute features for this candidate (scores may have shifted)
                var features = new CandidateFeatures
                {
                    GenreSimilarity = ComputeGenreSimilarity(rec.Genres ?? [], genrePreferences),
                    CollaborativeScore = ComputeCollaborativeScore(rec.ItemId, coOccurrence, collaborativeMax),
                    RatingScore = NormalizeRating(rec.CommunityRating),
                    RecencyScore = rec.Year.HasValue
                        ? ComputeYearProximity(rec.Year, avgYear)
                        : 0.5,
                    YearProximityScore = ComputeYearProximity(rec.Year, avgYear),
                    GenreCount = rec.Genres?.Length ?? 0,
                    IsSeries = string.Equals(rec.ItemType, "Series", StringComparison.OrdinalIgnoreCase)
                };

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = wasWatched ? 1.0 : 0.0
                });
            }
        }

        var positiveCount = examples.Count(e => e.Label > 0.5);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users.",
            _logger);

        var trained = strategy.Train(examples);

        if (trained)
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Strategy '{strategy.Name}' training completed successfully.",
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

    /// <inheritdoc />
    public Collection<RecommendationResult> GetAllRecommendations(int maxResultsPerUser = 20)
    {
        maxResultsPerUser = Math.Clamp(maxResultsPerUser, 1, MaxRecommendationsPerUser);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var results = new Collection<RecommendationResult>();

        // Load candidates once for ALL users (performance optimization)
        var candidates = LoadCandidateItems();
        var strategy = ResolveStrategy();

        _pluginLog.LogInfo(
            "Recommendations",
            $"Starting recommendation generation for {allProfiles.Count} users using strategy '{strategy.Name}'...",
            _logger);

        foreach (var profile in allProfiles)
        {
            try
            {
                results.Add(GenerateForUser(profile, allProfiles, candidates, maxResultsPerUser, strategy));
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
    ///     Resolves the scoring strategy. Uses an ensemble strategy that combines
    ///     the adaptive learned (ML) model with the rule-based heuristic model
    ///     for more robust and accurate recommendations.
    /// </summary>
    /// <returns>The ensemble scoring strategy.</returns>
    internal static IScoringStrategy ResolveStrategy()
    {
        return CreateEnsembleStrategy();
    }

    /// <summary>
    ///     Creates an <see cref="EnsembleScoringStrategy"/> with the appropriate weights path.
    ///     The ensemble combines learned (adaptive ML) and heuristic (rule-based) scoring.
    /// </summary>
    /// <returns>A configured ensemble scoring strategy.</returns>
    internal static EnsembleScoringStrategy CreateEnsembleStrategy()
    {
        var dataPath = Plugin.Instance?.DataFolderPath;
        string? weightsPath = null;
        if (!string.IsNullOrEmpty(dataPath))
        {
            weightsPath = System.IO.Path.Join(dataPath, "ml_weights.json");
        }

        return new EnsembleScoringStrategy(weightsPath);
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
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <param name="strategy">The scoring strategy to use.</param>
    /// <returns>A recommendation result for the user.</returns>
    internal RecommendationResult GenerateForUser(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        List<BaseItem> allCandidates,
        int maxResults,
        IScoringStrategy strategy)
    {
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

        // Build the collaborative co-occurrence map
        var coOccurrence = BuildCollaborativeMap(userProfile, allProfiles);

        // Pre-compute collaborative max for normalization (performance: compute once)
        var collaborativeMax = coOccurrence.Count > 0 ? (double)coOccurrence.Values.Max() : 0;

        // Compute the user's average watched year for year-proximity scoring
        var avgYear = ComputeAverageYear(userProfile);

        // Score each candidate that the user has NOT watched
        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();

        foreach (var candidate in allCandidates)
        {
            // Skip items the user has already watched directly
            if (watchedIds.Contains(candidate.Id))
            {
                continue;
            }

            // Skip series where user has already watched episodes
            if (candidate is Series && watchedSeriesIds.Contains(candidate.Id))
            {
                continue;
            }

            var genreScore = ComputeGenreSimilarity(candidate.Genres ?? [], genrePreferences);
            var collabScore = ComputeCollaborativeScore(candidate.Id, coOccurrence, collaborativeMax);
            var ratingScore = NormalizeRating(candidate.CommunityRating);
            var recencyScore = ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);
            var yearScore = ComputeYearProximity(candidate.ProductionYear, avgYear);

            // Build feature vector and delegate scoring to strategy
            var features = new CandidateFeatures
            {
                GenreSimilarity = genreScore,
                CollaborativeScore = collabScore,
                RatingScore = ratingScore,
                RecencyScore = recencyScore,
                YearProximityScore = yearScore,
                GenreCount = candidate.Genres?.Length ?? 0,
                IsSeries = candidate is Series
            };

            var totalScore = strategy.Score(features);

            // Determine the primary reason
            var (reason, reasonKey, relatedItem) = DetermineReason(
                candidate, genreScore, collabScore, ratingScore, genrePreferences);

            scored.Add((candidate, totalScore, reason, reasonKey, relatedItem));
        }

        // Take top results
        var topItems = scored
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
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
    ///     how many OTHER users who share watch overlap with this user also watched it.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping item IDs to co-occurrence counts.</returns>
    internal static Dictionary<Guid, int> BuildCollaborativeMap(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles)
    {
        var coOccurrence = new Dictionary<Guid, int>();
        var userWatchedIds = new HashSet<Guid>(
            userProfile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));

        if (userWatchedIds.Count == 0)
        {
            return coOccurrence;
        }

        foreach (var otherProfile in allProfiles)
        {
            if (otherProfile.UserId == userProfile.UserId)
            {
                continue;
            }

            var otherWatchedIds = new HashSet<Guid>(
                otherProfile.WatchedItems.Where(w => w.Played).Select(w => w.ItemId));

            // Compute overlap: how many items did both users watch?
            var overlap = userWatchedIds.Count(id => otherWatchedIds.Contains(id));

            // Only consider users with meaningful overlap
            if (overlap < MinCollaborativeOverlap)
            {
                continue;
            }

            // For each item the other user watched that this user hasn't, add co-occurrence
            foreach (var itemId in otherWatchedIds)
            {
                if (!userWatchedIds.Contains(itemId))
                {
                    coOccurrence.TryGetValue(itemId, out var count);
                    coOccurrence[itemId] = count + 1;
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
        string[] candidateGenres,
        Dictionary<string, double> genrePreferences)
    {
        if (candidateGenres.Length == 0 || genrePreferences.Count == 0)
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
        var candidateNorm = Math.Sqrt(candidateGenres.Length);

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
    internal static double ComputeCollaborativeScore(Guid itemId, Dictionary<Guid, int> coOccurrence, double maxCoOccurrence)
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
    /// <returns>A recency score between 0 and 1.</returns>
    internal static double ComputeRecencyScore(DateTime itemDate)
    {
        var ageInDays = (DateTime.UtcNow - itemDate).TotalDays;
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
    ///     Computes the average production year from the user's watched items.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>The average production year, or 0 if no years are available.</returns>
    internal static double ComputeAverageYear(UserWatchProfile profile)
    {
        var years = profile.WatchedItems
            .Where(w => w.Played && w.Year.HasValue)
            .Select(w => w.Year!.Value)
            .ToList();

        return years.Count > 0 ? years.Average() : 0;
    }

    /// <summary>
    ///     Determines the most relevant human-readable reason for a recommendation.
    /// </summary>
    private static (string Reason, string ReasonKey, string? RelatedItem) DetermineReason(
        BaseItem candidate,
        double genreScore,
        double collabScore,
        double ratingScore,
        Dictionary<string, double> genrePreferences)
    {
        // If collaborative score dominates
        if (collabScore > genreScore && collabScore > ReasonScoreThreshold)
        {
            return ("Popular with similar viewers", "reasonCollaborative", null);
        }

        // If genre score dominates, find the top matching genre
        if (genreScore > ReasonScoreThreshold && candidate.Genres is { Length: > 0 })
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

        // If high rating
        if (ratingScore > HighRatingThreshold)
        {
            return ("Highly rated", "reasonHighlyRated", null);
        }

        // Default
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