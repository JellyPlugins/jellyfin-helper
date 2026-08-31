using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Recommendation engine orchestrator. Delegates to specialized components.
/// </summary>
public sealed class Engine : IRecommendationEngine, IDisposable
{
    // File name for the persisted batch-generation counter. Sits in the plugin data folder.
    private const string BatchGenerationFileName = "jellyfin-helper-batch-generation.txt";

    private const string LogCategory = "Recommendations";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Engine> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly SimilarityComputer _similarityComputer;
    private readonly IScoringStrategy _strategy;
    private readonly IStrategySelector _strategySelector;
    private readonly TrainingService _trainingService;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IDiscoveryFeedbackStore _discoveryFeedbackStore;
    private readonly IItemRepository _itemRepository;

    // Short-lived candidate-metadata cache (NOT a result cache): the expensive-to-rebuild library working set (candidate BaseItems, people/BoxSet lookups, episode counts).
    private readonly Lock _snapshotRefreshLock = new();

    // Stored as a single immutable snapshot to prevent concurrent readers from mixing data across batches.
    private volatile CandidateSnapshot? _cachedSnapshot;

    // Library-wide genre/studio IDF rarity table, refreshed alongside the candidate snapshot. Null until the first snapshot computes it, in which case the GenreStudioIdfPrior feature degrades to a neutral 0.0.
    private volatile IReadOnlyDictionary<string, double>? _genreStudioIdf;

    private static readonly TimeSpan CandidateSnapshotMaxAge = TimeSpan.FromMinutes(30);

    // Monotonic counter incremented once per GetAllRecommendations invocation. Snapshotted before the parallel scoring loop so every user in the same batch shares the same batchGeneration value, making the exploration seed deterministic per (user, batch) pair.
    private int _batchGeneration;

    // Monotonic publish-order counter, incremented before EVERY snapshot publish (batch or live-refresh).
    private long _publicationSequence;

    /// <summary>Initializes a new instance of the <see cref="Engine" /> class.</summary>
    /// <param name="watchHistoryService">The watch history service.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="strategy">The scoring strategy resolved via DI.</param>
    /// <param name="strategySelector">The strategy selector for A/B testing.</param>
    /// <param name="discoveryFeedbackStore">The discovery feedback store for training data enrichment.</param>
    /// <param name="itemRepository">The item repository used to derive library-wide genre/studio rarity (IDF).</param>
    public Engine(
        IWatchHistoryService watchHistoryService,
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<Engine> logger,
        IScoringStrategy strategy,
        IStrategySelector strategySelector,
        IDiscoveryFeedbackStore discoveryFeedbackStore,
        IItemRepository itemRepository)
    {
        _watchHistoryService = watchHistoryService;
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _strategy = strategy;
        _strategySelector = strategySelector;
        _discoveryFeedbackStore = discoveryFeedbackStore;
        _itemRepository = itemRepository;
        _similarityComputer = new SimilarityComputer(libraryManager, pluginLog, logger);
        _trainingService = new TrainingService(watchHistoryService, discoveryFeedbackStore, pluginLog, logger);

        // Seed the batch counter from disk so the first post-reload batch does not reuse
        // the exploration seed the very first batch ever produced.
        _batchGeneration = LoadPersistedBatchGeneration();
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

        // Live requests get an exploration seed keyed to (userId, current UTC day). This keeps successive same-day requests deterministic without freezing exploration across days.
        var liveSeed = ComputeStableSeed(userId, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);

        // Read the snapshot once, expiring it past CandidateSnapshotMaxAge (its BaseItems' Genres/Studios/CommunityRating can mutate via metadata refresh, and new additions stay invisible until the next batch).
        var snapshot = GetOrRefreshLiveSnapshot();

        if (!userProfile.WatchedItems.Any(w => w.HasMeaningfulInteraction()))
        {
            // Cold-start: user exists but has no watch history - return popular/trending items, reusing the batch candidates to avoid redundant library queries.
            var communityPopularity = GetOrBuildCommunityPopularity(snapshot);
            return GenerateColdStartRecommendations(
                userId,
                maxResults,
                userProfile.UserName,
                snapshot.Candidates,
                userProfile.MaxParentalRating,
                userProfile,
                communityPopularity: communityPopularity,
                explorationSeed: liveSeed,
                cancellationToken: cancellationToken);
        }

        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();

        // Live path always sees a valid published snapshot (batch or single-flight refresh above),
        // so the fall-back "load fresh" branches are unnecessary.
        var candidates = snapshot.Candidates;
        var seriesEpisodeCounts = snapshot.SeriesEpisodeCounts;
        var peopleLookup = snapshot.PeopleLookup;
        var boxSetLookup = snapshot.CandidateBoxSetLookup;
        var contentAffinityLookup = snapshot.ContentAffinityLookup;
        var alphaOffset = _strategySelector.GetAlphaOffset(userProfile.UserId);
        // Live single-user path: no batch-scoped CollaborativeContext exists, so pass null and let GenerateForUser derive aggregates locally from precomputedUserSets (also null here).
        return GenerateForUser(
            userProfile,
            allProfiles,
            candidates,
            peopleLookup,
            boxSetLookup,
            contentAffinityLookup,
            seriesEpisodeCounts,
            maxResults,
            _strategy,
            null,
            alphaOffset,
            liveSeed,
            collaborativeContext: null,
            ct: cancellationToken);
    }

    /// <inheritdoc />
    public bool TrainStrategy(
        IReadOnlyList<RecommendationResult> previousResults,
        bool incremental = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousResults);

        UpdateDiscoveryWatchedStatus(cancellationToken);

        // Use live library data so training and inference share the same progression and rarity tables.
        var seriesEpisodeCounts = BuildSeriesEpisodeCounts();

        _genreStudioIdf = BuildGenreStudioIdfTable();

        var trained = _trainingService.Train(_strategy, previousResults, seriesEpisodeCounts, incremental, _genreStudioIdf, cancellationToken);

