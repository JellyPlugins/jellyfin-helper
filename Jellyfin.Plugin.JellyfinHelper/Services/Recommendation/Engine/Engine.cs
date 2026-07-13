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
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
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
    private readonly SimilarityComputer _similarityComputer;
    private readonly IScoringStrategy _strategy;
    private readonly IStrategySelector _strategySelector;
    private readonly TrainingService _trainingService;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IDiscoveryFeedbackStore _discoveryFeedbackStore;

    // Short-lived cache - populated during GetAllRecommendations and reused by on-demand
    // GetRecommendations calls until next batch run invalidates it.
    // Stored as a single immutable snapshot to prevent concurrent readers from mixing data across batches.
    private volatile CandidateSnapshot? _cachedSnapshot;

    /// <summary>Initializes a new instance of the <see cref="Engine" /> class.</summary>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="strategy">The scoring strategy resolved via DI.</param>
    /// <param name="strategySelector">The strategy selector for A/B testing.</param>
    /// <param name="discoveryFeedbackStore">The discovery feedback store for training data enrichment.</param>
    public Engine(
        IWatchHistoryService watchHistoryService,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<Engine> logger,
        IScoringStrategy strategy,
        IStrategySelector strategySelector,
        IDiscoveryFeedbackStore discoveryFeedbackStore)
    {
        _watchHistoryService = watchHistoryService;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _strategy = strategy;
        _strategySelector = strategySelector;
        _discoveryFeedbackStore = discoveryFeedbackStore;
        _similarityComputer = new SimilarityComputer(libraryManager, pluginLog, logger);
        _trainingService = new TrainingService(watchHistoryService, discoveryFeedbackStore, pluginLog, logger);
    }

    /// <inheritdoc />
    public RecommendationResult? GetRecommendations(
        Guid userId,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResults = Math.Clamp(maxResults, 1, EngineConstants.MaxRecommendationsPerUserLimit);

        var userProfile = _watchHistoryService.GetUserWatchProfile(userId);
        if (userProfile is null)
        {
            // User not found in any watch profile - return null so the controller can 404.
            return null;
        }

        if (userProfile.WatchedItems.Count == 0)
        {
            // Cold-start: user exists but has no watch history - return popular/trending items.
            // Reuse cached candidates from the last batch run if available to avoid redundant library queries.
            // On-demand single-user path does not have access to a community-popularity map,
            // so cold-start falls back to the classic rating + recency formula automatically.
            return GenerateColdStartRecommendations(
                userId,
                maxResults,
                userProfile.UserName,
                _cachedSnapshot?.Candidates,
                userProfile.MaxParentalRating,
                userProfile,
                communityPopularity: null,
                cancellationToken: cancellationToken);
        }

        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();

        // Reuse cached candidates/people/boxSets from last batch run if available, otherwise load fresh
        var snapshot = _cachedSnapshot;
        var candidates = snapshot?.Candidates ?? LoadCandidateItems();
        var peopleLookup = snapshot?.PeopleLookup ?? _similarityComputer.BuildCandidatePeopleLookup(candidates);
        var boxSetLookup = snapshot?.CandidateBoxSetLookup ?? BuildCandidateBoxSetLookupFresh(candidates);
        var alphaOffset = _strategySelector.GetAlphaOffset(userProfile.UserId);
        return GenerateForUser(
            userProfile,
            allProfiles,
            candidates,
            peopleLookup,
            boxSetLookup,
            maxResults,
            _strategy,
            null,
            alphaOffset,
            cancellationToken);
    }

    /// <inheritdoc />
    public bool TrainStrategy(
        IReadOnlyList<RecommendationResult> previousResults,
        bool incremental = false,
        CancellationToken cancellationToken = default)
    {
        // Before training, update discovery feedback "Requested + Watched" status.
        // Resolves TMDb provider IDs from library items and cross-references with user watch history
        // to detect when a previously-requested discovery item has been added to the library and watched.
        // This upgrades the training label from 0.75 (Requested) to 0.90 (RequestedAndWatched).
        UpdateDiscoveryWatchedStatus(cancellationToken);

        var trained = _trainingService.Train(_strategy, previousResults, incremental, cancellationToken);

        // After training, apply cohort-based feedback to adapt the sigmoid midpoint.
        // This compares watch-rates across exploration cohorts and shifts the midpoint
        // to calibrate how quickly the system trusts the ML model.
        if (trained && _strategy is EnsembleScoringStrategy ensemble && previousResults.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Build per-user watched-item lookup from current watch profiles.
            // This captures which previously-recommended items users have since watched.
            // Includes series-level IDs (from episode SeriesId and FavoriteSeriesIds) so that
            // series-type recommendations are correctly counted as "watched" when the user
            // watched episodes of that series.
            var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
            var watchedItemLookup = new Dictionary<Guid, HashSet<Guid>>(allProfiles.Count);
            foreach (var profile in allProfiles)
            {
                var watched = new HashSet<Guid>(
                    profile.WatchedItems
                        .Where(w => w.HasMeaningfulInteraction())
                        .Select(w => w.ItemId));

                // Add series-level IDs so series recommendations match episode watches
                foreach (var w in profile.WatchedItems)
                {
                    if (w.SeriesId.HasValue && w.HasMeaningfulInteraction())
                    {
                        watched.Add(w.SeriesId.Value);
                    }
                }

                foreach (var favSeriesId in profile.FavoriteSeriesIds)
                {
                    watched.Add(favSeriesId);
                }

                watchedItemLookup[profile.UserId] = watched;
            }

            ensemble.ApplyCohortFeedback(previousResults, watchedItemLookup);
        }

        return trained;
    }

    /// <inheritdoc />
    public IReadOnlyList<RecommendationResult> GetAllRecommendations(
        int maxResultsPerUser = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maxResultsPerUser = Math.Clamp(maxResultsPerUser, 1, EngineConstants.MaxRecommendationsPerUserLimit);
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var candidates = LoadCandidateItems();
        var peopleLookup = _similarityComputer.BuildCandidatePeopleLookup(candidates);

        // Pre-compute BoxSet membership for all candidates once (shared across all users).
        // Avoids redundant parent-hierarchy traversals in ScoreCandidate / BuildWatchedBoxSetCounts.
        var candidateBoxSetLookup = new Dictionary<Guid, List<Guid>>();
        foreach (var c in candidates)
        {
            var boxSets = ResolveBoxSetIds(c);
            if (boxSets.Count > 0)
            {
                candidateBoxSetLookup[c.Id] = boxSets;
            }
        }

        // Cache for on-demand single-user calls that may follow
        _cachedSnapshot = new CandidateSnapshot(candidates, peopleLookup, candidateBoxSetLookup);

        // Pre-compute all user watched-item sets ONCE for collaborative filtering.
        // Reduces O(U²×M) to O(U×M) by sharing sets across BuildCollaborativeMap calls.
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        // Cold-start prior: build a community popularity map (itemId → watch count)
        // from the precomputed user sets. Passed to cold-start scoring so that new users
        // benefit from the collective "wisdom of the crowd" rather than only static
        // metadata (rating + release date). Items that many active users have watched
        // are more likely to be broadly appealing to newcomers.
        // Only built once per batch run — reused across all cold-start users.
        //
        // Guard on non-empty sets: PrecomputeUserWatchSets keeps empty profiles, so a
        // simple .Count > 1 check would enable the community prior even when only a
        // single user has any watch data (that user's own set would be "the community").
        // We require at least two users with actual watch data before the prior kicks in.
        Dictionary<Guid, int>? communityPopularity = null;
        var usersWithHistory = 0;
        foreach (var userSet in precomputedUserSets.Values)
        {
            if (userSet.Count > 0 && ++usersWithHistory >= 2)
            {
                break;
            }
        }

        if (usersWithHistory >= 2)
        {
            communityPopularity = new Dictionary<Guid, int>();
            foreach (var userSet in precomputedUserSets.Values)
            {
                foreach (var itemId in userSet)
                {
                    communityPopularity.TryGetValue(itemId, out var count);
                    communityPopularity[itemId] = count + 1;
                }
            }
        }

        _pluginLog.LogInfo(
            "Recommendations",
            $"Starting recommendation generation for {allProfiles.Count} users using strategy '{_strategy.Name}'...",
            _logger);

        // Process users in parallel - each user's scoring is CPU-bound and independent.
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
                        ? GenerateColdStartRecommendations(
                            profile.UserId,
                            maxResultsPerUser,
                            profile.UserName,
                            candidates,
                            profile.MaxParentalRating,
                            profile,
                            communityPopularity,
                            cancellationToken)
                        : GenerateForUser(
                            profile,
                            allProfiles,
                            candidates,
                            peopleLookup,
                            candidateBoxSetLookup,
                            maxResultsPerUser,
                            _strategy,
                            precomputedUserSets,
                            _strategySelector.GetAlphaOffset(profile.UserId),
                            cancellationToken);
                    concurrentResults.Add(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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
    ///     When null, candidates are loaded fresh via <see cref="LoadCandidateItems" />.
    /// </param>
    /// <param name="maxParentalRating">
    ///     Optional maximum parental rating for the user.
    ///     Candidates exceeding this rating are excluded from cold-start recommendations.
    /// </param>
    /// <param name="userProfile">
    ///     Optional user watch profile. When provided, a stripped copy is included in the result
    ///     for consistency with <see cref="GenerateForUser" />. Cold-start users have empty
    ///     WatchedItems but their profile still carries UserId, UserName, MaxParentalRating etc.
    /// </param>
    /// <param name="communityPopularity">
    ///     Optional community popularity map (itemId → number of active users who have watched it),
    ///     built from all users' watch profiles in the batch path. When provided, the cold-start
    ///     formula becomes 40% rating + 30% recency + 30% community-popularity, letting new users
    ///     benefit from collective viewing signals. When null (on-demand single-user path or when
    ///     there is only one user in the system), the classic 60% rating + 40% recency formula
    ///     is used unchanged to preserve backward compatibility for isolated deployments.
    /// </param>
    /// <param name="cancellationToken">Token for cooperative cancellation during large candidate scans.</param>
    /// <returns>A recommendation result with popular/trending items.</returns>
    private RecommendationResult GenerateColdStartRecommendations(
        Guid userId,
        int maxResults,
        string? userName = null,
        List<BaseItem>? preloadedCandidates = null,
        int? maxParentalRating = null,
        UserWatchProfile? userProfile = null,
        IReadOnlyDictionary<Guid, int>? communityPopularity = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = preloadedCandidates ?? LoadCandidateItems();

        // Pre-compute the max community-popularity for normalization to [0, 1].
        // Using log1p compression so a single item watched by 100 users doesn't overshadow
        // items watched by 5-10 users — we want a smooth gradient, not a winner-take-all signal.
        // Falls back gracefully when community data is unavailable (single-user deployments).
        var useCommunityPrior = communityPopularity is { Count: > 0 };
        var maxLogPopularity = 0.0;
        if (useCommunityPrior)
        {
            foreach (var count in communityPopularity!.Values)
            {
                var logValue = Math.Log(1.0 + count);
                if (logValue > maxLogPopularity)
                {
                    maxLogPopularity = logValue;
                }
            }
        }

        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        var candidateIndex = 0;
        foreach (var candidate in candidates)
        {
            // Periodically check cancellation to stay responsive for large libraries
            if (++candidateIndex % EngineConstants.CancellationCheckBatchSize == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Parental rating filter - skip items the user is not allowed to see
            if (ExceedsMaxRating(candidate, maxParentalRating))
            {
                continue;
            }

            var combinedCriticScore = ContentScoring.ComputeCombinedCriticScore(
                candidate.CommunityRating,
                candidate.CriticRating);
            var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);

            double score;
            if (useCommunityPrior && maxLogPopularity > 0.0)
            {
                // Enhanced cold-start formula with community popularity prior.
                // Weights: 40% rating (quality), 30% recency (freshness), 30% community-popularity (social proof).
                // Community-popularity uses log1p compression to smooth long-tail distribution.
                var communityScore = 0.0;
                if (communityPopularity!.TryGetValue(candidate.Id, out var watchCount) && watchCount > 0)
                {
                    communityScore = Math.Clamp(Math.Log(1.0 + watchCount) / maxLogPopularity, 0.0, 1.0);
                }

                score = (0.4 * combinedCriticScore) + (0.3 * recencyScore) + (0.3 * communityScore);
            }
            else
            {
                // Classic formula (single-user deployments or on-demand path).
                score = (0.6 * combinedCriticScore) + (0.4 * recencyScore);
            }

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
                CriticRating = s.Item.CriticRating,
                OfficialRating = s.Item.OfficialRating,
                PremiereDate = s.Item.PremiereDate,
                PrimaryImageTag = s.Item.HasImage(ImageType.Primary) ? s.Item.Id.ToString("N") : null,
                PeopleNames = [],
                Studios = s.Item.Studios ?? [],
                Tags = s.Item.Tags ?? [],
                AudioLanguages = ResolveAudioLanguages(s.Item),
                SubtitleLanguages = ResolveSubtitleLanguages(s.Item),
                BoxSetIds = ResolveBoxSetIds(s.Item),
                DateCreated = s.Item.DateCreated
            })
            .ToList();

        _pluginLog.LogInfo(
            "Recommendations",
            $"Generated {topItems.Count} cold-start recommendations for user '{userId}' (no watch history)",
            _logger);

        return new RecommendationResult
        {
            UserId = userId,
            UserName = userName ?? string.Empty,
            Profile = userProfile is not null ? ReasonResolver.StripWatchedItemsForResponse(userProfile) : null,
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
    private List<BaseItem> LoadCandidateItems()
    {
        var movies = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                IsFolder = false
            });

        var series = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                IsFolder = true
            });

        var candidates = new List<BaseItem>(movies.Count + series.Count);

        // Filter out placeholder movies that have no media file on disk.
        // Arr stacks (Radarr/Sonarr) may create library entries with metadata
        // before the actual media file has been downloaded, resulting in items
        // with no Path that cannot be played.
        var skippedMovies = 0;
        foreach (var movie in movies)
        {
            if (string.IsNullOrEmpty(movie.Path))
            {
                skippedMovies++;
                continue;
            }

            candidates.Add(movie);
        }

        // Filter out empty series that have no episodes indexed yet.
        // Arr stacks may create series folders before any episodes are available.
        // A series without episodes cannot be resolved to a playable item and would
        // waste a recommendation slot.
        //
        // Performance: load all episodes in a single query and collect distinct SeriesIds,
        // rather than querying per-series (N queries → 1 query). This is O(E) in memory
        // but avoids N round-trips to the database on slow NAS/Docker systems.
        var allEpisodes = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsFolder = false
            });

        var seriesIdsWithEpisodes = allEpisodes
            .OfType<Episode>()
            .Where(episode => !string.IsNullOrEmpty(episode.Path))
            .Select(episode => episode.SeriesId)
            .Where(seriesId => seriesId != Guid.Empty)
            .ToHashSet();

        var skippedSeries = 0;
        foreach (var s in series)
        {
            if (!seriesIdsWithEpisodes.Contains(s.Id))
            {
                skippedSeries++;
                continue;
            }

            candidates.Add(s);
        }

        if (skippedMovies > 0 || skippedSeries > 0)
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Filtered {skippedMovies} empty movies and {skippedSeries} empty series from candidate pool.",
                _logger);
        }

        if (candidates.Count > EngineConstants.CandidateCountWarningThreshold)
        {
            _pluginLog.LogWarning(
                "Recommendations",
                $"Large candidate set: {candidates.Count} items. Consider using the scheduled task.",
                logger: _logger);
        }

        return candidates;
    }

    /// <summary>
    ///     Generates recommendations for a single user by scoring all unwatched items.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles for collaborative filtering.</param>
    /// <param name="allCandidates">Pre-loaded candidate items from the library.</param>
    /// <param name="peopleLookup">Pre-built people lookup (item ID → person names).</param>
    /// <param name="candidateBoxSetLookup">Pre-resolved BoxSet IDs per candidate (sparse: only items in BoxSets).</param>
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <param name="strategy">The scoring strategy to use.</param>
    /// <param name="precomputedUserSets">
    ///     Optional pre-computed user watch sets for collaborative filtering performance.
    ///     Pass null for single-user mode (sets will be built on-the-fly).
    /// </param>
    /// <param name="alphaOffset">Alpha offset for cohort-based exploration (0.0 = control group).</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>A recommendation result for the user.</returns>
    private RecommendationResult GenerateForUser(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        List<BaseItem> allCandidates,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup,
        int maxResults,
        IScoringStrategy strategy,
        Dictionary<Guid, HashSet<Guid>>? precomputedUserSets,
        double alphaOffset = 0.0,
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

        // Exclude played, favorited, AND started items from candidates - the user already knows these items.
        // Started items (PlayCount > 0 or PlaybackPositionTicks > 0) appear in Jellyfin's "Continue Watching"
        // and should not waste a recommendation slot. Their genre/studio/tag/people signals still flow
        // into preferences via PreferenceBuilder.
        var watchedIds = new HashSet<Guid>(
            userProfile.WatchedItems
                .Where(w => w.HasMeaningfulInteraction())
                .Select(w => w.ItemId));
        var watchedSeriesIds = new HashSet<Guid>(
            userProfile.WatchedItems
                .Where(w => w.HasMeaningfulInteraction() && w.SeriesId.HasValue)
                .Select(w => w.SeriesId!.Value));

        // Also include series-level favorites (user favorited the series itself, not individual episodes)
        foreach (var favSeriesId in userProfile.FavoriteSeriesIds)
        {
            watchedSeriesIds.Add(favSeriesId);
        }

        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(userProfile);

        // Build O(1) candidate lookup by ID - shared across studio/tag preference building
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
        // preferredPeople (HashSet): used by ReasonResolver to surface a concrete matched-person name
        // in recommendation reasons. Kept as an unweighted set for readable UI output.
        var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, peopleLookup);
        // preferredPeopleWeights: v3 (C2) frequency-aware weighting for the ML PeopleSimilarity
        // feature. Keys are always a superset-parity match with preferredPeople (same eligibility rule),
        // but per-key weights reflect how many watched items each person appears on, so dominant
        // collaborators (e.g. a director watched 8 times) drive similarity more than one-off cameos.
        var preferredPeopleWeights = PreferenceBuilder.BuildPeoplePreferenceWeights(userProfile, peopleLookup);
        var preferredTags = PreferenceBuilder.BuildTagPreferenceSet(userProfile, candidateLookup);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, userProfile);

        // Pre-compute BoxSet membership for watched items to enable CollectionProgressionBoost
        // at inference time. Maps BoxSet ID → count of watched items in that BoxSet.
        // Uses the pre-resolved candidateBoxSetLookup for O(1) lookups (no parent traversal).
        // Includes series-level IDs (from watched episodes' SeriesId and FavoriteSeriesIds)
        // so TV-collection BoxSets contribute progression signals alongside movie BoxSets.
        var watchedForBoxSets = new HashSet<Guid>(watchedIds);
        watchedForBoxSets.UnionWith(watchedSeriesIds);
        var watchedBoxSetCounts = BuildWatchedBoxSetCounts(watchedForBoxSets, candidateBoxSetLookup);

        // Pre-compute per-item genre, people, and studio sets for watched items.
        // Used by ContentNearestNeighborScore to find the most similar watched item for each candidate.
        // Built once per user, O(1) per-candidate lookup via parallel list indices.
        var watchedGenreSets = new List<HashSet<string>>();
        var watchedPeopleSets = new List<HashSet<string>>();
        var watchedStudioSets = new List<HashSet<string>>();
        foreach (var w in userProfile.WatchedItems.Where(w => w.Played || w.IsFavorite))
        {
            watchedGenreSets.Add(
                w.Genres is { Count: > 0 }
                    ? new HashSet<string>(w.Genres, StringComparer.OrdinalIgnoreCase)
                    : []);

            // People: resolve from peopleLookup (which maps item IDs to person name sets)
            watchedPeopleSets.Add(peopleLookup.TryGetValue(w.ItemId, out var wp) ? wp : []);

            // Studios: resolve from candidateLookup (which maps item IDs to BaseItems with Studios)
            watchedStudioSets.Add(
                candidateLookup.TryGetValue(w.ItemId, out var wi) && wi.Studios is { Length: > 0 }
                    ? new HashSet<string>(wi.Studios, StringComparer.OrdinalIgnoreCase)
                    : []);
        }

        // Score each unwatched candidate
        var scored = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        var candidateIndex = 0;
        var userMaxRating = userProfile.MaxParentalRating;
        foreach (var candidate in allCandidates)
        {
            // Periodically check cancellation to stay responsive for large libraries
            if (++candidateIndex % EngineConstants.CancellationCheckBatchSize == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            // Parental rating filter - skip items the user is not allowed to see.
            // Uses Jellyfin's InheritedParentalRatingValue which cascades from parent items
            // (e.g., a series rating applies to all its episodes).
            // This ensures children with restricted profiles only get age-appropriate recommendations.
            if (ExceedsMaxRating(candidate, userMaxRating))
            {
                continue;
            }

            if (watchedIds.Contains(candidate.Id))
            {
                continue;
            }

            // Skip series where the user has any interaction: Played, IsFavorite,
            // PlayCount > 0, or PlaybackPositionTicks > 0 on at least one episode,
            // or favorited the series itself. Jellyfin natively shows "Next Up" and
            // "Continue Watching" for these series, so recommending them wastes a slot.
            // Their signals still flow into preferences (genre, studio, people) via PreferenceBuilder.
            if (candidate is Series && watchedSeriesIds.Contains(candidate.Id))
            {
                continue;
            }

            scored.Add(
                ScoreCandidate(
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
                    preferredPeopleWeights,
                    preferredTags,
                    peopleLookup,
                    genreExposure,
                    watchedGenreSets,
                    watchedPeopleSets,
                    watchedStudioSets,
                    watchedBoxSetCounts,
                    candidateBoxSetLookup,
                    alphaOffset));
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
                CriticRating = s.Item.CriticRating,
                OfficialRating = s.Item.OfficialRating,
                PremiereDate = s.Item.PremiereDate,
                PrimaryImageTag = s.Item.HasImage(ImageType.Primary) ? s.Item.Id.ToString("N") : null,
                PeopleNames = peopleLookup.TryGetValue(s.Item.Id, out var people) ? [.. people] : [],
                Studios = s.Item.Studios ?? [],
                Tags = s.Item.Tags ?? [],
                AudioLanguages = ResolveAudioLanguages(s.Item),
                SubtitleLanguages = ResolveSubtitleLanguages(s.Item),
                BoxSetIds = ResolveBoxSetIds(s.Item),
                DateCreated = s.Item.DateCreated
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
            ScoringStrategyKey = strategy.NameKey,
            Cohort = _strategySelector.GetCohortName(userProfile.UserId)
        };
    }

    /// <summary>
    ///     Scores a single candidate item against the user's preferences.
    ///     Computes all feature signals and delegates to the scoring strategy.
    ///     When <paramref name="alphaOffset"/> is non-zero and the strategy is an
    ///     <see cref="EnsembleScoringStrategy"/>, the offset is applied for cohort exploration.
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
        IReadOnlyDictionary<string, double> preferredPeopleWeights,
        HashSet<string> preferredTags,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        List<HashSet<string>> watchedGenreSets,
        List<HashSet<string>> watchedPeopleSets,
        List<HashSet<string>> watchedStudioSets,
        Dictionary<Guid, int> watchedBoxSetCounts,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup,
        double alphaOffset = 0.0)
    {
        var genreScore = SimilarityComputer.ComputeGenreSimilarity(candidate.Genres ?? [], genrePreferences);
        var collabScore = ContentScoring.ComputeCollaborativeScore(candidate.Id, coOccurrence, collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(candidate.CommunityRating, candidate.CriticRating);
        var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);
        var libraryAddedRecency = ContentScoring.ComputeRecencyScore(candidate.DateCreated);
        var yearScore = ContentScoring.ComputeYearProximity(candidate.ProductionYear, averageYear);

        // Compute user-specific signals - for series candidates, aggregate from watched episodes
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
            // Average per-episode completion ratios
            completionRatio = episodesForScoring.Count > 0
                ? Math.Clamp(
                    episodesForScoring.Average(e => ContentScoring.ComputeCompletionRatio(e)),
                    0.0,
                    1.0)
                : 0.5;
        }
        else
        {
            watchedItemLookup.TryGetValue(candidate.Id, out var watchedItem);
            hasUserInteraction = watchedItem is not null;
            userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItem);
            completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItem) : 0.5;
        }

        var studioMatch = candidate.Studios is { Length: > 0 } &&
                          candidate.Studios.Any(s => preferredStudios.Contains(s));
        // Roadmap v3 (C2): use the weighted overload so a candidate carrying the user's
        // heavy-hitter collaborators (e.g. a director the user has watched 8 times) drives
        // similarity more than one-off cameo appearances that both the unweighted HashSet
        // and the previous overlap coefficient would treat identically.
        var peopleSimilarity = peopleLookup.TryGetValue(candidate.Id, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights)
            : 0.0;

        // Series progression boost: usually 0.0 at inference time.
        // Most series with meaningful interaction are filtered earlier by the watchedSeriesIds check,
        // so this boost is typically not reached during live scoring.
        // Note: seriesEpisodeLookup is broader than watchedSeriesIds because it includes all watched
        // entries with a SeriesId (not filtered by HasMeaningfulInteraction()), so edge cases exist
        // where this block can still execute.
        // The field is kept to preserve feature-vector layout parity with the training pipeline,
        // where the boost IS computed from real episode data (the series was recommended first,
        // then the user watched it — progression is a valid training signal even though it
        // rarely appears at inference time).
        var seriesProgressionBoost = 0.0;
        if (candidate is Series candidateSeries &&
            seriesEpisodeLookup.TryGetValue(candidateSeries.Id, out var progressionEps))
        {
            var playedEps = progressionEps.Count(e => e.Played);
            if (progressionEps.Count > 0)
            {
                var ratio = (double)playedEps / progressionEps.Count;
                seriesProgressionBoost = ratio < 0.9 ? Math.Clamp(ratio * 1.2, 0.0, 1.0) : 0.2;
            }
        }

        // Popularity proxy from collaborative scores (centralized formula)
        var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

        // Build feature vector and delegate scoring to strategy
        var features = new CandidateFeatures
        {
            GenreSimilarity = genreScore,
            CollaborativeScore = collabScore,
            CombinedCriticScore = combinedCriticScore,
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
            // IsWeekend uses the user's LastActivityDate as reference (falling back to UtcNow when
            // the user has no history yet). This aligns with the training-time semantics where
            // IsWeekend is derived from the watched item's LastPlayedDate rather than DateTime.UtcNow.
            // For active users (LastActivityDate close to now) this is functionally identical to
            // DateTime.UtcNow. For inactive users we anchor the weekend flag to their last real
            // interaction so that the ML model sees consistent train/serve semantics, eliminating
            // the previously observed skew where training reflected historical calendar context
            // but scoring reflected server clock at request time.
            IsWeekend = (userProfile.LastActivityDate ?? DateTime.UtcNow).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            TagSimilarity = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags),
            LibraryAddedRecency = libraryAddedRecency,
            // Content-based nearest-neighbor: composite item-to-item similarity (genre 50%, people 30%, studio 20%)
            // against the user's most similar watched item. Captures item-level affinity as a fine-tuning signal.
            ContentNearestNeighborScore = ContentScoring.ComputeContentNearestNeighborScore(
                new HashSet<string>(candidate.Genres ?? [], StringComparer.OrdinalIgnoreCase),
                peopleLookup.TryGetValue(candidate.Id, out var candidatePeopleForNn) ? candidatePeopleForNn : null,
                candidate.Studios is { Length: > 0 }
                    ? new HashSet<string>(candidate.Studios, StringComparer.OrdinalIgnoreCase)
                    : null,
                watchedGenreSets,
                watchedPeopleSets,
                watchedStudioSets),
            // Language affinity features: resolve media streams ONCE per candidate to avoid
            // calling GetMediaStreams() twice (audio + subtitle). Single-pass via ResolveMediaLanguages().
            LanguageAffinity = ComputeLanguageAffinityFromStreams(userProfile, candidate, out var candidateMediaLanguages),
            // Collection/BoxSet progression: uses pre-resolved BoxSet IDs from candidateBoxSetLookup.
            // No per-candidate parent traversal needed — all BoxSet memberships resolved once during batch init.
            CollectionProgressionBoost = ComputeCollectionProgressionBoostLive(
                candidateBoxSetLookup.TryGetValue(candidate.Id, out var candidateBoxSets) ? candidateBoxSets : [],
                watchedBoxSetCounts),
            // Subtitle language affinity: reuses the already-resolved subtitle languages
            // from the single ResolveMediaLanguages() call above (no second stream scan).
            SubtitleLanguageAffinity = ComputeSubtitleLanguageAffinityFromStreams(userProfile, candidateMediaLanguages.Subtitles)
        };

        // Genre exposure features: soft signals for genre distribution awareness
        // Computed once per user (genreExposure), applied per candidate (O(genres) per item)
        var (underexposure, dominanceRatio, affinityGap) =
            PreferenceBuilder.ComputeGenreExposureFeatures(candidate.Genres ?? [], genreExposure);
        features.GenreUnderexposure = underexposure;
        features.GenreDominanceRatio = dominanceRatio;
        features.GenreAffinityGap = affinityGap;

        // Apply alpha offset for cohort exploration when the strategy supports it.
        // For control cohort (offset≈0), this is a zero-cost fast path via the standard method.
        var explanation = Math.Abs(alphaOffset) > 1e-10 && strategy is EnsembleScoringStrategy ensemble
            ? ensemble.ScoreWithExplanationAndOffset(features, alphaOffset)
            : strategy.ScoreWithExplanation(features);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _pluginLog.LogDebug("Recommendations", $"Score for '{candidate.Name}': {explanation}", _logger);
        }

        var (reason, reasonKey, relatedItem) = ReasonResolver.DetermineReason(
            candidate,
            explanation,
            genrePreferences,
            preferredPeople,
            preferredStudios,
            peopleLookup);

        return (candidate, explanation.FinalScore, reason, reasonKey, relatedItem);
    }

    /// <summary>
    ///     Computes audio language affinity between a candidate's available audio languages
    ///     and the user's language profile. Returns 0.5 (neutral) when no language data is available.
    ///     Uses the chosen-vs-forced distinction: primary language = 1.0, preferred = 0.85,
    ///     tolerated = 0.5, known = 0.3, unknown = 0.1.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <returns>A language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    internal static double ComputeLanguageAffinity(UserWatchProfile userProfile, BaseItem candidate)
    {
        // No language profile → neutral (monolingual library or new user)
        if (userProfile.LanguageProfile.Count == 0)
        {
            return 0.5;
        }

        // Reuse the same stream-resolution logic as ResolveAudioLanguages (returns empty on error/null)
        var candidateLanguages = ResolveAudioLanguages(candidate);
        if (candidateLanguages.Count == 0)
        {
            return 0.5; // No audio stream info → neutral
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            candidateLanguages,
            userProfile.PrimaryLanguage,
            userProfile.PreferredLanguages,
            userProfile.ToleratedLanguages,
            userProfile.LanguageProfile);
    }

    /// <summary>
    ///     Resolves the normalized audio language codes available for a candidate item.
    ///     Delegates to <see cref="ResolveMediaLanguages"/> for a single-pass stream scan.
    ///     Returns an empty list if no audio stream data is available (graceful fallback).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A list of distinct, normalized ISO 639 language codes.</returns>
    private static List<string> ResolveAudioLanguages(BaseItem candidate)
    {
        return ResolveMediaLanguages(candidate).Audio;
    }

    /// <summary>
    ///     Resolves the normalized subtitle language codes available for a candidate item.
    ///     Delegates to <see cref="ResolveMediaLanguages"/> for a single-pass stream scan.
    ///     Returns an empty list if no subtitle stream data is available (graceful fallback).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A list of distinct, normalized ISO 639 subtitle language codes.</returns>
    private static List<string> ResolveSubtitleLanguages(BaseItem candidate)
    {
        return ResolveMediaLanguages(candidate).Subtitles;
    }

    /// <summary>
    ///     Resolves both audio and subtitle language codes from a candidate item's media streams
    ///     in a single pass. Avoids calling <see cref="BaseItem.GetMediaStreams"/> twice per item
    ///     in the scoring hot path (1000+ candidates per user).
    ///     Returns empty lists if no stream data is available (graceful fallback).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A tuple of (Audio languages, Subtitle languages) as distinct, normalized ISO 639 codes.</returns>
    private static (List<string> Audio, List<string> Subtitles) ResolveMediaLanguages(BaseItem candidate)
    {
        try
        {
            var streams = candidate.GetMediaStreams();

            // Series items have no direct media streams — resolve from first child episode as fallback.
            // This enables LanguageAffinity and SubtitleLanguageAffinity to produce real signals
            // for series candidates instead of defaulting to 0.5 (neutral).
            // Series.Children returns Season objects in Jellyfin 10.11+.
            // Navigate Series → first Season → first Episode with a valid file path
            // to extract representative audio/subtitle language metadata.
            if ((streams is null || streams.Count == 0) && candidate is Series series)
            {
                var firstEpisode = series.Children?
                    .OfType<Season>()
                    .SelectMany(season => season.Children?.OfType<Episode>() ?? [])
                    .FirstOrDefault(e => !string.IsNullOrEmpty(e.Path));
                if (firstEpisode is not null)
                {
                    streams = firstEpisode.GetMediaStreams();
                }
            }

            if (streams is null)
            {
                return ([], []);
            }

            var audioLanguages = new List<string>();
            var audioSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLanguages = new List<string>();
            var subtitleSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in streams)
            {
                var normalized = WatchHistoryService.NormalizeLanguage(s.Language);
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                switch (s.Type)
                {
                    case MediaStreamType.Audio when audioSeen.Add(normalized):
                        audioLanguages.Add(normalized);
                        break;
                    case MediaStreamType.Subtitle when subtitleSeen.Add(normalized):
                        subtitleLanguages.Add(normalized);
                        break;
                }
            }

            return (audioLanguages, subtitleLanguages);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return ([], []); // Graceful: no stream data available
        }
    }

    /// <summary>
    ///     Resolves the BoxSet (collection) IDs that a candidate item belongs to.
    ///     Uses Jellyfin's parent hierarchy to find BoxSet containers.
    ///     Returns an empty list if the item is not in any collection.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A list of BoxSet IDs the item belongs to.</returns>
    private static List<Guid> ResolveBoxSetIds(BaseItem candidate)
    {
        const int maxTraversalDepth = 20;

        try
        {
            var boxSetIds = new List<Guid>();

            // Traverse the parent hierarchy to find BoxSet containers.
            // Depth limit guards against corrupted metadata with circular parent references.
            var parent = candidate.GetParent();
            var depth = 0;
            while (parent is not null && depth < maxTraversalDepth)
            {
                if (parent is MediaBrowser.Controller.Entities.Movies.BoxSet)
                {
                    boxSetIds.Add(parent.Id);
                }

                parent = parent.GetParent();
                depth++;
            }

            return boxSetIds;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return []; // Graceful fallback
        }
    }

    /// <summary>
    ///     Computes subtitle language affinity between a candidate's available subtitle languages
    ///     and the user's subtitle language profile. Returns 0.5 (neutral) when no data is available.
    ///     Uses the same chosen-vs-forced logic as audio language affinity.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with subtitle language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <returns>A subtitle language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    internal static double ComputeSubtitleLanguageAffinity(UserWatchProfile userProfile, BaseItem candidate)
    {
        // No subtitle language profile → neutral
        if (userProfile.SubtitleLanguageProfile.Count == 0)
        {
            return 0.5;
        }

        var candidateLanguages = ResolveSubtitleLanguages(candidate);
        if (candidateLanguages.Count == 0)
        {
            return 0.5; // No subtitle stream info → neutral
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            candidateLanguages,
            userProfile.PrimarySubtitleLanguage,
            userProfile.PreferredSubtitleLanguages,
            userProfile.ToleratedSubtitleLanguages,
            userProfile.SubtitleLanguageProfile);
    }

    /// <summary>
    ///     Computes audio language affinity with a single-pass stream resolution.
    ///     Resolves both audio and subtitle streams in one call to <see cref="ResolveMediaLanguages"/>,
    ///     outputs the full result so the caller can reuse the subtitle portion without a second scan.
    ///     Used by <see cref="ScoreCandidate"/> to avoid double GetMediaStreams() calls per candidate.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <param name="mediaLanguages">
    ///     Outputs the resolved media languages tuple so subtitle affinity can be computed
    ///     from the same data without a second stream scan.
    /// </param>
    /// <returns>A language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    private static double ComputeLanguageAffinityFromStreams(
        UserWatchProfile userProfile,
        BaseItem candidate,
        out (List<string> Audio, List<string> Subtitles) mediaLanguages)
    {
        mediaLanguages = ResolveMediaLanguages(candidate);

        // No language profile → neutral (monolingual library or new user)
        if (userProfile.LanguageProfile.Count == 0 || mediaLanguages.Audio.Count == 0)
        {
            return 0.5;
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            mediaLanguages.Audio,
            userProfile.PrimaryLanguage,
            userProfile.PreferredLanguages,
            userProfile.ToleratedLanguages,
            userProfile.LanguageProfile);
    }

    /// <summary>
    ///     Computes subtitle language affinity from pre-resolved subtitle language codes.
    ///     Companion to <see cref="ComputeLanguageAffinityFromStreams"/> - reuses the subtitle
    ///     portion of the already-resolved media streams to avoid a second GetMediaStreams() call.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with subtitle language preferences.</param>
    /// <param name="subtitleLanguages">Pre-resolved subtitle language codes from ResolveMediaLanguages().</param>
    /// <returns>A subtitle language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    private static double ComputeSubtitleLanguageAffinityFromStreams(
        UserWatchProfile userProfile,
        List<string> subtitleLanguages)
    {
        if (userProfile.SubtitleLanguageProfile.Count == 0 || subtitleLanguages.Count == 0)
        {
            return 0.5;
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            subtitleLanguages,
            userProfile.PrimarySubtitleLanguage,
            userProfile.PreferredSubtitleLanguages,
            userProfile.ToleratedSubtitleLanguages,
            userProfile.SubtitleLanguageProfile);
    }

    /// <summary>
    ///     Pre-computes BoxSet membership counts for the user's watched items using the
    ///     pre-resolved candidateBoxSetLookup. For each BoxSet that contains at least one
    ///     watched item, stores the count of watched members.
    ///     Built once per user in <see cref="GenerateForUser"/>.
    /// </summary>
    /// <param name="watchedIds">Set of item IDs the user has meaningfully interacted with.</param>
    /// <param name="candidateBoxSetLookup">Pre-resolved BoxSet IDs per candidate (sparse).</param>
    /// <returns>A dictionary mapping BoxSet ID → number of watched items in that BoxSet.</returns>
    private static Dictionary<Guid, int> BuildWatchedBoxSetCounts(
        HashSet<Guid> watchedIds,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup)
    {
        var boxSetCounts = new Dictionary<Guid, int>();

        foreach (var watchedId in watchedIds)
        {
            if (!candidateBoxSetLookup.TryGetValue(watchedId, out var boxSetIds))
            {
                continue;
            }

            foreach (var boxSetId in boxSetIds)
            {
                boxSetCounts.TryGetValue(boxSetId, out var count);
                boxSetCounts[boxSetId] = count + 1;
            }
        }

        return boxSetCounts;
    }

    /// <summary>
    ///     Builds the candidateBoxSetLookup on-demand (for the single-user path when no cached snapshot exists).
    ///     Equivalent to the inline loop in <see cref="GetAllRecommendations"/> but extracted as a helper.
    /// </summary>
    private static Dictionary<Guid, List<Guid>> BuildCandidateBoxSetLookupFresh(List<BaseItem> candidates)
    {
        var lookup = new Dictionary<Guid, List<Guid>>();
        foreach (var c in candidates)
        {
            var boxSets = ResolveBoxSetIds(c);
            if (boxSets.Count > 0)
            {
                lookup[c.Id] = boxSets;
            }
        }

        return lookup;
    }

    /// <summary>
    ///     Computes the CollectionProgressionBoost for a candidate during live inference scoring.
    ///     Uses the pre-computed <paramref name="watchedBoxSetCounts"/> dictionary for O(1) lookup
    ///     instead of per-candidate parent traversal + child enumeration. Returns a progression
    ///     ratio proportional to how many collection siblings are already watched.
    ///     <para>
    ///         Roadmap v3 (C3.1): the diminishing-returns scale (<c>0.3 + (n-1) × 0.2, clamped [0,1]</c>)
    ///         lives centrally in <see cref="EngineConstants.ComputeCollectionProgressionBoost(int)"/> so
    ///         that this live path and the training-time
    ///         <c>TrainingDataBuilder.ComputeCollectionProgressionBoostWithCounts</c> can never drift.
    ///         The 16 formula-contract tests in <c>CollectionProgressionBoostTests</c> exercise the
    ///         shared helper directly and therefore guard both call sites simultaneously.
    ///     </para>
    /// </summary>
    /// <param name="candidateBoxSetIds">Pre-resolved BoxSet IDs for the candidate (from ResolveBoxSetIds).</param>
    /// <param name="watchedBoxSetCounts">Pre-computed BoxSet ID → watched member count mapping.</param>
    /// <returns>A boost value between 0.0 and 1.0, or 0.0 if not in any collection.</returns>
    private static double ComputeCollectionProgressionBoostLive(
        List<Guid> candidateBoxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        if (watchedBoxSetCounts.Count == 0 || candidateBoxSetIds.Count == 0)
        {
            return 0.0;
        }

        // Find the best progression signal across all BoxSets the candidate belongs to.
        // The formula itself is delegated to EngineConstants so the training path uses
        // exactly the same implementation — guaranteeing train/serve parity by construction.
        var bestBoost = 0.0;
        foreach (var boxSetId in candidateBoxSetIds)
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
    ///     Computes the collection/BoxSet progression boost for a candidate item.
    ///     Returns a positive value if the candidate belongs to a collection where the user
    ///     has already watched other items. Encourages "complete the collection" recommendations.
    /// </summary>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <param name="watchedIds">Set of item IDs the user has watched.</param>
    /// <returns>A boost value between 0.0 and 1.0.</returns>
    internal static double ComputeCollectionProgressionBoost(BaseItem candidate, HashSet<Guid> watchedIds)
    {
        try
        {
            // Check if the candidate belongs to any BoxSet via parent traversal
            var parent = candidate.GetParent();
            while (parent is not null)
            {
                if (parent is MediaBrowser.Controller.Entities.Movies.BoxSet boxSet)
                {
                    // Check if the user has watched any other item in this BoxSet
                    var children = boxSet.Children;
                    if (children is not null)
                    {
                        var watchedCount = 0;
                        var totalCount = 0;
                        foreach (var child in children)
                        {
                            if (child.Id == candidate.Id)
                            {
                                continue; // Don't count the candidate itself
                            }

                            totalCount++;
                            if (watchedIds.Contains(child.Id))
                            {
                                watchedCount++;
                            }
                        }

                        if (watchedCount > 0 && totalCount > 0)
                        {
                            // Boost proportional to how much of the collection is watched
                            var ratio = (double)watchedCount / totalCount;
                            return Math.Clamp(ratio * 1.2, 0.0, 1.0);
                        }
                    }
                }

                parent = parent.GetParent();
            }

            return 0.0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return 0.0; // Graceful fallback
        }
    }

    /// <summary>
    ///     Updates the "Requested + Watched" status in the discovery feedback store.
    ///     For each user with discovery feedback, resolves TMDb provider IDs from library items
    ///     the user has watched and cross-references them with requested discovery items.
    ///     When a match is found, the feedback entry is upgraded from "Requested" (label 0.75)
    ///     to "RequestedAndWatched" (label 0.90) for more accurate training signal.
    ///     Best-effort: failures are logged but do not block training.
    /// </summary>
    /// <param name="cancellationToken">Token for cooperative cancellation.</param>
    private void UpdateDiscoveryWatchedStatus(CancellationToken cancellationToken)
    {
        try
        {
            var allFeedback = _discoveryFeedbackStore.LoadAll();
            if (allFeedback.Count == 0)
            {
                return;
            }

            // Build a TMDb ID → watched set per user.
            // Query library items once and resolve their TMDb provider IDs,
            // then cross-reference with each user's watched items.
            var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
            var profileById = new Dictionary<Guid, UserWatchProfile>(allProfiles.Count);
            foreach (var p in allProfiles)
            {
                profileById.TryAdd(p.UserId, p);
            }

            // Build Jellyfin ItemId → TMDb ID and ItemId → MediaType mappings from library items.
            // Only load movies + series (same as LoadCandidateItems) to avoid excessive queries.
            var tmdbIdByItemId = new Dictionary<Guid, int>();
            var mediaTypeByItemId = new Dictionary<Guid, string>();
            var libraryItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
            });

            foreach (var item in libraryItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.TryGetProviderId("Tmdb", out var tmdbStr) &&
                    int.TryParse(tmdbStr, out var tmdbId) && tmdbId > 0)
                {
                    tmdbIdByItemId.TryAdd(item.Id, tmdbId);
                    mediaTypeByItemId.TryAdd(item.Id, item is Series ? "tv" : "movie");
                }
            }

            if (tmdbIdByItemId.Count == 0)
            {
                return;
            }

            // For each user in the feedback store, resolve which (TmdbId, MediaType) they've watched
            foreach (var userFeedback in allFeedback)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Only process users who have at least one requested-but-not-yet-watched entry
                    if (!userFeedback.Entries.Any(e => e.RequestedAtUtc.HasValue && !e.WasWatched))
                    {
                        continue;
                    }

                    // Find the user's watch profile via O(1) dictionary lookup
                    if (!profileById.TryGetValue(userFeedback.UserId, out var userProfile))
                    {
                        continue;
                    }

                    // Collect composite (TmdbId, MediaType) keys of items this user has watched.
                    // MediaType resolved from library item type (Movie → "movie", Series → "tv").
                    var watchedItems = new HashSet<(int TmdbId, string MediaType)>();
                    foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
                    {
                        if (tmdbIdByItemId.TryGetValue(w.ItemId, out var tmdbId))
                        {
                            var mt = mediaTypeByItemId.TryGetValue(w.ItemId, out var resolved) ? resolved : "movie";
                            watchedItems.Add((tmdbId, mt));
                        }

                        // Also check series-level TMDb IDs (for TV shows)
                        if (w.SeriesId.HasValue && tmdbIdByItemId.TryGetValue(w.SeriesId.Value, out var seriesTmdbId))
                        {
                            watchedItems.Add((seriesTmdbId, "tv"));
                        }
                    }

                    // Include series-level favorites (user favorited the series itself, not individual episodes)
                    foreach (var favoriteSeriesId in userProfile.FavoriteSeriesIds)
                    {
                        if (tmdbIdByItemId.TryGetValue(favoriteSeriesId, out var favoriteSeriesTmdbId))
                        {
                            watchedItems.Add((favoriteSeriesTmdbId, "tv"));
                        }
                    }

                    if (watchedItems.Count > 0)
                    {
                        _discoveryFeedbackStore.MarkWatched(userFeedback.UserId, watchedItems);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    _pluginLog.LogDebug(
                        "Recommendations",
                        $"Could not update discovery watched status for user '{userFeedback.UserId}': {ex.Message}",
                        _logger);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _pluginLog.LogDebug(
                "Recommendations",
                $"Discovery watched-status update failed (non-critical): {ex.Message}",
                _logger);
        }
    }

    /// <summary>
    ///     Returns true when the candidate's parental rating exceeds the user's maximum,
    ///     or when the candidate has no rating at all (unrated items are treated as unrestricted
    ///     and must be excluded for restricted profiles).
    /// </summary>
    private static bool ExceedsMaxRating(BaseItem candidate, int? maxRating)
    {
        if (!maxRating.HasValue)
        {
            return false;
        }

        return !candidate.InheritedParentalRatingValue.HasValue
               || candidate.InheritedParentalRatingValue.Value > maxRating.Value;
    }

    /// <summary>
    ///     Immutable snapshot of candidate items and their people lookup.
    ///     Published/read as a single reference so concurrent readers always see
    ///     a consistent pair (candidates from the same batch as the people lookup).
    /// </summary>
    /// <param name="Candidates">All candidate items from the library.</param>
    /// <param name="PeopleLookup">Item ID → person name set mapping.</param>
    /// <param name="CandidateBoxSetLookup">
    ///     Pre-resolved BoxSet IDs per candidate. Built once during batch generation
    ///     to avoid redundant parent-hierarchy traversals across multiple users.
    ///     Only candidates that belong to at least one BoxSet are stored (sparse).
    /// </param>
    private sealed record CandidateSnapshot(
        List<BaseItem> Candidates,
        Dictionary<Guid, HashSet<string>> PeopleLookup,
        Dictionary<Guid, List<Guid>> CandidateBoxSetLookup);
}