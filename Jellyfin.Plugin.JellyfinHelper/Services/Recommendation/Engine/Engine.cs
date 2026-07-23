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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Recommendation engine orchestrator. Delegates to specialized components.
/// </summary>
public sealed class Engine : IRecommendationEngine, IDisposable
{
    // File name for the persisted batch-generation counter. Sits in the plugin data folder.
    private const string BatchGenerationFileName = "jellyfin-helper-batch-generation.txt";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Engine> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly SimilarityComputer _similarityComputer;
    private readonly IScoringStrategy _strategy;
    private readonly IStrategySelector _strategySelector;
    private readonly TrainingService _trainingService;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IDiscoveryFeedbackStore _discoveryFeedbackStore;

    // Short-lived candidate-metadata cache — NOT a recommendation-result cache. Holds the
    // library-derived working set (candidate BaseItems, people lookup, BoxSet membership,
    // series episode counts) that is expensive to rebuild via the LibraryManager. Populated
    // by GetAllRecommendations (scheduled batch, both Activate and DryRun modes) and reused
    // by on-demand GetRecommendations calls until either the next batch run overwrites it or
    // the TTL expires (whichever comes first).
    //
    // DryRun mode interaction: the DryRun scheduler still calls GetAllRecommendations to build
    // this snapshot even though it never persists results to disk (see
    // Api/RecommendationController.GetAllRecommendations + ScheduledTasks/RecommendationsTask).
    // The API path calls GetAllRecommendations again when the browser cache is empty; the
    // snapshot lets that regeneration skip LoadCandidateItems + BuildCandidatePeopleLookup +
    // BuildCandidateBoxSetLookupFresh. The TTL makes sure a stale snapshot never survives a
    // library metadata refresh long enough to serve users obsolete BaseItem references.
    //
    // The TTL is orthogonal to the DryRun preview semantics: at inference time we always score
    // against whatever the snapshot currently holds, and expiring it forces a fresh library
    // scan on the next call — which is exactly the behaviour DryRun users want when they add
    // a new film and check "what would happen if I activated recommendations".
    // Single-flight gate for on-demand snapshot rebuilds. When the cache is empty or expired
    // several concurrent live requests would otherwise each trigger their own LoadCandidateItems /
    // BuildCandidatePeopleLookup / BuildCandidateBoxSetLookupFresh pass, hammering the library
    // manager in lock-step. The gate serialises the FIRST rebuild; every waiter that arrives while
    // the winner is materialising reads the freshly published snapshot instead of doing its own
    // scan. The scheduled batch path (GetAllRecommendations) intentionally bypasses this gate — it
    // is the authoritative source of the snapshot and must not defer to a stale live-path build.
    // Declared before _cachedSnapshot so StyleCop's readonly-fields-first ordering (SA1214) is
    // satisfied — the two fields are semantically paired.
    private readonly object _snapshotRefreshLock = new();

    // Stored as a single immutable snapshot to prevent concurrent readers from mixing data across batches.
    private volatile CandidateSnapshot? _cachedSnapshot;

    private static readonly TimeSpan CandidateSnapshotMaxAge = TimeSpan.FromMinutes(30);

    // Monotonic counter incremented once per GetAllRecommendations invocation. Snapshotted before the
    // parallel scoring loop so every user in the same batch shares the same batchGeneration value,
    // making the exploration seed deterministic per (user, batch) pair.
    private int _batchGeneration;

    // Monotonic publish-order counter, incremented immediately before EVERY snapshot publish (batch
    // or live-refresh). Unlike _batchGeneration (which tracks batch-start order and stays at 0 for
    // live-refresh writes), this sequence reflects the ACTUAL publish order and is used by
    // TryPublishSnapshot to decide freshness. Without it a long-running batch that started before a
    // live-refresh could still overwrite the fresher live-refresh snapshot on completion, because
    // its BatchGeneration >= 1 outranks the live-refresh's BatchGeneration = 0.
    private long _publicationSequence;

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

        // Live requests get an exploration seed keyed to (userId, current UTC day). This keeps
        // successive same-day requests deterministic without freezing exploration across days.
        // Uses a stable, process-independent hash: System.HashCode is randomised per-process,
        // so a Jellyfin restart within the same UTC day would otherwise reshuffle a user's
        // exploration slot even though (userId, dayNumber) is unchanged.
        var liveSeed = ComputeStableSeed(userId, DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);

        // Read the snapshot once and expire it when it exceeds CandidateSnapshotMaxAge. Snapshot
        // fields reference JF domain objects whose Genres/Studios/CommunityRating may mutate via
        // metadata refresh between batches, and new library additions would otherwise stay
        // invisible until the next scheduled run.
        //
        // Single-flight refresh: when the cache is empty or expired, GetOrRefreshLiveSnapshot()
        // serialises the FIRST rebuild through _snapshotRefreshLock and republishes the result
        // to _cachedSnapshot so every concurrent live request that arrives during the rebuild
        // reads the freshly published data instead of racing to re-scan the library. Without
        // this gate a batch of near-simultaneous "GetRecommendations" calls right after TTL
        // expiry would each run LoadCandidateItems + BuildCandidatePeopleLookup +
        // BuildCandidateBoxSetLookupFresh in parallel — a "stampede" that lands N heavy library
        // scans on the LibraryManager in lock-step.
        var snapshot = GetOrRefreshLiveSnapshot();