        // Adjust learning speed based on cohort watch rates.
        if (trained && _strategy is EnsembleScoringStrategy ensemble && previousResults.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
            var watchedItemLookup = new Dictionary<Guid, HashSet<Guid>>(allProfiles.Count);
            foreach (var profile in allProfiles)
            {
                var watched = new HashSet<Guid>(
                    profile.WatchedItems
                        .Where(w => w.HasMeaningfulInteraction())
                        .Select(w => w.ItemId));

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

        // Bump the batch counter once per invocation and snapshot it so every user in this batch shares the same exploration seed context.
        var batchGeneration = Interlocked.Increment(ref _batchGeneration);
        PersistBatchGeneration(batchGeneration);

        // Freshness watermark for the stale-publish guard: read the publication sequence NOW, before the slow candidate build, so if a live-refresh publishes a newer snapshot while this batch assembles, TryPublishSnapshot can drop this write instead of clobbering the fresher one.
        var observedSequence = Interlocked.Read(ref _publicationSequence);

        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var (candidates, seriesEpisodeCounts) = LoadCandidateItems();
        var peopleLookup = _similarityComputer.BuildCandidatePeopleLookup(candidates);
        var contentAffinityLookup = BuildCandidateContentAffinityLookup(candidates);

        // Refresh the library-wide genre/studio IDF rarity table once per batch (shared across users).
        _genreStudioIdf = BuildGenreStudioIdfTable();

        // Pre-compute BoxSet membership for all candidates once (shared across users), avoiding
        // redundant parent-hierarchy traversals in ScoreCandidate / BuildWatchedBoxSetCounts.
        var candidateBoxSetLookup = new Dictionary<Guid, List<Guid>>();
        foreach (var c in candidates)
        {
            var boxSets = ResolveBoxSetIds(c);
            if (boxSets.Count > 0)
            {
                candidateBoxSetLookup[c.Id] = boxSets;
            }
        }

        // Pre-compute all user watched-item sets ONCE for collaborative filtering (O(U²×M) -> O(U×M)).
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        // Wrap the user sets in a CollaborativeContext so the itemPopularity map (O(U×M)) and the trust-gate decision (O(U)) are shared across every per-user BuildCollaborativeMap call.
        var collaborativeContext = CollaborativeFilter.PrecomputeCollaborativeContext(precomputedUserSets);

        // Cold-start prior: community popularity map (itemId -> watch count) from the precomputed user sets, passed to cold-start scoring so new users benefit from "wisdom of the crowd" rather than only static metadata.
        var communityPopularity = BuildCommunityPopularityMap(precomputedUserSets);

        // Publish the snapshot once, community-popularity map already included.
        TryPublishSnapshot(new CandidateSnapshot(
            candidates,
            peopleLookup,
            candidateBoxSetLookup,
            seriesEpisodeCounts,
            communityPopularity,
            CommunityPopularityComputed: true, // batch path has executed the O(U×M) scan
            BatchGeneration: batchGeneration,
            PublicationSequence: 0, // assigned atomically inside TryPublishSnapshot
            ObservedSequence: observedSequence, // watermark captured before the build began
            DateTime.UtcNow,
            contentAffinityLookup));

        _pluginLog.LogInfo(
            LogCategory,
            $"Starting recommendation generation for {allProfiles.Count} users using strategy '{_strategy.Name}'...",
            _logger);

        // Process users in parallel - each user's scoring is CPU-bound and independent. ConcurrentBag collects results safely; shared read-only data (candidates, peopleLookup, precomputedUserSets) is never mutated, so no locking.
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
                    // Combine per-user id with the shared batch generation counter for a fresh but user-stable exploration seed.
                    var batchSeed = ComputeStableSeed(profile.UserId, batchGeneration);
                    var result = !profile.WatchedItems.Any(w => w.HasMeaningfulInteraction())
                        ? GenerateColdStartRecommendations(
                            profile.UserId,
                            maxResultsPerUser,
                            profile.UserName,
                            candidates,
                            profile.MaxParentalRating,
                            profile,
                            communityPopularity,
                            explorationSeed: batchSeed,
                            cancellationToken: cancellationToken)
                        : GenerateForUser(
                            profile,
                            allProfiles,
                            candidates,
                            peopleLookup,
                            candidateBoxSetLookup,
                            contentAffinityLookup,
                            seriesEpisodeCounts,
                            maxResultsPerUser,
                            _strategy,
                            precomputedUserSets,
                            _strategySelector.GetAlphaOffset(profile.UserId),
                            batchSeed,
                            collaborativeContext,
                            cancellationToken);
                    concurrentResults.Add(result);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    _pluginLog.LogWarning(
                        LogCategory,
                        $"Failed to generate recommendations for user '{profile.UserName}'",
                        ex,
                        _logger);
                }
            });

        var results = new Collection<RecommendationResult>(concurrentResults.ToList());

        _pluginLog.LogInfo(
            LogCategory,
            $"Finished: {results.Count} users, {results.Sum(r => r.Recommendations.Count)} total recommendations.",
            _logger);
        return results;
    }

    /// <summary>
    ///     Generates cold-start recommendations for users with no watch history. Uses community ratings and recency as proxy signals since no personal preferences exist.
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
    ///     Optional community popularity map (itemId -> number of active users who watched it), built
    ///     from all profiles in the batch path. When provided, the cold-start formula becomes
    ///     40% rating + 30% recency + 30% community-popularity. When null (on-demand single-user path,
    ///     or only one user in the system), the classic 60% rating + 40% recency formula is used
    ///     unchanged for backward compatibility in isolated deployments.
    /// </param>
    /// <param name="explorationSeed">
    ///     Optional deterministic seed forwarded to <see cref="DiversityReranker.ApplyDiversityReranking"/>.
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
        int? explorationSeed = null,
        CancellationToken cancellationToken = default)
    {
        // Cold start does not need SeriesEpisodeCounts (progression signals require watch history,
        // which cold-start users lack). Discard it explicitly.
        var candidates = preloadedCandidates ?? LoadCandidateItems().Candidates;

        // Pre-compute the max community-popularity to normalize to [0, 1]. log1p compression keeps a single item watched by 100 users from overshadowing items watched by 5-10 (smooth gradient, not winner-take-all).
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

            var score = ComputeColdStartScore(candidate, useCommunityPrior, maxLogPopularity, communityPopularity);
            scored.Add((candidate, score, "Popular and highly rated", "reasonPopular", null));
        }

        var topItems = DiversityReranker.ApplyDiversityReranking(scored, maxResults, explorationSeed)
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
                DateCreated = s.Item.DateCreated,
                TmdbCollectionName = ContentAffinityResolver.ResolveTmdbCollectionName(s.Item),
                ProductionCountries = ContentAffinityResolver.ResolveProductionCountries(s.Item),
                InheritedTags = ContentAffinityResolver.ResolveInheritedTags(s.Item),
                SeriesStatus = ContentAffinityResolver.ResolveSeriesStatus(s.Item),
                EndDate = ContentAffinityResolver.ResolveSeriesEndDate(s.Item),
                WriterNames = ResolveWriterNames(s.Item),
                PeopleWeights = []
            })
            .ToList();

        _pluginLog.LogInfo(
            LogCategory,
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
    ///     Computes the cold-start relevance score for a single candidate, blending a rating term, a recency term and (when community data is available) a log1p-compressed popularity term.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="useCommunityPrior">Whether community-popularity data is available.</param>
    /// <param name="maxLogPopularity">The pre-computed max log1p popularity used to normalize.</param>
    /// <param name="communityPopularity">Per-item community watch counts, or <c>null</c>.</param>
    /// <returns>The cold-start score for the candidate.</returns>
    private static double ComputeColdStartScore(
        BaseItem candidate,
        bool useCommunityPrior,
        double maxLogPopularity,
        IReadOnlyDictionary<Guid, int>? communityPopularity)
    {
        // Cold-start rating term.
        var isUnrated = !HasUsableRating(candidate.CommunityRating) && !HasUsableRating(candidate.CriticRating);
        var ratingScore = isUnrated
            ? EngineConstants.ColdStartUnratedRatingPrior
            : ContentScoring.ComputeCombinedCriticScore(candidate.CommunityRating, candidate.CriticRating);
        var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? candidate.DateCreated);

        if (useCommunityPrior && maxLogPopularity > 0.0)
        {
            // Enhanced cold-start formula: 40% rating (quality), 30% recency (freshness),
            // 30% community-popularity (social proof, log1p-compressed for the long tail).
            var communityScore = 0.0;
            if (communityPopularity!.TryGetValue(candidate.Id, out var watchCount) && watchCount > 0)
            {
                communityScore = Math.Clamp(Math.Log(1.0 + watchCount) / maxLogPopularity, 0.0, 1.0);
            }

            return (0.4 * ratingScore) + (0.3 * recencyScore) + (0.3 * communityScore);
        }

        // Classic formula (single-user deployments or on-demand path).
        return (0.6 * ratingScore) + (0.4 * recencyScore);
    }

    /// <summary>
    ///     Returns true when a rating value is present and usable (finite and non-negative), matching the exact validity predicate ComputeCombinedCriticScore uses to decide "rated vs.
    /// </summary>
    /// <param name="rating">The candidate's community or critic rating.</param>
    /// <returns><c>true</c> if the rating is present, finite and >= 0.</returns>
    private static bool HasUsableRating(float? rating)
        => rating.HasValue && float.IsFinite(rating.Value) && rating.Value >= 0;

    /// <summary>
    ///     Loads all candidate items (movies and series) from the library, together with a per-series episode count map derived from the same episode query used for the empty-series filter (no extra DB round-trip).
    /// </summary>
    /// <returns>
    ///     Candidates and a <c>seriesId -> totalEpisodeCount</c> map. The map only contains
    ///     series with at least one playable episode; consumers must treat missing keys as
    ///     "no progression signal available".
    /// </returns>
    private (List<BaseItem> Candidates, Dictionary<Guid, int> SeriesEpisodeCounts) LoadCandidateItems()
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

        // Filter out placeholder movies with no media file on disk. Arr stacks (Radarr/Sonarr) may create library entries with metadata before the file is downloaded, yielding unplayable items with no Path.
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

        // Filter out empty series with no episodes indexed yet (Arr stacks may create series folders before episodes exist).
        var allEpisodes = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsFolder = false
            });

        // Single pass building both the "series has episodes" filter set and the per-series total episode count for PreferenceBuilder's progression multiplier.
        var seriesEpisodeCounts = CountPlayableEpisodesPerSeries(allEpisodes);

        var skippedSeries = 0;
        foreach (var s in series)
        {
            if (!seriesEpisodeCounts.ContainsKey(s.Id))
            {
                skippedSeries++;
                continue;
            }

            candidates.Add(s);
        }

        if (skippedMovies > 0 || skippedSeries > 0)
        {
            _pluginLog.LogInfo(
                LogCategory,
                $"Filtered {skippedMovies} empty movies and {skippedSeries} empty series from candidate pool.",
                _logger);
        }

        if (candidates.Count > EngineConstants.CandidateCountWarningThreshold)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Large candidate set: {candidates.Count} items. Consider using the scheduled task.",
                logger: _logger);
        }

        return (candidates, seriesEpisodeCounts);
    }

    /// <summary>
    ///     Builds the per-series total-episode-count map (SeriesId -> number of playable episodes in the library) used by PreferenceBuilder's progression multiplier.
    /// </summary>
    /// <returns>A map of series id to its count of playable (non-empty <c>Path</c>) episodes.</returns>
    private Dictionary<Guid, int> BuildSeriesEpisodeCounts()
    {
        var allEpisodes = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsFolder = false
            });

        return CountPlayableEpisodesPerSeries(allEpisodes);
    }

    /// <summary>
    ///     Collapses a flat episode list into a per-series playable-episode count. Only episodes with a non-empty Path and a valid SeriesId are counted, matching the candidate-filtering rule so the progression ratio (watched / total) stays meaningful.
    /// </summary>
    private static Dictionary<Guid, int> CountPlayableEpisodesPerSeries(IReadOnlyList<BaseItem> episodes)
    {
        var seriesEpisodeCounts = new Dictionary<Guid, int>();
        foreach (var episode in episodes.OfType<Episode>())
        {
            if (string.IsNullOrEmpty(episode.Path) || episode.SeriesId == Guid.Empty)
            {
                continue;
            }

            seriesEpisodeCounts.TryGetValue(episode.SeriesId, out var count);
            seriesEpisodeCounts[episode.SeriesId] = count + 1;
        }

        return seriesEpisodeCounts;
    }

    /// <summary>
    ///     Generates recommendations for a single user by scoring all unwatched items.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles for collaborative filtering.</param>
    /// <param name="allCandidates">Pre-loaded candidate items from the library.</param>
    /// <param name="peopleLookup">Pre-built people lookup (item ID -> person names).</param>
    /// <param name="candidateBoxSetLookup">Pre-resolved BoxSet IDs per candidate (sparse: only items in BoxSets).</param>
    /// <param name="contentAffinityLookup">Per-candidate content-affinity source data (5 metadata fields + writers + billing), pre-computed once per snapshot.</param>
    /// <param name="seriesEpisodeCounts">
    ///     Per-series total episode count (from <see cref="LoadCandidateItems"/>) used by
    ///     <see cref="PreferenceBuilder"/> to weight watched-episode signals by the fraction
    ///     of the series the user has actually seen. Missing keys treated as "no signal".
    /// </param>
    /// <param name="maxResults">Maximum number of recommendations to return.</param>
    /// <param name="strategy">The scoring strategy to use.</param>
    /// <param name="precomputedUserSets">
    ///     Optional pre-computed user watch sets for collaborative filtering performance.
    ///     Pass null for single-user mode (sets will be built on-the-fly).
    /// </param>
    /// <param name="alphaOffset">Alpha offset for cohort-based exploration (0.0 = control group).</param>
    /// <param name="explorationSeed">
    ///     Optional deterministic seed for the diversity exploration RNG. Live requests pass
    ///     <c>ComputeStableSeed(userId, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber)</c>,
    ///     batch runs pass <c>ComputeStableSeed(userId, batchGeneration)</c>. The stable-seed
    ///     helper is used instead of <see cref="HashCode.Combine{T1,T2}"/> so the value is
    ///     process-independent - a Jellyfin restart does not reshuffle same-day exploration.
    /// </param>
    /// <param name="collaborativeContext">
    ///     Optional batch-scoped collaborative aggregates (item popularity + trust-gate flag)
    ///     shared across every user in the same batch. When provided, this method uses the
    ///     <see cref="CollaborativeFilter.BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeFilter.CollaborativeContext)"/>
    ///     overload so those O(U×M) / O(U) aggregates are not recomputed per user. When null
    ///     (single-user live path) the method falls back to the legacy overload which derives
    ///     the aggregates locally from <paramref name="precomputedUserSets"/> or a fresh build.
    /// </param>
    /// <param name="ct">Cancellation token for cooperative cancellation. Kept last to satisfy CA1068.</param>
    /// <returns>A recommendation result for the user.</returns>
    private RecommendationResult GenerateForUser(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        List<BaseItem> allCandidates,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup,
        Dictionary<Guid, CandidateContentAffinity> contentAffinityLookup,
        IReadOnlyDictionary<Guid, int> seriesEpisodeCounts,
        int maxResults,
        IScoringStrategy strategy,
        Dictionary<Guid, HashSet<Guid>>? precomputedUserSets,
        double alphaOffset = 0.0,
        int? explorationSeed = null,
        CollaborativeFilter.CollaborativeContext? collaborativeContext = null,
        CancellationToken ct = default)
    {
        // Build a lookup of watched items by ID for O(1) access in scoring methods
        var watchedItemLookup = new Dictionary<Guid, WatchedItemInfo>(userProfile.WatchedItems.Count);
        foreach (var w in userProfile.WatchedItems)
        {
            watchedItemLookup.TryAdd(w.ItemId, w);
        }

        // Exclude played, favorited, AND started items - the user already knows them. Started items (PlayCount > 0 or PlaybackPositionTicks > 0) appear in Jellyfin's "Continue Watching" and should not waste a slot.
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

        // seriesEpisodeCounts is forwarded so PreferenceBuilder can weight each episode row by the fraction of the series the user has actually watched.
        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(userProfile, seriesEpisodeCounts);

        // Build O(1) candidate lookup by ID - shared across studio/tag preference building
        var candidateLookup = new Dictionary<Guid, BaseItem>(allCandidates.Count);
        foreach (var c in allCandidates)
        {
            candidateLookup.TryAdd(c.Id, c);
        }

        // Build the collaborative co-occurrence map. • Batch mode: use the precomputed CollaborativeContext (itemPopularity + trust-gate read from the shared record rather than redone).
        var coOccurrence = collaborativeContext is not null
            ? CollaborativeFilter.BuildCollaborativeMap(userProfile, allProfiles, collaborativeContext)
            : CollaborativeFilter.BuildCollaborativeMap(userProfile, allProfiles, precomputedUserSets);
        // NaN-safe collaborative-score ceiling.
        var collaborativeMax = ComputeCollaborativeMax(coOccurrence);

        var averageYear = ContentScoring.ComputeAverageYear(userProfile);
        var preferredStudios = PreferenceBuilder.BuildStudioPreferenceSet(userProfile, candidateLookup);
        // preferredPeople (HashSet): used by ReasonResolver to surface a concrete matched-person name
        // in recommendation reasons. Kept as an unweighted set for readable UI output.
        var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, peopleLookup);
        // preferredPeopleWeights: Frequency-aware weighting for the ML PeopleSimilarity feature.
        var preferredPeopleWeights = PreferenceBuilder.BuildPeoplePreferenceWeights(userProfile, peopleLookup, seriesEpisodeCounts);
        // Precompute the top-K average preferred weight ONCE per user so the O(P log P) sort inside
        // ComputePeopleSimilarity does not re-run per candidate.
        var averagePreferredPeopleWeight = SimilarityComputer.ComputeAveragePreferredWeight(preferredPeopleWeights);
        var preferredTags = PreferenceBuilder.BuildTagPreferenceSet(userProfile, candidateLookup);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePreferences, userProfile);

        // Per-user preference maps for the content-affinity signals. Built once per user from the watch profile (empty when the user has no history / no items carrying the field), then passed to every ScoreCandidate call.
        var preferredFranchises = PreferenceBuilder.BuildFranchisePreferenceVector(userProfile);
        var preferredCountries = PreferenceBuilder.BuildProductionCountryPreferenceVector(userProfile);
        var preferredInheritedTags = PreferenceBuilder.BuildInheritedTagPreferenceSet(userProfile);
        var preferredWriterWeights = PreferenceBuilder.BuildWriterPreferenceWeights(userProfile);
        // Precompute the top-K average writer weight ONCE per user (mirrors averagePreferredPeopleWeight)
        // so the O(W log W) sort inside ComputeWriterAffinity does not re-run per candidate.
        var averageWriterWeight = SimilarityComputer.ComputeAveragePreferredWeight(preferredWriterWeights);
        var preferredBilledPeople = preferredPeopleWeights;

        // Library-wide genre/studio IDF rarity table (computed once per snapshot, Phase 2H via IItemRepository).
        var genreStudioIdf = _genreStudioIdf;

        var userGenreNormSq = ComputeUserGenreNormSq(genrePreferences);

        // Pre-compute BoxSet membership for watched items to enable CollectionProgressionBoost at inference. Maps BoxSet ID -> count of watched items in that BoxSet, using the pre-resolved candidateBoxSetLookup for O(1) lookups (no parent traversal).
        var watchedForBoxSets = new HashSet<Guid>(watchedIds);
        watchedForBoxSets.UnionWith(watchedSeriesIds);
        var watchedBoxSetCounts = BuildWatchedBoxSetCounts(watchedForBoxSets, candidateBoxSetLookup);

        // Pre-compute per-item genre, people, and studio sets for watched items. Used by ContentNearestNeighborScore to find the most similar watched item for each candidate.
        BuildWatchedNeighborSets(
            userProfile,
            peopleLookup,
            candidateLookup,
            out var watchedGenreSets,
            out var watchedPeopleSets,
            out var watchedStudioSets);

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

            if (ShouldSkipCandidate(candidate, userMaxRating, watchedIds, watchedSeriesIds))
            {
                continue;
            }

            scored.Add(
                ScoreCandidate(
                    candidate,
                    userProfile,
                    strategy,
                    genrePreferences,
                    userGenreNormSq,
                    coOccurrence,
                    collaborativeMax,
                    averageYear,
                    watchedItemLookup,
                    preferredStudios,
                    preferredPeople,
                    preferredPeopleWeights,
                    averagePreferredPeopleWeight,
                    preferredTags,
                    peopleLookup,
                    genreExposure,
                    watchedGenreSets,
                    watchedPeopleSets,
                    watchedStudioSets,
                    watchedBoxSetCounts,
                    candidateBoxSetLookup,
                    contentAffinityLookup,
                    preferredFranchises,
                    preferredCountries,
                    preferredInheritedTags,
                    preferredWriterWeights,
                    averageWriterWeight,
                    preferredBilledPeople,
                    genreStudioIdf,
                    alphaOffset));
        }

        scored = DiversityReranker.DeduplicateSeries(scored);

        var topItems = DiversityReranker.ApplyDiversityReranking(scored, maxResults, explorationSeed)
            .Select(s => BuildRecommendedItem(s, contentAffinityLookup, peopleLookup, candidateBoxSetLookup))
            .ToList();

        _pluginLog.LogInfo(
            LogCategory,
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
    ///     Computes the NaN-safe maximum of the collaborative co-occurrence values. Extracted verbatim from GenerateForUser; the finite-only filtering and comparison are unchanged.
    /// </summary>
    /// <param name="coOccurrence">The collaborative co-occurrence map.</param>
    /// <returns>The largest finite value, or 0.0 when none exceed it.</returns>
    private static double ComputeCollaborativeMax(IReadOnlyDictionary<Guid, double> coOccurrence)
    {
        var collaborativeMax = 0.0;
        foreach (var v in coOccurrence.Values.Where(double.IsFinite))
        {
            if (v > collaborativeMax)
            {
                collaborativeMax = v;
            }
        }

        return collaborativeMax;
    }

    /// <summary>
    ///     Computes the squared L2 norm of the user's genre-preference weights. Extracted verbatim from GenerateForUser; the accumulation order and arithmetic are unchanged.
    /// </summary>
    /// <param name="genrePreferences">The user's genre-preference vector.</param>
    /// <returns>The sum of squared genre weights.</returns>
    private static double ComputeUserGenreNormSq(IReadOnlyDictionary<string, double> genrePreferences)
    {
        var userGenreNormSq = 0.0;
        foreach (var w in genrePreferences.Values)
        {
            userGenreNormSq += w * w;
        }

        return userGenreNormSq;
    }

    /// <summary>
    ///     Projects a scored candidate tuple into a RecommendedItem DTO. Extracted verbatim from the top-N projection in GenerateForUser; field resolution and the content-affinity fallback are unchanged.
    /// </summary>
    /// <param name="s">The scored candidate tuple.</param>
    /// <param name="contentAffinityLookup">Per-snapshot content-affinity precompute.</param>
    /// <param name="peopleLookup">Item id -> person name set lookup.</param>
    /// <param name="candidateBoxSetLookup">Item id -> containing BoxSet ids lookup.</param>
    /// <returns>The populated recommendation DTO.</returns>
    private RecommendedItem BuildRecommendedItem(
        (BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem) s,
        Dictionary<Guid, CandidateContentAffinity> contentAffinityLookup,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup)
    {
        // Resolve the candidate's content-affinity source data ONCE from the per-snapshot precompute (fallback to a live resolve only for a just-added candidate), then reuse it for all seven cached DTO fields below - no per-field GetPeople/GetInheritedTags.
        var content = contentAffinityLookup.TryGetValue(s.Item.Id, out var cachedContent)
            ? cachedContent
            : ResolveContentAffinity(s.Item);
        return new RecommendedItem
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
            BoxSetIds = candidateBoxSetLookup.TryGetValue(s.Item.Id, out var bsIds) ? bsIds : [],
            DateCreated = s.Item.DateCreated,
            // Content-affinity fields for the top-N result DTO come from the per-snapshot precompute (resolved once into `content` above) rather than a fresh GetInheritedTags()/GetPeople per top-N item per user.
            TmdbCollectionName = content.TmdbCollectionName,
            ProductionCountries = content.ProductionCountries,
            InheritedTags = content.InheritedTags,
            SeriesStatus = content.SeriesStatus,
            EndDate = content.SeriesEndDate,
            WriterNames = content.Writers,
            PeopleWeights = AlignBillingToNames(
                content.Billing,
                peopleLookup.TryGetValue(s.Item.Id, out var pw) ? pw : null)
        };
    }

    /// <summary>
    ///     Determines whether a candidate should be skipped during per-user scoring: it exceeds the user's parental rating, is already watched, or is a series the user has interacted with.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <param name="userMaxRating">The user's max allowed parental rating, or <c>null</c>.</param>
    /// <param name="watchedIds">Item ids the user has meaningfully interacted with.</param>
    /// <param name="watchedSeriesIds">Series ids the user has interacted with or favorited.</param>
    /// <returns><c>true</c> when the candidate must be excluded from recommendations.</returns>
    private static bool ShouldSkipCandidate(
        BaseItem candidate,
        int? userMaxRating,
        HashSet<Guid> watchedIds,
        HashSet<Guid> watchedSeriesIds)
    {
        // Parental rating filter - skip items the user is not allowed to see. Uses Jellyfin's InheritedParentalRatingValue which cascades from parents (a series rating applies to its episodes), so restricted profiles only get age-appropriate recommendations.
        if (ExceedsMaxRating(candidate, userMaxRating))
        {
            return true;
        }

        if (watchedIds.Contains(candidate.Id))
        {
            return true;
        }

        // Skip series with any interaction (Played, IsFavorite, PlayCount > 0, or PlaybackPositionTicks > 0 on an episode, or the series favorited).
        return candidate is Series && watchedSeriesIds.Contains(candidate.Id);
    }

    /// <summary>
    ///     Builds the parallel per-watched-item genre/people/studio sets used by ContentNearestNeighborScore.
    /// </summary>
    /// <param name="userProfile">The user's watch profile.</param>
    /// <param name="peopleLookup">Item id -> person name set lookup.</param>
    /// <param name="candidateLookup">Item id -> candidate item lookup.</param>
    /// <param name="watchedGenreSets">Receives the per-watched-item genre sets.</param>
    /// <param name="watchedPeopleSets">Receives the per-watched-item people sets.</param>
    /// <param name="watchedStudioSets">Receives the per-watched-item studio sets.</param>
    private static void BuildWatchedNeighborSets(
        UserWatchProfile userProfile,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        Dictionary<Guid, BaseItem> candidateLookup,
        out List<HashSet<string>> watchedGenreSets,
        out List<HashSet<string>> watchedPeopleSets,
        out List<HashSet<string>> watchedStudioSets)
    {
        watchedGenreSets = new List<HashSet<string>>();
        watchedPeopleSets = new List<HashSet<string>>();
        watchedStudioSets = new List<HashSet<string>>();
        foreach (var w in userProfile.WatchedItems.Where(w => w.HasMeaningfulInteraction()))
        {
            watchedGenreSets.Add(
                w.Genres is { Count: > 0 }
                    ? new HashSet<string>(w.Genres, StringComparer.OrdinalIgnoreCase)
                    : []);

            // People: resolve from peopleLookup (which maps item IDs to person name sets)
            peopleLookup.TryGetValue(w.ItemId, out var wp);
            if (wp == null && w.SeriesId.HasValue)
            {
                peopleLookup.TryGetValue(w.SeriesId.Value, out wp);
            }

            watchedPeopleSets.Add(wp != null ? new HashSet<string>(wp, StringComparer.OrdinalIgnoreCase) : []);

            // Studios: resolve from candidateLookup (which maps item IDs to BaseItems with Studios)
            candidateLookup.TryGetValue(w.ItemId, out var wi);
            if (wi == null && w.SeriesId.HasValue)
            {
                candidateLookup.TryGetValue(w.SeriesId.Value, out wi);
            }

            watchedStudioSets.Add(
                wi?.Studios is { Length: > 0 }
                    ? new HashSet<string>(wi.Studios, StringComparer.OrdinalIgnoreCase)
                    : []);
        }
    }

    /// <summary>
    ///     Scores a single candidate item against the user's preferences. Computes all feature signals and delegates to the scoring strategy.
    /// </summary>
    private (BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem) ScoreCandidate(
        BaseItem candidate,
        UserWatchProfile userProfile,
        IScoringStrategy strategy,
        Dictionary<string, double> genrePreferences,
        double userGenreNormSq,
        Dictionary<Guid, double> coOccurrence,
        double collaborativeMax,
        double averageYear,
        Dictionary<Guid, WatchedItemInfo> watchedItemLookup,
        HashSet<string> preferredStudios,
        HashSet<string> preferredPeople,
        IReadOnlyDictionary<string, double> preferredPeopleWeights,
        double averagePreferredPeopleWeight,
        HashSet<string> preferredTags,
        Dictionary<Guid, HashSet<string>> peopleLookup,
        PreferenceBuilder.GenreExposureAnalysis genreExposure,
        List<HashSet<string>> watchedGenreSets,
        List<HashSet<string>> watchedPeopleSets,
        List<HashSet<string>> watchedStudioSets,
        Dictionary<Guid, int> watchedBoxSetCounts,
        Dictionary<Guid, List<Guid>> candidateBoxSetLookup,
        Dictionary<Guid, CandidateContentAffinity> contentAffinityLookup,
        IReadOnlyDictionary<string, double> preferredFranchises,
        IReadOnlyDictionary<string, double> preferredCountries,
        HashSet<string> preferredInheritedTags,
        IReadOnlyDictionary<string, double> preferredWriterWeights,
        double averageWriterWeight,
        IReadOnlyDictionary<string, double> preferredBilledPeople,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        double alphaOffset = 0.0)
    {
        var genreScore = SimilarityComputer.ComputeGenreSimilarity(candidate.Genres ?? [], genrePreferences, userGenreNormSq);
        var collabScore = ContentScoring.ComputeCollaborativeScore(candidate.Id, coOccurrence, collaborativeMax);
        var combinedCriticScore =
            ContentScoring.ComputeCombinedCriticScore(candidate.CommunityRating, candidate.CriticRating);
        var dateCreated = candidate.DateCreated;
        var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? dateCreated);
        var libraryAddedRecency = ContentScoring.ComputeRecencyScore(dateCreated);
        var yearScore = ContentScoring.ComputeYearProximity(candidate.ProductionYear, averageYear);

        // Compute user-specific signals. Series with meaningful interaction are excluded upstream (watchedSeriesIds filter), so every Series reaching here is treated like a Movie: look up in watchedItemLookup, fall back to neutral defaults when absent.
        watchedItemLookup.TryGetValue(candidate.Id, out var watchedItem);
        var hasUserInteraction = watchedItem is not null;
        var userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItem);
        // No interaction: no completion data, use 0.0 (not 0.5 which implies 50% progress)
        var completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItem) : 0.0;

        // Resolve the candidate's user-invariant content-affinity data from the per-snapshot precompute (built once per candidate, never per user).
        var content = contentAffinityLookup.TryGetValue(candidate.Id, out var cachedContent)
            ? cachedContent
            : ResolveContentAffinity(candidate);

        // Aliases onto the precomputed sets; reused for studioMatch and ContentNearestNeighborScore.
        var candidateGenreSet = content.GenreSet;
        var candidateStudioSet = content.StudioSet;

        var studioMatch = candidateStudioSet is not null && candidateStudioSet.Any(preferredStudios.Contains);

        // Weighted overload so a candidate carrying the user's heavy-hitter collaborators (e.g. a director watched 8 times) drives similarity more than one-off cameos that the unweighted HashSet and the old overlap coefficient treated identically.
        var peopleSimilarity = peopleLookup.TryGetValue(candidate.Id, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights, averagePreferredPeopleWeight)
            : 0.0;

        // Series progression boost: hardcoded 0.0 at inference. Series with meaningful episode interaction are excluded upstream, so any series here has no play/favorite signal to aggregate.
        const double seriesProgressionBoost = 0.0;

        // Popularity proxy from collaborative scores (centralized formula)
        var popularityScore = ContentScoring.ComputePopularityScore(collabScore, combinedCriticScore);

        // Language affinity: resolve media streams ONCE per candidate to avoid two GetMediaStreams() calls (audio + subtitle).
        var languageAffinity = ComputeLanguageAffinityFromStreams(userProfile, candidate, out var candidateMediaLanguages);

        // New content-affinity signals. All seven read their candidate-side source from the per-snapshot `content` precompute (built once per candidate), so this hot path does no GetInheritedTags() traversal and no GetPeople round-trip.
        var franchiseAffinity = SimilarityComputer.ComputeFranchiseAffinity(
            content.TmdbCollectionName, preferredFranchises);
        var productionLocationAffinity = SimilarityComputer.ComputeProductionLocationAffinity(
            content.ProductionCountries, preferredCountries);
        var inheritedTagSimilarity = SimilarityComputer.ComputeInheritedTagSimilarity(
            content.InheritedTags, preferredInheritedTags);
        var seriesCompletability = EngineConstants.ComputeSeriesCompletability(
            candidate is Series, content.SeriesStatus, content.SeriesEndDate.HasValue);
        var writerAffinity = SimilarityComputer.ComputeWriterAffinity(
            content.Writers, preferredWriterWeights, averageWriterWeight);
        var billingWeightedPeople = SimilarityComputer.ComputeBillingWeightedPeople(
            content.Billing, preferredBilledPeople);
        var genreStudioIdfPrior = SimilarityComputer.ComputeGenreStudioIdfPrior(
            candidate.Genres, candidate.Studios, genreStudioIdf);

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
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile),
            TagSimilarity = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags),
            LibraryAddedRecency = libraryAddedRecency,
            ContentNearestNeighborScore = ContentScoring.ComputeContentNearestNeighborScore(
                candidateGenreSet,
                candidatePeople,
                candidateStudioSet,
                watchedGenreSets,
                watchedPeopleSets,
                watchedStudioSets),
            LanguageAffinity = languageAffinity,
            CollectionProgressionBoost = ComputeCollectionProgressionBoostLive(
                candidateBoxSetLookup.TryGetValue(candidate.Id, out var candidateBoxSets) ? candidateBoxSets : [],
                watchedBoxSetCounts),
            // Subtitle language affinity: reuses the already-resolved subtitle languages
            // from the single ResolveMediaLanguages() call above (no second stream scan).
            SubtitleLanguageAffinity = ComputeSubtitleLanguageAffinityFromStreams(userProfile, candidateMediaLanguages.Subtitles),
            FranchiseAffinity = franchiseAffinity,
            ProductionLocationAffinity = productionLocationAffinity,
            InheritedTagSimilarity = inheritedTagSimilarity,
            SeriesCompletability = seriesCompletability,
            WriterAffinity = writerAffinity,
            BillingWeightedPeople = billingWeightedPeople,
            GenreStudioIdfPrior = genreStudioIdfPrior
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
            _pluginLog.LogDebug(LogCategory, $"Score for '{candidate.Name}': {explanation}", _logger);
        }

        var (reason, reasonKey, relatedItem) = ReasonResolver.DetermineReason(
            candidate,
            explanation,
            genrePreferences,
            preferredPeople,
            preferredStudios,
            peopleLookup,
            preferredPeopleWeights);

        return (candidate, explanation.FinalScore, reason, reasonKey, relatedItem);
    }

    /// <summary>
    ///     Computes audio language affinity between a candidate's available audio languages and the user's language profile.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <returns>A language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    internal static double ComputeLanguageAffinity(UserWatchProfile userProfile, BaseItem candidate)
    {
        // No language profile -> neutral (monolingual library or new user)
        if (userProfile.LanguageProfile.Count == 0)
        {
            return 0.5;
        }

        // Reuse the same stream-resolution logic as ResolveAudioLanguages (returns empty on error/null)
        var candidateLanguages = ResolveAudioLanguages(candidate);
        if (candidateLanguages.Count == 0)
        {
            return 0.5; // No audio stream info -> neutral
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            candidateLanguages,
            userProfile.PrimaryLanguage,
            userProfile.PreferredLanguages,
            userProfile.ToleratedLanguages,
            userProfile.LanguageProfile);
    }

    /// <summary>
    ///     Resolves the normalized audio language codes available for a candidate item. Delegates to a static stream scan (no series fallback).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A list of distinct, normalized ISO 639 language codes.</returns>
    private static List<string> ResolveAudioLanguages(BaseItem candidate)
    {
        return ResolveStreamsLanguages(candidate).Audio;
    }

    /// <summary>
    ///     Resolves the normalized subtitle language codes available for a candidate item. Delegates to a static stream scan (no series fallback).
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A list of distinct, normalized ISO 639 subtitle language codes.</returns>
    private static List<string> ResolveSubtitleLanguages(BaseItem candidate)
    {
        return ResolveStreamsLanguages(candidate).Subtitles;
    }

    /// <summary>
    ///     Resolves both audio and subtitle language codes from a candidate item's media streams in a single pass, without a series child-episode fallback.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A tuple of (Audio languages, Subtitle languages) as distinct, normalized ISO 639 codes.</returns>
    private static (List<string> Audio, List<string> Subtitles) ResolveStreamsLanguages(BaseItem candidate)
    {
        try
        {
            var streams = candidate.GetMediaStreams();
            if (streams is null)
            {
                return ([], []);
            }

            return ParseLanguagesFromStreams(streams);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return ([], []); // Graceful: no stream data available
        }
    }

    /// <summary>
    ///     Resolves both audio and subtitle language codes from a candidate item's media streams in a single pass.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A tuple of (Audio languages, Subtitle languages) as distinct, normalized ISO 639 codes.</returns>
    private (List<string> Audio, List<string> Subtitles) ResolveMediaLanguages(BaseItem candidate)
    {
        try
        {
            var streams = candidate.GetMediaStreams();

            // Series items have no direct media streams - resolve from first child episode as fallback. This enables LanguageAffinity and SubtitleLanguageAffinity to produce real signals for series candidates instead of defaulting to 0.5 (neutral).
            if ((streams is null || streams.Count == 0) && candidate is Series series)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsFolder = false,
                    Limit = 1,
                });
                var firstEpisode = episodes.Count > 0 ? episodes[0] : null;
                if (firstEpisode is not null)
                {
                    streams = firstEpisode.GetMediaStreams();
                    if ((streams is null || streams.Count == 0) && _logger.IsEnabled(LogLevel.Debug))
                    {
                        _pluginLog.LogDebug(LogCategory, $"Series '{series.Name}' (Id={series.Id}): fallback episode has no media streams.", _logger);
                    }
                }
            }

            if (streams is null)
            {
                return ([], []);
            }

            return ParseLanguagesFromStreams(streams);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return ([], []); // Graceful: no stream data available
        }
    }

    /// <summary>
    ///     Parses distinct, normalized audio and subtitle language codes from a list of media streams.
    /// </summary>
    /// <param name="streams">The media streams to parse.</param>
    /// <returns>A tuple of (Audio languages, Subtitle languages).</returns>
    private static (List<string> Audio, List<string> Subtitles) ParseLanguagesFromStreams(IReadOnlyList<MediaStream> streams)
    {
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

    /// <summary>
    ///     Resolves the BoxSet (collection) IDs that a candidate item belongs to. Uses Jellyfin's parent hierarchy to find BoxSet containers.
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
        catch (Exception ex) when (!ex.IsFatal())
        {
            return []; // Graceful fallback
        }
    }

    /// <summary>
    ///     Resolves the writer (screenplay/creator) names of an item via the library people index. Returns an empty list when unavailable.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>The distinct writer names, or an empty list.</returns>
    private List<string> ResolveWriterNames(BaseItem candidate)
    {
        try
        {
            return ContentAffinityResolver.ExtractWriterNames(_libraryManager.GetPeople(candidate));
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return []; // Graceful fallback
        }
    }

    /// <summary>
    ///     Extracts a name -> billing-weight map from an already-fetched people list (no library call).
    /// </summary>
    /// <param name="people">The item's people, or null.</param>
    /// <returns>A name -> billing-weight map (empty when no billed cast/directors).</returns>
    private static Dictionary<string, double> ExtractBillingWeightMap(IReadOnlyList<PersonInfo>? people)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (people is null || people.Count == 0)
        {
            return map;
        }

        var fallbackOrder = 0;
        foreach (var person in people)
        {
            if ((person.Type != PersonKind.Actor && person.Type != PersonKind.Director)
                || string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            // SortOrder is ascending (0 = top-billed). Missing -> use encounter order as a proxy.
            var order = person.SortOrder ?? fallbackOrder;
            var weight = EngineConstants.ComputeBillingWeight(order);
            if (!map.TryGetValue(person.Name, out var existing) || weight > existing)
            {
                map[person.Name] = weight;
            }

            fallbackOrder++;
        }

        return map;
    }

    /// <summary>
    ///     Pre-computes, once per candidate snapshot, ALL candidate-invariant content-affinity source data for every candidate: five metadata fields (TMDb collection, production countries, inherited tags, series status, series end date) plus writer names and the.
    /// </summary>
    /// <param name="candidates">The candidate items in the snapshot.</param>
    /// <returns>Per-item content-affinity source data keyed by item id (dense over readable candidates).</returns>
    private Dictionary<Guid, CandidateContentAffinity> BuildCandidateContentAffinityLookup(List<BaseItem> candidates)
    {
        var lookup = new Dictionary<Guid, CandidateContentAffinity>(candidates.Count);

        foreach (var candidate in candidates)
        {
            IReadOnlyList<PersonInfo>? people;
            try
            {
                people = _libraryManager.GetPeople(candidate);
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                people = null; // People unreadable - still cache the metadata fields below.
            }

            lookup[candidate.Id] = new CandidateContentAffinity(
                ContentAffinityResolver.ResolveTmdbCollectionName(candidate),
                ContentAffinityResolver.ResolveProductionCountries(candidate),
                ContentAffinityResolver.ResolveInheritedTags(candidate),
                ContentAffinityResolver.ResolveSeriesStatus(candidate),
                ContentAffinityResolver.ResolveSeriesEndDate(candidate),
                ContentAffinityResolver.ExtractWriterNames(people),
                ExtractBillingWeightMap(people),
                BuildGenreSet(candidate),
                BuildStudioSet(candidate));
        }

        return lookup;
    }

    /// <summary>
    ///     Builds the case-insensitive genre set for a candidate. Extracted so the per-snapshot
    ///     precompute and the live fallback build it identically.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A case-insensitive set of the candidate's genres (never null; possibly empty).</returns>
    private static HashSet<string> BuildGenreSet(BaseItem candidate)
        => new(candidate.Genres ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Builds the case-insensitive studio set for a candidate, or null when it has no studios.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>A case-insensitive set of the candidate's studios, or null when there are none.</returns>
    private static HashSet<string>? BuildStudioSet(BaseItem candidate)
        => candidate.Studios is { Length: > 0 } studios
            ? new HashSet<string>(studios, StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    ///     Live fallback that builds a single CandidateContentAffinity for a candidate not present in the per-snapshot lookup (e.g.
    /// </summary>
    /// <param name="candidate">The candidate item.</param>
    /// <returns>The candidate's content-affinity source data.</returns>
    private CandidateContentAffinity ResolveContentAffinity(BaseItem candidate)
    {
        IReadOnlyList<PersonInfo>? people;
        try
        {
            people = _libraryManager.GetPeople(candidate);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            people = null;
        }

        return new CandidateContentAffinity(
            ContentAffinityResolver.ResolveTmdbCollectionName(candidate),
            ContentAffinityResolver.ResolveProductionCountries(candidate),
            ContentAffinityResolver.ResolveInheritedTags(candidate),
            ContentAffinityResolver.ResolveSeriesStatus(candidate),
            ContentAffinityResolver.ResolveSeriesEndDate(candidate),
            ContentAffinityResolver.ExtractWriterNames(people),
            ExtractBillingWeightMap(people),
            BuildGenreSet(candidate),
            BuildStudioSet(candidate));
    }

    /// <summary>
    ///     Aligns a pre-resolved billing-weight map to a people-name set (the same set cached as PeopleNames), producing an index-aligned weight list for parity-safe consumption at training time.
    /// </summary>
    /// <param name="billing">The candidate's pre-resolved name -> billing-weight map.</param>
    /// <param name="alignedNames">The people names the weights must align to; null/empty -> empty result.</param>
    /// <returns>Billing weights aligned to <paramref name="alignedNames"/> in enumeration order.</returns>
    private static List<double> AlignBillingToNames(Dictionary<string, double> billing, HashSet<string>? alignedNames)
    {
        if (alignedNames is null || alignedNames.Count == 0)
        {
            return [];
        }

        var weights = new List<double>(alignedNames.Count);
        foreach (var name in alignedNames)
        {
            weights.Add(billing.TryGetValue(name, out var w) ? w : 0.0);
        }

        return weights;
    }

    /// <summary>
    ///     Builds a library-wide genre/studio IDF (inverse document frequency) rarity table, normalized to [0, 1], where ubiquitous genres/studios score near 0 and rare ones near 1.
    /// </summary>
    /// <returns>A case-insensitive genre/studio -> normalized-IDF map (empty when unavailable).</returns>
    private Dictionary<string, double> BuildGenreStudioIdfTable()
    {
        var table = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var query = new InternalItemsQuery { Recursive = true };
            var rawIdf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var maxIdf = 0.0;

            void Accumulate(QueryResult<(BaseItem Item, ItemCounts ItemCounts)>? result)
            {
                if (result?.Items is not { Count: > 0 } items)
                {
                    return;
                }

                // N = number of distinct terms (documents) in this facet; guaranteed > 0 here.
                var n = items.Count;
                foreach (var (item, counts) in items)
                {
                    var name = item?.Name;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    // add-one smoothing on the document frequency -> never log(N/0); df>=0 always.
                    var df = Math.Max(0, counts.ItemCount);
                    var idf = Math.Log((double)(n + 1) / (df + 1));
                    if (!double.IsFinite(idf) || idf < 0.0)
                    {
                        idf = 0.0;
                    }

                    // Keep the higher IDF if a term appears in both facets (defensive; keys rarely collide).
                    if (!rawIdf.TryGetValue(name, out var existing) || idf > existing)
                    {
                        rawIdf[name] = idf;
                    }

                    if (idf > maxIdf)
                    {
                        maxIdf = idf;
                    }
                }
            }

            Accumulate(_itemRepository.GetGenres(query));
            Accumulate(_itemRepository.GetStudios(query));

            if (rawIdf.Count == 0 || maxIdf <= 0.0)
            {
                return table; // empty -> neutral 0.0 downstream (no division by zero)
            }

            // Normalize to [0,1] so the prior is comparable to the other bounded features.
            foreach (var (name, idf) in rawIdf)
            {
                table[name] = Math.Clamp(idf / maxIdf, 0.0, 1.0);
            }

            return table;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Failed to build genre/studio IDF table; GenreStudioIdfPrior will be neutral. {ex.Message}",
                logger: _logger);
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Computes subtitle language affinity between a candidate's available subtitle languages and the user's subtitle language profile.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with subtitle language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <returns>A subtitle language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    internal static double ComputeSubtitleLanguageAffinity(UserWatchProfile userProfile, BaseItem candidate)
    {
        // No subtitle language profile -> neutral
        if (userProfile.SubtitleLanguageProfile.Count == 0)
        {
            return 0.5;
        }

        var candidateLanguages = ResolveSubtitleLanguages(candidate);
        if (candidateLanguages.Count == 0)
        {
            return 0.5; // No subtitle stream info -> neutral
        }

        return Training.TrainingFeatureComputer.ComputeBestLanguageAffinity(
            candidateLanguages,
            userProfile.PrimarySubtitleLanguage,
            userProfile.PreferredSubtitleLanguages,
            userProfile.ToleratedSubtitleLanguages,
            userProfile.SubtitleLanguageProfile);
    }

    /// <summary>
    ///     Computes audio language affinity with a single-pass stream resolution. Resolves both audio and subtitle streams in one call to ResolveMediaLanguages, outputs the full result so the caller can reuse the subtitle portion without a second scan.
    /// </summary>
    /// <param name="userProfile">The user's watch profile with language preferences.</param>
    /// <param name="candidate">The candidate item to evaluate.</param>
    /// <param name="mediaLanguages">
    ///     Outputs the resolved media languages tuple so subtitle affinity can be computed
    ///     from the same data without a second stream scan.
    /// </param>
    /// <returns>A language affinity score between 0.1 and 1.0, or 0.5 if no data available.</returns>
    private double ComputeLanguageAffinityFromStreams(
        UserWatchProfile userProfile,
        BaseItem candidate,
        out (List<string> Audio, List<string> Subtitles) mediaLanguages)
    {
        mediaLanguages = ResolveMediaLanguages(candidate);

        // No language profile -> neutral (monolingual library or new user)
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
    ///     Computes subtitle language affinity from pre-resolved subtitle language codes. Companion to ComputeLanguageAffinityFromStreams - reuses the subtitle portion of the already-resolved media streams to avoid a second GetMediaStreams() call.
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
    ///     Pre-computes BoxSet membership counts for the user's watched items using the pre-resolved candidateBoxSetLookup.
    /// </summary>
    /// <param name="watchedIds">Set of item IDs the user has meaningfully interacted with.</param>
    /// <param name="candidateBoxSetLookup">Pre-resolved BoxSet IDs per candidate (sparse).</param>
    /// <returns>A dictionary mapping BoxSet ID -> number of watched items in that BoxSet.</returns>
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
    ///     Returns the current candidate snapshot, refreshing it under a single-flight gate when the cache is empty or has exceeded CandidateSnapshotMaxAge.
    /// </summary>
    /// <returns>A fresh (or still-valid) snapshot. Never returns null.</returns>
    private CandidateSnapshot GetOrRefreshLiveSnapshot()
    {
        var snapshot = _cachedSnapshot;
        if (snapshot is not null && DateTime.UtcNow - snapshot.CreatedAtUtc <= CandidateSnapshotMaxAge)
        {
            return snapshot;
        }

        lock (_snapshotRefreshLock)
        {
            // Re-check under the lock: a competing thread may have completed a refresh while we waited.
            // Publishing the winner's snapshot makes the rest of the batch a no-op.
            snapshot = _cachedSnapshot;
            if (snapshot is not null && DateTime.UtcNow - snapshot.CreatedAtUtc <= CandidateSnapshotMaxAge)
            {
                return snapshot;
            }

            var (candidates, seriesEpisodeCounts) = LoadCandidateItems();
            var peopleLookup = _similarityComputer.BuildCandidatePeopleLookup(candidates);
            var boxSetLookup = BuildCandidateBoxSetLookupFresh(candidates);
            var contentAffinityLookup = BuildCandidateContentAffinityLookup(candidates);

            // Refresh the library-wide genre/studio IDF rarity table alongside the live snapshot.
            _genreStudioIdf = BuildGenreStudioIdfTable();

            // Increment inside the lock so the sequence is assigned atomically with the cache write,
            // preventing a race with the batch path's TryPublishSnapshot.
            var seq = Interlocked.Increment(ref _publicationSequence);
            var fresh = new CandidateSnapshot(
                candidates,
                peopleLookup,
                boxSetLookup,
                seriesEpisodeCounts,
                null,
                CommunityPopularityComputed: false, // live rebuild has no all-user data yet - first cold-start hit fills this in
                BatchGeneration: 0, // live-refresh writes carry BatchGeneration=0; for exploration-seed semantics only
                PublicationSequence: seq,
                ObservedSequence: seq, // built and published under the same lock; the guard never applies here
                DateTime.UtcNow,
                contentAffinityLookup);

            // Republish so subsequent live requests hit the fresh cache without re-entering the slow path. Already inside the refresh lock, so a direct assignment is safe.
            _cachedSnapshot = fresh;
            return fresh;
        }
    }

    /// <summary>
    ///     Publishes a snapshot to _cachedSnapshot while enforcing monotonic ordering by PublicationSequence.
    /// </summary>
    /// <param name="candidate">The snapshot the caller would like to publish.</param>
    private void TryPublishSnapshot(CandidateSnapshot candidate)
    {
        lock (_snapshotRefreshLock)
        {
            // Stale-guard: reject if a strictly-newer snapshot was published AFTER this candidate's builder observed the sequence counter (candidate.ObservedSequence, captured at build-start before the slow load).
            var current = _cachedSnapshot;
            if (current is not null && current.PublicationSequence > candidate.ObservedSequence)
            {
                return;
            }

            // Assign the sequence and write atomically under the lock so the batch path (which builds
            // outside the lock) cannot race a concurrent live-refresh into an out-of-order sequence.
            var seq = Interlocked.Increment(ref _publicationSequence);
            _cachedSnapshot = candidate with { PublicationSequence = seq };
        }
    }

    /// <summary>
    ///     Test seam: directly publishes a snapshot with the given sequence number, bypassing the auto-increment inside TryPublishSnapshot.
    /// </summary>
    /// <param name="publicationSequence">The exact sequence number to stamp on the snapshot.</param>
    /// <returns><c>true</c> if the snapshot was published; <c>false</c> if a newer one was already present.</returns>
    internal bool TryPublishSnapshotForTest(long publicationSequence)
    {
        var snapshot = new CandidateSnapshot(
            [], [], [], [], null, false, 0, publicationSequence, publicationSequence, DateTime.UtcNow, []);

        lock (_snapshotRefreshLock)
        {
            var current = _cachedSnapshot;
            if (current is not null && current.PublicationSequence > snapshot.PublicationSequence)
            {
                return false;
            }

            _cachedSnapshot = snapshot;
            return true;
        }
    }

    /// <summary>
    ///     Reads the community-popularity map from the snapshot, computing it on-demand when the snapshot has none and caching the result back onto _cachedSnapshot so subsequent cold-start hits get an O(1) hand-off.
    /// </summary>
    /// <param name="snapshot">The snapshot returned by <see cref="GetOrRefreshLiveSnapshot"/>.</param>
    /// <returns>
    ///     The community-popularity map, or <c>null</c> when fewer than two users have any watch
    ///     history (callers fall back to rating + recency).
    /// </returns>
    private Dictionary<Guid, int>? GetOrBuildCommunityPopularity(CandidateSnapshot snapshot)
    {
        if (snapshot.CommunityPopularityComputed)
        {
            // Already computed (may legitimately be null in single-user deployments). Reuse verbatim.
            return snapshot.CommunityPopularity;
        }

        var built = BuildCommunityPopularityForColdStart();

        // Publish the result back so subsequent cold-start requests on this snapshot skip the O(U×M) scan.
        lock (_snapshotRefreshLock)
        {
            if (ReferenceEquals(_cachedSnapshot, snapshot))
            {
                _cachedSnapshot = snapshot with
                {
                    CommunityPopularity = built,
                    CommunityPopularityComputed = true
                };
            }
        }

        return built;
    }

    /// <summary>
    ///     Computes the CollectionProgressionBoost for a candidate during live inference. Uses the pre-computed watchedBoxSetCounts for O(1) lookup instead of per-candidate parent traversal.
    /// </summary>
    /// <param name="candidateBoxSetIds">Pre-resolved BoxSet IDs for the candidate (from ResolveBoxSetIds).</param>
    /// <param name="watchedBoxSetCounts">Pre-computed BoxSet ID -> watched member count mapping.</param>
    /// <returns>A boost value between 0.0 and 1.0, or 0.0 if not in any collection.</returns>
    private static double ComputeCollectionProgressionBoostLive(
        List<Guid> candidateBoxSetIds,
        Dictionary<Guid, int> watchedBoxSetCounts)
    {
        if (watchedBoxSetCounts.Count == 0 || candidateBoxSetIds.Count == 0)
        {
            return 0.0;
        }

        // Find the best progression signal across the candidate's BoxSets. The formula is delegated
        // to EngineConstants so the training path uses the same implementation (train/serve parity).
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
    ///     Updates "Requested + Watched" status in the discovery feedback store.
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

            // Build a TMDb ID -> watched set per user.
            // Query library items once and resolve their TMDb provider IDs,
            // then cross-reference with each user's watched items.
            var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
            var profileById = new Dictionary<Guid, UserWatchProfile>(allProfiles.Count);
            foreach (var p in allProfiles)
            {
                profileById.TryAdd(p.UserId, p);
            }

            // Build Jellyfin ItemId -> TMDb ID and ItemId -> MediaType mappings from library items.
            // Only load movies + series (same as LoadCandidateItems) to avoid excessive queries.
            var libraryItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series]
            });

            BuildTmdbMappings(libraryItems, cancellationToken, out var tmdbIdByItemId, out var mediaTypeByItemId);

            if (tmdbIdByItemId.Count == 0)
            {
                return;
            }

            // For each user in the feedback store, resolve which (TmdbId, MediaType) they've watched
            foreach (var userId in allFeedback
                         .Where(f => f.Entries.Any(e => e.RequestedAtUtc.HasValue && !e.WasWatched))
                         .Select(userFeedback => userFeedback.UserId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateWatchedStatusForUser(userId, profileById, tmdbIdByItemId, mediaTypeByItemId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Discovery watched-status update failed (non-critical): {ex.Message}",
                _logger);
        }
    }

    /// <summary>
    ///     Builds the Jellyfin ItemId -> TMDb ID and ItemId -> MediaType mappings from the loaded library items.
    /// </summary>
    /// <param name="libraryItems">The movie + series library items.</param>
    /// <param name="cancellationToken">Token for cooperative cancellation.</param>
    /// <param name="tmdbIdByItemId">Receives the ItemId -> TMDb ID mapping.</param>
    /// <param name="mediaTypeByItemId">Receives the ItemId -> media type ("tv"/"movie") mapping.</param>
    private static void BuildTmdbMappings(
        IReadOnlyList<BaseItem> libraryItems,
        CancellationToken cancellationToken,
        out Dictionary<Guid, int> tmdbIdByItemId,
        out Dictionary<Guid, string> mediaTypeByItemId)
    {
        tmdbIdByItemId = new Dictionary<Guid, int>();
        mediaTypeByItemId = new Dictionary<Guid, string>();
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
    }

    /// <summary>
    ///     Resolves the (TmdbId, MediaType) set a single user has watched and marks matching discovery feedback entries as watched.
    /// </summary>
    /// <param name="userId">The user id to update.</param>
    /// <param name="profileById">Watch profiles keyed by user id.</param>
    /// <param name="tmdbIdByItemId">ItemId -> TMDb ID mapping.</param>
    /// <param name="mediaTypeByItemId">ItemId -> media type mapping.</param>
    private void UpdateWatchedStatusForUser(
        Guid userId,
        Dictionary<Guid, UserWatchProfile> profileById,
        Dictionary<Guid, int> tmdbIdByItemId,
        Dictionary<Guid, string> mediaTypeByItemId)
    {
        try
        {
            // Find the user's watch profile via O(1) dictionary lookup
            if (!profileById.TryGetValue(userId, out var userProfile))
            {
                return;
            }

            // Collect composite (TmdbId, MediaType) keys of items this user has watched.
            // MediaType resolved from library item type (Movie -> "movie", Series -> "tv").
            var watchedItems = CollectWatchedTmdbKeys(userProfile, tmdbIdByItemId, mediaTypeByItemId);

            if (watchedItems.Count > 0)
            {
                _discoveryFeedbackStore.MarkWatched(userId, watchedItems);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Could not update discovery watched status for user '{userId}': {ex.Message}",
                _logger);
        }
    }

    /// <summary>
    ///     Builds the composite (TmdbId, MediaType) key set for a single user's watched items and series-level favorites.
    /// </summary>
    private static HashSet<(int TmdbId, string MediaType)> CollectWatchedTmdbKeys(
        UserWatchProfile userProfile,
        Dictionary<Guid, int> tmdbIdByItemId,
        Dictionary<Guid, string> mediaTypeByItemId)
    {
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

        return watchedItems;
    }

    /// <summary>
    ///     Builds the community-popularity map (itemId -> number of users who have watched it) used by GenerateColdStartRecommendations from the current watch profiles.
    /// </summary>
    private Dictionary<Guid, int>? BuildCommunityPopularityForColdStart()
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        if (allProfiles.Count < 2)
        {
            return null;
        }

        var userSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);
        return BuildCommunityPopularityMap(userSets);
    }

    /// <summary>
    ///     Shared community-popularity computation used by both the batch path (GetAllRecommendations) and the live cold-start path (BuildCommunityPopularityForColdStart).
    /// </summary>
    /// <param name="userSets">Precomputed user watch sets (from <see cref="CollaborativeFilter.PrecomputeUserWatchSets"/>).</param>
    /// <returns>
    ///     The community-popularity map, or <c>null</c> when fewer than two users have any
    ///     watch history. Callers treat <c>null</c> as "fall back to the classic rating +
    ///     recency cold-start formula".
    /// </returns>
    private static Dictionary<Guid, int>? BuildCommunityPopularityMap(
        IReadOnlyDictionary<Guid, HashSet<Guid>> userSets)
    {
        // Require at least two users with actual watch data: PrecomputeUserWatchSets keeps empty profiles, so a plain Count > 1 on the outer dictionary would enable the prior when only one user has data (that user's own set would be "the community").
        var usersWithHistory = 0;
        foreach (var userSet in userSets.Values)
        {
            if (userSet.Count > 0 && ++usersWithHistory >= 2)
            {
                break;
            }
        }

        if (usersWithHistory < 2)
        {
            return null;
        }

        var popularity = new Dictionary<Guid, int>();
        foreach (var userSet in userSets.Values)
        {
            foreach (var itemId in userSet)
            {
                popularity.TryGetValue(itemId, out var count);
                popularity[itemId] = count + 1;
            }
        }

        return popularity;
    }

    /// <summary>
    ///     Deterministic, process-independent seed from a Guid and an integer suffix (e.g. UTC day number).
    /// </summary>
    /// <param name="id">The user (or entity) identifier.</param>
    /// <param name="suffix">A secondary integer key (UTC day number, batch generation, ...).</param>
    /// <returns>A deterministic 32-bit seed for RNG consumers.</returns>
    internal static int ComputeStableSeed(Guid id, int suffix)
    {
        // FNV-1a over the raw Guid bytes: process-stable (no hash randomisation), cheap, no external dependency.
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        unchecked
        {
            var hash = (int)2166136261u;
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= 16777619;
            }

            return hash ^ suffix;
        }
    }

    /// <summary>
    ///     Reads the persisted batch-generation counter from disk. Best-effort: missing file, bad content, or IO trouble all fall back to 0 so the engine still boots.
    /// </summary>
    private int LoadPersistedBatchGeneration()
    {
        try
        {
            var path = ResolveBatchGenerationFilePath();
            if (path is null || !File.Exists(path))
            {
                return 0;
            }

            var raw = File.ReadAllText(path);
            return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : 0;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Could not load persisted batch generation, starting at 0: {ex.Message}",
                _logger);
            return 0;
        }
    }

    /// <summary>
    ///     Writes the current batch-generation counter to disk. Best-effort: a failure here never blocks the batch - worst case is a repeated seed after the next reload, which is the state we started from.
    /// </summary>
    private void PersistBatchGeneration(int value)
    {
        try
        {
            var path = ResolveBatchGenerationFilePath();
            if (path is null)
            {
                return;
            }

            AtomicFile.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                LogCategory,
                $"Could not persist batch generation {value}: {ex.Message}",
                _logger);
        }
    }

    private static string? ResolveBatchGenerationFilePath()
    {
        // DataFolderPath accessed from Plugin.Instance; cached in constructor to be considered.
        var dataFolder = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrEmpty(dataFolder) ? null : Path.Join(dataFolder, BatchGenerationFileName);
    }

    /// <summary>
    ///     Returns true when the candidate's parental rating exceeds the user's maximum, or when the candidate has no rating at all.
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_strategy is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _trainingService.Dispose();
    }

    /// <summary>
    ///     Per-candidate, user-invariant content-affinity source data, pre-computed once per snapshot by BuildCandidateContentAffinityLookup.
    /// </summary>
    /// <param name="TmdbCollectionName">The candidate's TMDb collection (franchise) name, or null.</param>
    /// <param name="ProductionCountries">The candidate's production countries (never null; possibly empty).</param>
    /// <param name="InheritedTags">The candidate's inherited tags (never null; possibly empty).</param>
    /// <param name="SeriesStatus">The candidate's series lifecycle status, or null for non-series.</param>
    /// <param name="SeriesEndDate">The candidate's series end date, or null.</param>
    /// <param name="Writers">Distinct writer names (never null; possibly empty).</param>
    /// <param name="Billing">Billed cast/director name -> billing weight (never null; possibly empty).</param>
    /// <param name="GenreSet">
    ///     Case-insensitive set of the candidate's genres, pre-built once per candidate. Candidate-invariant
    ///     (independent of the user), so hoisting it here removes a per-(candidate × user) HashSet
    ///     allocation from the <see cref="ScoreCandidate"/> hot path. Never null; possibly empty.
    /// </param>
    /// <param name="StudioSet">
    ///     Case-insensitive set of the candidate's studios, pre-built once per candidate, or null when the
    ///     candidate has no studios. Same hoisting rationale as <paramref name="GenreSet"/>.
    /// </param>
    private sealed record CandidateContentAffinity(
        string? TmdbCollectionName,
        List<string> ProductionCountries,
        List<string> InheritedTags,
        string? SeriesStatus,
        DateTime? SeriesEndDate,
        List<string> Writers,
        Dictionary<string, double> Billing,
        HashSet<string> GenreSet,
        HashSet<string>? StudioSet);

    /// <summary>
    ///     Immutable snapshot of candidate items and their people lookup. Published/read as a single reference so concurrent readers always see a consistent pair (candidates from the same batch as the people lookup).
    /// </summary>
    /// <param name="Candidates">All candidate items from the library.</param>
    /// <param name="PeopleLookup">Item ID -> person name set mapping.</param>
    /// <param name="CandidateBoxSetLookup">
    ///     Pre-resolved BoxSet IDs per candidate. Built once during batch generation
    ///     to avoid redundant parent-hierarchy traversals across multiple users.
    ///     Only candidates that belong to at least one BoxSet are stored (sparse).
    /// </param>
    /// <param name="SeriesEpisodeCounts">
    ///     Per-series total episode count derived from the same episode query used for the
    ///     empty-series filter. Consumed by <see cref="PreferenceBuilder"/> to weight
    ///     watched-episode signals by the fraction of the series the user has actually seen.
    /// </param>
    /// <param name="CommunityPopularity">
    ///     Optional community-popularity map (itemId -> user watch count) computed once per batch and
    ///     republished onto the snapshot so live cold-start requests reuse it instead of re-scanning.
    ///     Null in two cases the <see cref="CommunityPopularityComputed"/> flag disambiguates: (1) the
    ///     compute step has not run on this snapshot yet (e.g. the live path rebuilt it without all-user
    ///     data); (2) the compute step ran and legitimately produced no map (fewer than two users with
    ///     watch history). The flag distinguishes "compute now" from "already null, do NOT recompute".
    /// </param>
    /// <param name="CommunityPopularityComputed">
    ///     True once <see cref="CommunityPopularity"/> has been derived from the current profiles. When
    ///     true and the map is null, the compute legitimately produced nothing (fewer than two users);
    ///     callers MUST NOT retry the O(U×M) scan for this snapshot's lifetime. When false, the first
    ///     cold-start hit fills it in via <see cref="GetOrBuildCommunityPopularity"/>, which republishes
    ///     onto <see cref="_cachedSnapshot"/> so subsequent hits short-circuit.
    /// </param>
    /// <param name="CreatedAtUtc">
    ///     UTC publish timestamp. With <see cref="CandidateSnapshotMaxAge"/> it bounds the reuse window
    ///     for the on-demand <see cref="GetRecommendations(Guid, int, CancellationToken)"/> path so
    ///     library mutations between batches do not leave the live path serving stale candidates.
    /// </param>
    /// <param name="BatchGeneration">
    ///     The <see cref="_batchGeneration"/> value at publication, or <c>0</c> for live-refresh
    ///     snapshots (not part of any batch lineage). Consumed by the exploration-seed derivation
    ///     (<see cref="ComputeStableSeed"/>) to keep per-user seeds stable across a batch. NOT used for
    ///     publish-ordering - see <see cref="PublicationSequence"/>.
    /// </param>
    /// <param name="PublicationSequence">
    ///     Monotonic counter incremented before every publish (batch or live-refresh). Used by
    ///     <see cref="TryPublishSnapshot"/> to reject out-of-order writes. Unlike
    ///     <see cref="BatchGeneration"/> (0 for live-refresh, letting a slow batch overwrite a newer
    ///     live-refresh), this reflects actual publish order and closes the stale-overwrite gap.
    /// </param>
    /// <param name="ObservedSequence">
    ///     Value of <see cref="_publicationSequence"/> read when this builder STARTED assembling
    ///     candidates (before the slow load, outside the publish lock). The freshness watermark the
    ///     stale-guard in <see cref="TryPublishSnapshot"/> compares against: if a concurrent publish
    ///     landed a strictly-newer sequence after this builder observed the counter, the batch is stale
    ///     and dropped. Capturing at build-start (rather than re-reading inside the lock, where it would
    ///     equal the cached value and make the guard unreachable) is what gives the guard teeth.
    /// </param>
    /// <param name="ContentAffinityLookup">Item ID -> per-candidate content-affinity source data (5 metadata fields + writers + billing), pre-computed once per snapshot.</param>
    private sealed record CandidateSnapshot(
        List<BaseItem> Candidates,
        Dictionary<Guid, HashSet<string>> PeopleLookup,
        Dictionary<Guid, List<Guid>> CandidateBoxSetLookup,
        Dictionary<Guid, int> SeriesEpisodeCounts,
        Dictionary<Guid, int>? CommunityPopularity,
        bool CommunityPopularityComputed,
        int BatchGeneration,
        long PublicationSequence,
        long ObservedSequence,
        DateTime CreatedAtUtc,
        Dictionary<Guid, CandidateContentAffinity> ContentAffinityLookup);
}