        if (userProfile.WatchedItems.Count == 0)
        {
            // Cold-start: user exists but has no watch history - return popular/trending items.
            // Reuse cached candidates from the last batch run if available to avoid redundant library queries.
            //
            // Community-popularity resolution goes through GetOrBuildCommunityPopularity so the
            // O(U×M) GetAllUserWatchProfiles + PrecomputeUserWatchSets scan runs AT MOST ONCE
            // per snapshot lifetime (typically ~30 minutes, bounded by CandidateSnapshotMaxAge).
            //
            // The previous formulation used `snapshot.CommunityPopularity ?? BuildCommunityPopularityForColdStart()`,
            // which had a fatal flaw: when the live path published a snapshot (which cannot compute
            // the community map — it does not have all-user data at that moment), CommunityPopularity
            // was null, and EVERY subsequent cold-start hit ran the full BuildCommunityPopularityForColdStart
            // scan again. In a single-user or empty-history deployment the helper legitimately returns null,
            // meaning the same null-then-recompute-yields-null cycle repeated on every HTTP request.
            //
            // Publish the compute result (even a null) back onto the snapshot with an explicit
            // `CommunityPopularityComputed = true` marker, so subsequent calls short-circuit. See
            // GetOrBuildCommunityPopularity for the read-back-and-republish protocol.
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

        // Live path now always sees a valid, published snapshot (built either by the batch or by
        // the single-flight refresh above). We can therefore skip the fall-back "load fresh"
        // branches entirely.
        var candidates = snapshot.Candidates;
        var seriesEpisodeCounts = snapshot.SeriesEpisodeCounts;
        var peopleLookup = snapshot.PeopleLookup;
        var boxSetLookup = snapshot.CandidateBoxSetLookup;
        var alphaOffset = _strategySelector.GetAlphaOffset(userProfile.UserId);
        // Live single-user path: no batch-scoped CollaborativeContext exists here (only the batch
        // path builds one). We therefore pass null and let GenerateForUser derive the aggregates
        // locally from precomputedUserSets (which is also null in the live path). The named
        // `ct:` argument is used because CollaborativeContext? sits before the CancellationToken
        // in the parameter list — CA1068 forces the CancellationToken to be the last positional
        // parameter, but we want to skip the optional context, so named-arg it is.
        return GenerateForUser(
            userProfile,
            allProfiles,
            candidates,
            peopleLookup,
            boxSetLookup,
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
        // Before training, update discovery feedback "Requested + Watched" status.
        // Resolves TMDb provider IDs from library items and cross-references with user watch history
        // to detect when a previously-requested discovery item has been added to the library and watched.
        // This upgrades the training label from 0.75 (Requested) to 0.90 (RequestedAndWatched).
        UpdateDiscoveryWatchedStatus(cancellationToken);

        // Build the per-series total-episode-count map from the live library so the training
        // path applies the EXACT same progression multiplier as inference. Without this the
        // model trains on genre/people preference vectors weighted 1.0 while it is served
        // vectors weighted 0.3–1.5 — a train/serve skew. Same source as LoadCandidateItems.
        var seriesEpisodeCounts = BuildSeriesEpisodeCounts();

        var trained = _trainingService.Train(_strategy, previousResults, seriesEpisodeCounts, incremental, cancellationToken);

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

        // Bump the batch counter once per invocation and snapshot the value so every user in this
        // batch shares the same exploration seed context. Persist immediately so the counter
        // survives plugin reloads and the first post-restart batch does not collide with the
        // very first batch of the previous process.
        var batchGeneration = Interlocked.Increment(ref _batchGeneration);
        PersistBatchGeneration(batchGeneration);

        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        var (candidates, seriesEpisodeCounts) = LoadCandidateItems();
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

        // Cache for on-demand single-user calls that may follow.
        // CreatedAtUtc drives the CandidateSnapshotMaxAge check in GetRecommendations so that a
        // long gap between scheduled batches never lets the live path serve arbitrarily stale
        // BaseItem references (which JF's metadata refresh may mutate in-place).
        // The snapshot is republished a few lines below with the community-popularity map filled in,
        // so live cold-start requests can reuse it instead of re-scanning every user's watch history.
        //
        // Publish goes through TryPublishSnapshot which serializes writes under _snapshotRefreshLock
        // AND rejects publishes from an older batch generation (an older overlapping batch that
        // finishes after a newer one must NOT clobber the newer batch's cached data).
        TryPublishSnapshot(new CandidateSnapshot(
            candidates,
            peopleLookup,
            candidateBoxSetLookup,
            seriesEpisodeCounts,
            null,
            CommunityPopularityComputed: false, // filled in by the second publish once we have all-user aggregates
            BatchGeneration: batchGeneration,
            PublicationSequence: Interlocked.Increment(ref _publicationSequence),
            DateTime.UtcNow));

        // Pre-compute all user watched-item sets ONCE for collaborative filtering.
        // Reduces O(U²×M) to O(U×M) by sharing sets across BuildCollaborativeMap calls.
        var precomputedUserSets = CollaborativeFilter.PrecomputeUserWatchSets(allProfiles);

        // Wrap the user sets in a CollaborativeContext so the itemPopularity map (O(U×M) scan)
        // and the trust-gate decision (O(U) scan) are also shared across every per-user call
        // to BuildCollaborativeMap. Without this bundle each user's invocation would re-derive
        // both aggregates from the exact same input, effectively re-imposing an O(U²×M) cost
        // that a previous review round had already flagged and reported fixed. Building the
        // context once here keeps batch cost at O(U×M) + O(U×batch-loop-work).
        var collaborativeContext = CollaborativeFilter.PrecomputeCollaborativeContext(precomputedUserSets);

        // Cold-start prior: build a community popularity map (itemId → watch count)
        // from the precomputed user sets. Passed to cold-start scoring so that new users
        // benefit from the collective "wisdom of the crowd" rather than only static
        // metadata (rating + release date). Items that many active users have watched
        // are more likely to be broadly appealing to newcomers.
        // Only built once per batch run — reused across all cold-start users.
        //
        // Delegated to BuildCommunityPopularityMap so the batch path (here) and the live
        // cold-start path (BuildCommunityPopularityForColdStart) share ONE source of truth
        // for the two-user gate and the counting loop. A previous duplication of this logic
        // in two places already drifted once during a refactor; centralising it prevents a
        // recurrence.
        var communityPopularity = BuildCommunityPopularityMap(precomputedUserSets);

        // Republish the snapshot with the community-popularity map filled in. Live cold-start
        // requests that arrive between batch runs can now read this map directly instead of
        // re-computing it (which required GetAllUserWatchProfiles + PrecomputeUserWatchSets on
        // every hit — an O(U×M) scan for every single new-user request).
        //
        // Again gated by TryPublishSnapshot so out-of-order batches (an older overlapping
        // GetAllRecommendations finishing after this one) cannot clobber the newer data.
        TryPublishSnapshot(new CandidateSnapshot(
            candidates,
            peopleLookup,
            candidateBoxSetLookup,
            seriesEpisodeCounts,
            communityPopularity,
            CommunityPopularityComputed: true, // batch path has executed the O(U×M) scan
            BatchGeneration: batchGeneration,
            PublicationSequence: Interlocked.Increment(ref _publicationSequence),
            DateTime.UtcNow));

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
                    // Combine the per-user id with the shared batch generation counter so every
                    // batch produces a fresh but user-stable exploration seed. Uses the same
                    // ComputeStableSeed helper as the live path so the seed is process-independent —
                    // a Jellyfin restart between two batches of the same generation counter would
                    // otherwise reshuffle exploration outcomes.
                    var batchSeed = ComputeStableSeed(profile.UserId, batchGeneration);
                    var result = profile.WatchedItems.Count == 0
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
        // Cold start does not need the SeriesEpisodeCounts map (progression signals require
        // watch history, which cold-start users lack by definition). Discard it explicitly.
        var candidates = preloadedCandidates ?? LoadCandidateItems().Candidates;

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
    ///     Loads all candidate items (movies and series) from the library, together with a
    ///     per-series episode count map derived from the same episode query used for the
    ///     empty-series filter (no extra DB round-trip).
    /// </summary>
    /// <returns>
    ///     Candidates and a <c>seriesId → totalEpisodeCount</c> map. The map only contains
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

        // Single pass: build both the "series has episodes" filter set AND the per-series
        // total episode count needed for the progression multiplier in PreferenceBuilder.
        // Only playable episodes (non-empty Path) are counted to keep the ratio meaningful.
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

        return (candidates, seriesEpisodeCounts);
    }

    /// <summary>
    ///     Builds the per-series total-episode-count map (SeriesId → number of playable episodes
    ///     in the library) used by <see cref="PreferenceBuilder"/>'s progression multiplier.
    ///     <para>
    ///         This is the SAME computation performed inline inside <see cref="LoadCandidateItems"/>
    ///         for the inference path, extracted so the training path (<see cref="TrainStrategy"/>)
    ///         can produce a byte-for-byte identical map. Keeping a single definition is what
    ///         guarantees train/serve parity of the progression-weighted genre/people preference
    ///         vectors — a divergent copy here would silently reintroduce the skew.
    ///     </para>
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
    ///     Collapses a flat episode list into a per-series playable-episode count. Only episodes
    ///     with a non-empty <c>Path</c> and a valid <c>SeriesId</c> are counted, matching the
    ///     candidate-filtering rule so the progression ratio (watched / total) stays meaningful.
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
    /// <param name="peopleLookup">Pre-built people lookup (item ID → person names).</param>
    /// <param name="candidateBoxSetLookup">Pre-resolved BoxSet IDs per candidate (sparse: only items in BoxSets).</param>
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
    ///     process-independent — a Jellyfin restart does not reshuffle same-day exploration.
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

        // seriesEpisodeCounts is forwarded so PreferenceBuilder can weight each episode row by
        // the fraction of the series the user has actually watched. Movies and standalone rows
        // ignore the map (missing SeriesId), so the effect is scoped to TV series preferences.
        var genrePreferences = PreferenceBuilder.BuildGenrePreferenceVector(userProfile, seriesEpisodeCounts);

        // Build O(1) candidate lookup by ID - shared across studio/tag preference building
        var candidateLookup = new Dictionary<Guid, BaseItem>(allCandidates.Count);
        foreach (var c in allCandidates)
        {
            candidateLookup.TryAdd(c.Id, c);
        }

        // Build the collaborative co-occurrence map.
        //   • Batch mode: use the precomputed CollaborativeContext so itemPopularity and the
        //     trust-gate decision are read from the shared record instead of being redone.
        //   • Live single-user mode: caller passes collaborativeContext=null, we fall back to
        //     the legacy overload which materialises the aggregates locally.
        var coOccurrence = collaborativeContext is not null
            ? CollaborativeFilter.BuildCollaborativeMap(userProfile, allProfiles, collaborativeContext)
            : CollaborativeFilter.BuildCollaborativeMap(userProfile, allProfiles, precomputedUserSets);
        // Compute the collaborative-score ceiling in a NaN-safe loop.
        // LINQ Max() propagates NaN (IEEE 754) if any entry is non-finite, which would silently
        // poison all ComputeCollaborativeScore calls for this user. Skipping non-finite values
        // ensures a degenerate Jaccard edge case cannot collapse the entire collaborative signal.
        var collaborativeMax = 0.0;
        foreach (var v in coOccurrence.Values)
        {
            if (double.IsFinite(v) && v > collaborativeMax)
            {
                collaborativeMax = v;
            }
        }

        var averageYear = ContentScoring.ComputeAverageYear(userProfile);
        var preferredStudios = PreferenceBuilder.BuildStudioPreferenceSet(userProfile, candidateLookup);
        // preferredPeople (HashSet): used by ReasonResolver to surface a concrete matched-person name
        // in recommendation reasons. Kept as an unweighted set for readable UI output.
        var preferredPeople = PreferenceBuilder.BuildPeoplePreferenceSet(userProfile, peopleLookup);
        // preferredPeopleWeights: v3 (C2) frequency-aware weighting for the ML PeopleSimilarity
        // feature. Keys are always a superset-parity match with preferredPeople (same eligibility rule),
        // but per-key weights reflect how many watched items each person appears on, so dominant
        // collaborators (e.g. a director watched 8 times) drive similarity more than one-off cameos.
        // seriesEpisodeCounts is forwarded so people from a fully-watched series contribute more
        // weight than people from a series the user abandoned after two episodes, mirroring the
        // exact same progression multiplier applied to genre preferences above.
        var preferredPeopleWeights = PreferenceBuilder.BuildPeoplePreferenceWeights(userProfile, peopleLookup, seriesEpisodeCounts);
        // Precompute the top-K average preferred weight ONCE per user so the O(P log P) sort
        // inside ComputePeopleSimilarity does not re-run for every candidate. Cuts a heavy
        // per-candidate cost down to a single per-user amortised call.
        var averagePreferredPeopleWeight = SimilarityComputer.ComputeAveragePreferredWeight(preferredPeopleWeights);
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
                    averagePreferredPeopleWeight,
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

        var topItems = DiversityReranker.ApplyDiversityReranking(scored, maxResults, explorationSeed)
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
        double averagePreferredPeopleWeight,
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
        var dateCreated = candidate.DateCreated;
        var recencyScore = ContentScoring.ComputeRecencyScore(candidate.PremiereDate ?? dateCreated);
        var libraryAddedRecency = ContentScoring.ComputeRecencyScore(dateCreated);
        var yearScore = ContentScoring.ComputeYearProximity(candidate.ProductionYear, averageYear);

        // Compute user-specific signals. Series with meaningful interaction have already been
        // excluded upstream (watchedSeriesIds filter in GenerateForUser), so every Series that
        // reaches this method is treated identically to a Movie: look the candidate up in
        // watchedItemLookup and fall back to neutral defaults when it is not present. This
        // matches the training-time neutralization performed for aggregated series examples
        // and organic standalone rows, closing a train/serve skew where the training path used
        // per-episode averages while the live path never hit the aggregation branch.
        watchedItemLookup.TryGetValue(candidate.Id, out var watchedItem);
        var hasUserInteraction = watchedItem is not null;
        var userRatingScore = ContentScoring.ComputeUserRatingScore(watchedItem);
        var completionRatio = hasUserInteraction ? ContentScoring.ComputeCompletionRatio(watchedItem) : 0.0;

        // Pre-build candidate genre/studio sets once; reused for studioMatch and ContentNearestNeighborScore.
        var candidateGenreSet = new HashSet<string>(candidate.Genres ?? [], StringComparer.OrdinalIgnoreCase);
        var candidateStudioSet = candidate.Studios is { Length: > 0 }
            ? new HashSet<string>(candidate.Studios, StringComparer.OrdinalIgnoreCase)
            : null;

        var studioMatch = candidateStudioSet is not null && candidateStudioSet.Any(preferredStudios.Contains);

        // Roadmap v3 (C2): use the weighted overload so a candidate carrying the user's
        // heavy-hitter collaborators (e.g. a director the user has watched 8 times) drives
        // similarity more than one-off cameo appearances that both the unweighted HashSet
        // and the previous overlap coefficient would treat identically.
        var peopleSimilarity = peopleLookup.TryGetValue(candidate.Id, out var candidatePeople)
            ? SimilarityComputer.ComputePeopleSimilarity(candidatePeople, preferredPeopleWeights, averagePreferredPeopleWeight)
            : 0.0;

        // Series progression boost: hardcoded 0.0 at inference. Series with meaningful episode
        // interaction are already excluded upstream by the watchedSeriesIds filter, so any series
        // that reaches this method by definition has no play/favorite signal to aggregate. The
        // training pipeline mirrors this by writing 0.0 for aggregated series examples (which live
        // scoring never re-sees) and for standalone rows, guaranteeing train/serve parity on this
        // channel. The feature slot itself is kept in CandidateFeatures so the network layout does
        // not change; the value is simply constant.
        const double seriesProgressionBoost = 0.0;

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
            // IsWeekend is resolved through TemporalFeatures.ResolveIsWeekend so that every
            // feature-vector construction site (live scoring + all four training phases) shares
            // the exact same user-anchored precedence.
            IsWeekend = TemporalFeatures.ResolveIsWeekend(userProfile),
            TagSimilarity = SimilarityComputer.ComputeTagSimilarity(candidate, preferredTags),
            LibraryAddedRecency = libraryAddedRecency,
            // Content-based nearest-neighbor: composite item-to-item similarity (genre 50%, people 30%, studio 20%)
            // against the user's most similar watched item. Captures item-level affinity as a fine-tuning signal.
            ContentNearestNeighborScore = ContentScoring.ComputeContentNearestNeighborScore(
                candidateGenreSet,
                candidatePeople,
                candidateStudioSet,
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
            peopleLookup,
            preferredPeopleWeights);

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
        catch (Exception ex) when (!ex.IsFatal())
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
        catch (Exception ex) when (!ex.IsFatal())
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
    ///     Returns the current candidate snapshot, refreshing it under a single-flight gate
    ///     when the cache is empty or has exceeded <see cref="CandidateSnapshotMaxAge"/>.
    ///     <para>
    ///         Concurrency contract: only ONE thread performs the heavy LibraryManager scan
    ///         at any given time. All other threads that arrive during the rebuild block on
    ///         <see cref="_snapshotRefreshLock"/> and, once the winner has published the new
    ///         snapshot to <see cref="_cachedSnapshot"/>, read that fresh snapshot instead of
    ///         doing their own scan. This closes the stampede window described in the class-
    ///         level cache comment: without the gate, N live requests hitting an expired
    ///         cache would trigger N parallel LoadCandidateItems + BuildCandidatePeopleLookup
    ///         + BuildCandidateBoxSetLookupFresh runs, hammering the library manager and
    ///         producing N transient snapshots (each candidate to be garbage-collected as
    ///         soon as the next one overwrites <see cref="_cachedSnapshot"/>).
    ///     </para>
    ///     <para>
    ///         Double-check inside the lock guards against the race where two threads read
    ///         "cache is null/expired" from the volatile field simultaneously — the second
    ///         thread must re-verify after acquiring the lock so that only the first one
    ///         performs the rebuild.
    ///     </para>
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
            // Re-check under the lock: a competing thread may already have completed a
            // refresh while we were waiting. Publishing the winner's snapshot is what makes
            // the rest of the batch a no-op — the "cost" of a stampede is paid at most once.
            snapshot = _cachedSnapshot;
            if (snapshot is not null && DateTime.UtcNow - snapshot.CreatedAtUtc <= CandidateSnapshotMaxAge)
            {
                return snapshot;
            }

            var (candidates, seriesEpisodeCounts) = LoadCandidateItems();
            var peopleLookup = _similarityComputer.BuildCandidatePeopleLookup(candidates);
            var boxSetLookup = BuildCandidateBoxSetLookupFresh(candidates);

            var fresh = new CandidateSnapshot(
                candidates,
                peopleLookup,
                boxSetLookup,
                seriesEpisodeCounts,
                null,
                CommunityPopularityComputed: false, // live rebuild has no all-user data yet — first cold-start hit will fill this in
                BatchGeneration: 0, // live-refresh writes carry BatchGeneration=0; kept for exploration-seed semantics only
                PublicationSequence: Interlocked.Increment(ref _publicationSequence),
                DateTime.UtcNow);

            // Republish so subsequent live requests hit the fresh cache without re-entering
            // this method's slow path. We're already inside the refresh lock; publishing the
            // reference directly is safe. TryPublishSnapshot is intentionally NOT used here
            // because we're already committed to this rebuild (we passed the double-check
            // above) and want the freshly-built snapshot to win even against an older
            // still-visible cached instance from a previous refresh.
            _cachedSnapshot = fresh;
            return fresh;
        }
    }

    /// <summary>
    ///     Publishes a snapshot to <see cref="_cachedSnapshot"/> while enforcing monotonic
    ///     ordering by <see cref="CandidateSnapshot.PublicationSequence"/>. Rejects the write
    ///     when the currently-cached snapshot has a strictly-larger publication sequence, so
    ///     an older publish that finishes late cannot clobber a newer one — regardless of
    ///     whether it originated from a batch run or a live-refresh.
    ///     <para>
    ///         Ordering is decided by <see cref="CandidateSnapshot.PublicationSequence"/>
    ///         rather than <see cref="CandidateSnapshot.BatchGeneration"/>: the batch counter
    ///         reflects batch-start order and stays at 0 for live-refresh writes, so a slow
    ///         batch that started before a live-refresh could otherwise still overwrite the
    ///         fresher live-refresh snapshot on completion. The publication sequence is
    ///         incremented immediately before every publish attempt and therefore always
    ///         reflects actual publish order, closing that gap without disturbing the
    ///         per-(user, batch) exploration-seed contract that still uses BatchGeneration.
    ///     </para>
    ///     <para>
    ///         All writes serialise through <see cref="_snapshotRefreshLock"/> so this method
    ///         is safe under concurrent calls from parallel batches or a live-refresh racing
    ///         with the batch path.
    ///     </para>
    /// </summary>
    /// <param name="candidate">The snapshot the caller would like to publish.</param>
    /// <returns>
    ///     True when the snapshot was actually published; false when a strictly-newer snapshot
    ///     was already cached and this write was skipped. Callers currently ignore the return
    ///     value (a rejected publish is silently correct behaviour) but the bool makes the
    ///     contract testable and audit-friendly.
    /// </returns>
    private bool TryPublishSnapshot(CandidateSnapshot candidate)
    {
        lock (_snapshotRefreshLock)
        {
            var current = _cachedSnapshot;
            if (current is not null && current.PublicationSequence > candidate.PublicationSequence)
            {
                // A newer publish has already landed. Reject this write so the older one
                // cannot roll the cache back to stale data.
                return false;
            }

            _cachedSnapshot = candidate;
            return true;
        }
    }

    /// <summary>
    ///     Test seam: invokes <see cref="TryPublishSnapshot"/> with a minimal snapshot carrying
    ///     only <paramref name="publicationSequence"/>. Lets unit tests exercise the publish-ordering
    ///     contract without needing to construct a full <see cref="CandidateSnapshot"/>.
    /// </summary>
    /// <param name="publicationSequence">The sequence number to publish.</param>
    /// <returns><c>true</c> if the snapshot was published; <c>false</c> if a newer one was already present.</returns>
    internal bool TryPublishSnapshotForTest(long publicationSequence)
    {
        var snapshot = new CandidateSnapshot(
            [], [], [], [], null, false, 0, publicationSequence, DateTime.UtcNow);
        return TryPublishSnapshot(snapshot);
    }

    /// <summary>
    ///     Reads the community-popularity map from the given snapshot, computing it on-demand
    ///     when the snapshot has not yet published one and caching the result back onto
    ///     <see cref="_cachedSnapshot"/> so subsequent cold-start hits get an O(1) hand-off.
    ///     <para>
    ///         Why the write-back is critical: without it, every cold-start request on a snapshot
    ///         produced by <see cref="GetOrRefreshLiveSnapshot"/> (which cannot compute the map
    ///         and therefore sets <c>CommunityPopularityComputed = false</c>,
    ///         <c>CommunityPopularity = null</c>) would call
    ///         <see cref="BuildCommunityPopularityForColdStart"/> anew, re-running
    ///         <c>GetAllUserWatchProfiles</c> + <c>PrecomputeUserWatchSets</c> — an O(U×M) scan —
    ///         on every single HTTP hit. Persisting even a <c>null</c> result (with the flag set
    ///         to <c>true</c>) short-circuits future requests during the TTL window when fewer
    ///         than two users have watch history.
    ///     </para>
    ///     <para>
    ///         Concurrency: the read of the marker and the compute itself happen without a lock
    ///         because the underlying record is immutable and a racy double-compute is harmless
    ///         (both racing threads produce the same map from the same source). The write-back
    ///         is performed under <see cref="_snapshotRefreshLock"/> so we do not overwrite a
    ///         newer batch snapshot published concurrently — we only republish if the currently
    ///         cached instance is still the one we started reading from
    ///         (<see cref="object.ReferenceEquals"/>).
    ///     </para>
    /// </summary>
    /// <param name="snapshot">The snapshot returned by <see cref="GetOrRefreshLiveSnapshot"/>.</param>
    /// <returns>
    ///     The community-popularity map for cold-start scoring, or <c>null</c> when fewer than
    ///     two users have any watch history (callers fall back to rating + recency).
    /// </returns>
    private Dictionary<Guid, int>? GetOrBuildCommunityPopularity(CandidateSnapshot snapshot)
    {
        if (snapshot.CommunityPopularityComputed)
        {
            // Already computed (may legitimately be null in single-user deployments). Reuse verbatim.
            return snapshot.CommunityPopularity;
        }

        var built = BuildCommunityPopularityForColdStart();

        // Publish the result back so subsequent cold-start requests on this snapshot skip the
        // O(U×M) scan. Guard the swap under the refresh lock and re-check that the snapshot we
        // are updating is still the currently-published one; a batch overwrite that happened
        // while we were computing would leave us stomping newer data with an outdated
        // CommunityPopularity value.
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
                catch (Exception ex) when (!ex.IsFatal())
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
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(
                "Recommendations",
                $"Discovery watched-status update failed (non-critical): {ex.Message}",
                _logger);
        }
    }

    /// <summary>
    ///     Builds the community-popularity map (itemId → number of users who have watched it)
    ///     used by <see cref="GenerateColdStartRecommendations"/> from the current watch profiles.
    ///     Matches the exact logic and two-user gate applied by
    ///     <see cref="GetAllRecommendations"/> so on-demand cold-start requests get the same
    ///     community-blended ranking that the batch path would have produced.
    ///     Returns null when fewer than two users have any watch history — callers then fall
    ///     back to the classic rating + recency formula unchanged.
    ///     <para>
    ///         Only owns the "load profiles → precompute sets" step; the actual counting and
    ///         two-user gate is delegated to <see cref="BuildCommunityPopularityMap"/> so the
    ///         batch and live paths never drift on either the gate or the counting formula.
    ///     </para>
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
    ///     Shared community-popularity computation used by both the batch path
    ///     (<see cref="GetAllRecommendations"/>) and the live cold-start path
    ///     (<see cref="BuildCommunityPopularityForColdStart"/>). Centralises the two-user gate
    ///     and the item-counting loop so a future change to either rule automatically
    ///     propagates to both callers — historically these two loops were duplicated inline
    ///     and drifted at least once during refactoring.
    ///     <para>
    ///         Gate: at least two users must contribute at least one watched item each before
    ///         the map is emitted. This prevents a single-user deployment from turning its own
    ///         watch history into "the community", which would degenerate the cold-start
    ///         blend into a self-fulfilling prophecy (recommendations weighted by the only
    ///         user's own past picks).
    ///     </para>
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
        // Guard on non-empty sets: PrecomputeUserWatchSets keeps empty profiles, so a simple
        // Count > 1 check on the outer dictionary would enable the community prior even when
        // only one user has any watch data (that user's own set would be "the community").
        // We require at least two users with actual watch data before the prior kicks in.
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
    ///     Deterministic, process-independent seed derived from a <see cref="Guid"/> and an
    ///     integer suffix (e.g. UTC day number). <see cref="HashCode.Combine{T1,T2}"/> is
    ///     randomised per-process, so the same (userId, day) tuple would map to a different
    ///     seed after each Jellyfin restart. Diversity exploration would then reshuffle within
    ///     the same day, defeating the purpose of the daily seed contract. This helper folds
    ///     the Guid's 128 bits through a stable mix and combines them with the suffix using
    ///     a fixed multiplier — no cryptographic strength required, only determinism.
    /// </summary>
    /// <param name="id">The user (or entity) identifier.</param>
    /// <param name="suffix">A secondary integer key (UTC day number, batch generation, ...).</param>
    /// <returns>A deterministic 32-bit seed for RNG consumers.</returns>
    internal static int ComputeStableSeed(Guid id, int suffix)
    {
        // FNV-1a over the raw Guid bytes: process-stable (no hash randomisation), cheap,
        // no external dependency. Guid.GetHashCode() uses SipHash on .NET 6+ and changes
        // every process restart, which would reshuffle exploration picks for the same
        // (userId, dayNumber) pair after a Jellyfin restart.
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
    ///     Reads the persisted batch-generation counter from disk. Best-effort: missing file,
    ///     bad content, or IO trouble all fall back to 0 so the engine still boots.
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
                "Recommendations",
                $"Could not load persisted batch generation, starting at 0: {ex.Message}",
                _logger);
            return 0;
        }
    }

    /// <summary>
    ///     Writes the current batch-generation counter to disk. Best-effort: a failure here
    ///     never blocks the batch — worst case is a repeated seed after the next reload,
    ///     which is the state we started from.
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
                "Recommendations",
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
    ///     Returns true when the candidate's parental rating exceeds the user's maximum,
    ///     or when the candidate has no rating at all.
    ///     <para>
    ///         <b>Policy for unrated items:</b> Items with a null
    ///         <see cref="BaseItem.InheritedParentalRatingValue"/> are treated as restricted
    ///         (excluded) for users who have a max parental rating configured. This is the
    ///         conservative safe default — recently-added or metadata-incomplete content that
    ///         has not yet received a rating from a provider will not appear in recommendations
    ///         for restricted profiles until a rating is assigned. Operators who prefer to allow
    ///         unrated content should leave the user's MaxParentalRating unset.
    ///     </para>
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
    /// <param name="SeriesEpisodeCounts">
    ///     Per-series total episode count derived from the same episode query used for the
    ///     empty-series filter. Consumed by <see cref="PreferenceBuilder"/> to weight
    ///     watched-episode signals by the fraction of the series the user has actually seen.
    /// </param>
    /// <param name="CommunityPopularity">
    ///     Optional community-popularity map (itemId → number of users who watched it) computed
    ///     once per batch and republished onto the snapshot so live cold-start requests reuse it
    ///     instead of re-scanning every user's watch history on every hit. Null in two very
    ///     different situations that used to be indistinguishable from a caller's point of view:
    ///     <list type="bullet">
    ///         <item>The compute step has not run yet on this snapshot (e.g. the live path just
    ///         rebuilt the snapshot but does not have all-user data at that moment).</item>
    ///         <item>The compute step has run and legitimately produced no map (fewer than two
    ///         users have any watch history — single-user or empty-history deployment).</item>
    ///     </list>
    ///     The <see cref="CommunityPopularityComputed"/> flag disambiguates these two cases so
    ///     callers can tell "not yet computed → compute now" from "already computed as null →
    ///     do NOT recompute" and skip a redundant O(U×M) scan in the latter case.
    /// </param>
    /// <param name="CommunityPopularityComputed">
    ///     True once <see cref="CommunityPopularity"/> has been derived from the current watch
    ///     profiles (either by the batch path or by the live cold-start helper). When true and
    ///     <see cref="CommunityPopularity"/> is null, the compute step legitimately produced no
    ///     map (fewer than two users with watch history); callers MUST NOT retry the O(U×M)
    ///     scan for the lifetime of this snapshot. When false, the map has never been computed
    ///     yet on this snapshot and the first cold-start hit is expected to fill it in through
    ///     <see cref="GetOrBuildCommunityPopularity"/>, which republishes the result back onto
    ///     <see cref="_cachedSnapshot"/> so subsequent hits short-circuit.
    /// </param>
    /// <param name="CreatedAtUtc">
    ///     UTC timestamp at which this snapshot was published. Used together with
    ///     <see cref="CandidateSnapshotMaxAge"/> to bound the reuse window for the on-demand
    ///     <see cref="GetRecommendations(Guid, int, CancellationToken)"/> path so that library
    ///     mutations between daily batch runs (new items, metadata refreshes) do not leave
    ///     the live path serving arbitrarily stale candidates.
    /// </param>
    /// <param name="BatchGeneration">
    ///     The <see cref="_batchGeneration"/> value at the time of publication, or <c>0</c>
    ///     for snapshots produced by the live-refresh path (which is not part of any batch
    ///     lineage). Monotonically increasing per batch. Consumed by the exploration-seed
    ///     derivation (<see cref="ComputeStableSeed"/>) to make per-user seeds stable across
    ///     the users of a single batch. NOT used for publish-ordering decisions — see
    ///     <see cref="PublicationSequence"/> for that.
    /// </param>
    /// <param name="PublicationSequence">
    ///     Monotonically increasing counter incremented immediately before every publish
    ///     (batch or live-refresh). Used by <see cref="TryPublishSnapshot"/> to reject
    ///     out-of-order writes: any publish whose sequence is strictly smaller than the
    ///     currently-cached snapshot's is silently dropped. Unlike <see cref="BatchGeneration"/>,
    ///     which is 0 for live-refresh writes and therefore lets a slow batch overwrite a
    ///     newer live-refresh, the publication sequence reflects actual publish order and
    ///     closes that stale-overwrite gap regardless of write origin.
    /// </param>
    private sealed record CandidateSnapshot(
        List<BaseItem> Candidates,
        Dictionary<Guid, HashSet<string>> PeopleLookup,
        Dictionary<Guid, List<Guid>> CandidateBoxSetLookup,
        Dictionary<Guid, int> SeriesEpisodeCounts,
        Dictionary<Guid, int>? CommunityPopularity,
        bool CommunityPopularityComputed,
        int BatchGeneration,
        long PublicationSequence,
        DateTime CreatedAtUtc);
}
