using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Handles training of scoring strategies using implicit feedback
///     from previous recommendation results and current watch data.
/// </summary>
internal sealed class TrainingService : IDisposable
{
    private const string LogSource = "Recommendations";

    /// <summary>
    ///     Non-blocking gate to prevent concurrent Train() invocations. The scheduled task serializes calls, but this guard ensures correctness if Train() is ever invoked from multiple paths simultaneously.
    /// </summary>
    private readonly SemaphoreSlim _trainGate = new(1, 1);

    private readonly IPluginLogService _pluginLog;
    private readonly ILogger _logger;
    private readonly IWatchHistoryService _watchHistoryService;
    private readonly IDiscoveryFeedbackStore? _discoveryFeedbackStore;

    internal TrainingService(
        IWatchHistoryService watchHistoryService,
        IPluginLogService pluginLog,
        ILogger logger)
        : this(watchHistoryService, discoveryFeedbackStore: null, pluginLog, logger)
    {
    }

    internal TrainingService(
        IWatchHistoryService watchHistoryService,
        IDiscoveryFeedbackStore? discoveryFeedbackStore,
        IPluginLogService pluginLog,
        ILogger logger)
    {
        _watchHistoryService = watchHistoryService;
        _discoveryFeedbackStore = discoveryFeedbackStore;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _trainGate.Dispose();
    }

    /// <summary>
    ///     Trains the active scoring strategy using implicit feedback from previous recommendations.
    /// </summary>
    /// <param name="strategy">The scoring strategy to train.</param>
    /// <param name="previousResults">The recommendation results from the previous run.</param>
    /// <param name="seriesEpisodeCounts">
    ///     Per-series total-episode-count map (SeriesId to playable episodes in the library), built
    ///     by the caller from the live library. Threaded into <see cref="TrainingDataBuilder"/> so
    ///     the training-time genre/people preference vectors apply the identical progression
    ///     multiplier used at inference, eliminating train/serve skew. May be null/empty, in which
    ///     case the builder falls back to the neutral (unweighted) path.
    /// </param>
    /// <param name="incremental">When true, subsample older examples for efficiency.</param>
    /// <param name="genreStudioIdf">
    ///     Library-wide genre/studio IDF rarity table (the SAME table used at inference), threaded in so
    ///     the GenreStudioIdfPrior feature is identical between train and serve. Null -> neutral 0.0 both sides.
    /// </param>
    /// <param name="libraryItemMetadata">
    ///     Item -> (studios/tags/BoxSet ids) map built from the live library, threaded in so watched-item
    ///     studios/tags resolve from the same source the serve path reads. Null -> cache-only behaviour
    ///     (byte-identical to before this parameter existed).
    /// </param>
    /// <param name="cancellationToken">Token to cancel the training operation.</param>
    /// <returns>True if training was performed, false if skipped.</returns>
    internal bool Train(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts = null,
        bool incremental = false,
        IReadOnlyDictionary<string, double>? genreStudioIdf = null,
        LibraryItemMetadata? libraryItemMetadata = null,
        CancellationToken cancellationToken = default)
    {
        if (previousResults.Count == 0)
        {
            _pluginLog.LogInfo(LogSource, "Training skipped - no previous recommendations available.", _logger);
            return false;
        }

        // Non-blocking guard: skip if another training run is already in progress.
        // Timeout=0 means non-blocking; CancellationToken not applicable for a zero-wait.
        if (!_trainGate.Wait(0, CancellationToken.None))
        {
            _pluginLog.LogInfo(
                LogSource,
                "Training skipped - another training run is already in progress.",
                _logger);
            return false;
        }

        try
        {
            return TrainCore(strategy, previousResults, seriesEpisodeCounts, incremental, genreStudioIdf, libraryItemMetadata, cancellationToken);
        }
        finally
        {
            _trainGate.Release();
        }
    }

    /// <summary>
    ///     Trains the global model once on the pooled examples (learned + neural + global blend state), then
    ///     trains a dedicated per-user learned model for every user with enough examples, warm-started from
    ///     the global model. The neural MLP is fit only in the global pass; per-user ensembles reuse it by
    ///     reference and only dose it via their own β.
    /// </summary>
    /// <param name="registry">The per-user ensemble registry (owns the global ensemble + per-user models).</param>
    /// <param name="previousResults">The previous-run recommendation results.</param>
    /// <param name="seriesEpisodeCounts">Per-series episode counts for progression parity.</param>
    /// <param name="incremental">When true, subsample older examples for efficiency.</param>
    /// <param name="genreStudioIdf">Library-wide genre/studio IDF rarity table.</param>
    /// <param name="libraryItemMetadata">Item metadata map from the live library.</param>
    /// <param name="cancellationToken">Token to cancel the training operation.</param>
    /// <returns>True if the global training pass was performed, false if skipped.</returns>
    internal bool TrainPerUser(
        IPerUserEnsembleRegistry registry,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts = null,
        bool incremental = false,
        IReadOnlyDictionary<string, double>? genreStudioIdf = null,
        LibraryItemMetadata? libraryItemMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (previousResults.Count == 0)
        {
            _pluginLog.LogInfo(LogSource, "Training skipped - no previous recommendations available.", _logger);
            return false;
        }

        if (!_trainGate.Wait(0, CancellationToken.None))
        {
            _pluginLog.LogInfo(
                LogSource,
                "Training skipped - another training run is already in progress.",
                _logger);
            return false;
        }

        try
        {
            var globalEnsemble = registry.GlobalEnsemble;

            // Build the full pooled example set once. The global pass may subsample it for efficiency, but
            // the per-user passes must see each user's real example count, so they train on this full set.
            var batch = BuildTrainingExamples(
                globalEnsemble, previousResults, seriesEpisodeCounts, genreStudioIdf, libraryItemMetadata, cancellationToken);
            var pooled = batch.Examples;

            SplitHeldOut(pooled, out var globalExamples, out var globalHoldout);
            // Pin training-only priors BEFORE this cycle's global promotion. Personal acceptance must not
            // reuse labels that just selected the global seed or shared Neural version.
            var neuralPrior = globalEnsemble.GetAcceptedNeuralPrediction();
            var priorAcceptance = globalEnsemble.GetAcceptedObservationIds();
            if (priorAcceptance is null)
            {
                _pluginLog.LogInfo(LogSource, "Training skipped - exact acceptance reuse ledger is unavailable; existing models retained.", _logger);
                return false;
            }

            var priorAcceptanceIds = priorAcceptance.ToHashSet();
            var preparedModels = new Dictionary<Guid, EnsembleScoringStrategy>();
            foreach (var group in pooled.GroupBy(example => example.UserId).Where(group => group.Key != Guid.Empty))
            {
                SplitHeldOut(group.ToList(), out var personalFit, out var personalHoldout);
                if (personalFit.Where(example => example.IsObserved).Select(example => example.ObservationId).Distinct().Count() < PerUserEnsembleRegistry.PerUserModelThreshold)
                {
                    continue;
                }

                var personal = registry.GetOrCreateTrainableEnsembleForUser(group.Key);
                personal.SetSharedPredictionProvider(() => neuralPrior);
                personal.SetSeedAcceptanceObservations(personalHoldout.Select(row => row.ObservationId).Distinct().Where(priorAcceptanceIds.Contains).ToArray());
                preparedModels[group.Key] = personal;
            }

            if (incremental && globalExamples.Count >= EngineConstants.IncrementalMinExamplesThreshold)
            {
                globalExamples = ApplyIncrementalSampling(globalExamples, batch.PreviousResults);
            }

            // Global pass: trains the learned model, the neural MLP (once), and the global blend state.
            var globalTrained = TrainStrategyOnExamples(globalEnsemble, globalExamples, trainNeural: true, cancellationToken, globalHoldout);

            // Per-user passes train on the full per-user slices so incremental subsampling of the pooled set
            // never drops a user below the threshold or starves their model of their own recent history.
            TrainPerUserModels(registry, pooled, cancellationToken, preparedModels);

            return globalTrained;
        }
        finally
        {
            _trainGate.Release();
        }
    }

    /// <summary>
    ///     Retires per-user models that have gone idle beyond the configured window, independently of whether
    ///     there was anything to train on this run. Actual observed play/impression/action timestamps, not
    ///     accepted-model age or generated recommendations, determine inactivity. Unknown activity retains
    ///     the model; regular evaluations do not invent user activity or force another promotion.
    /// </summary>
    /// <param name="registry">The per-user ensemble registry.</param>
    /// <returns>The number of stale per-user models that were confirmed retired.</returns>
    public int RetireStaleModels(IPerUserEnsembleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var now = DateTime.UtcNow;
        var activity = _watchHistoryService.GetAllUserWatchProfiles().Where(profile => profile.UserId != Guid.Empty)
            .GroupBy(profile => profile.UserId).ToDictionary(group => group.Key, group => group.SelectMany(profile => new[] { profile.LastActivityDate, profile.LastSessionActivityDate }).Max());
        var feedback = LoadDiscoveryFeedback();
        if (_discoveryFeedbackStore is not null && feedback is null)
        {
            _pluginLog.LogInfo(LogSource, "Inactive-model retirement skipped - observed Discovery activity is unavailable.", _logger);
            return 0;
        }

        foreach (var result in (feedback ?? []).Where(result => activity.ContainsKey(result.UserId)))
        {
            var lastAction = result.Entries.SelectMany(entry => new[] { entry.ImpressionAtUtc, entry.RequestedAtUtc, entry.DismissedAtUtc, entry.WatchedAtUtc })
                .Where(stamp => stamp.HasValue && stamp.Value <= now).Max();
            if (lastAction > activity[result.UserId] || (activity[result.UserId] is null && lastAction.HasValue))
            {
                activity[result.UserId] = lastAction;
            }
        }

        var idleCutoff = now - TimeSpan.FromDays(EngineConstants.PerUserModelMaxIdleDays);
        var staleEvicted = registry.EvictInactiveModels(idleCutoff, activity);
        if (staleEvicted > 0)
        {
            _pluginLog.LogInfo(
                LogSource,
                $"Retired {staleEvicted} per-user model(s) after {EngineConstants.PerUserModelMaxIdleDays} days of observed inactivity; those users fall back to the global model.",
                _logger);
        }

        return staleEvicted;
    }

    /// <summary>
    ///     Trains one dedicated learned model per user whose example count clears the per-user threshold.
    ///     Each per-user pass fits only the linear learned weights (trainNeural: false) on that user's own
    ///     examples, warm-started from the global model on first creation.
    /// </summary>
    /// <param name="registry">The per-user ensemble registry.</param>
    /// <param name="pooled">The full pooled training examples (tagged with UserId, before incremental subsampling).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="preparedModels">Personal models seeded before the global promotion, when prepared by the caller.</param>
    private void TrainPerUserModels(
        IPerUserEnsembleRegistry registry,
        List<TrainingExample> pooled,
        CancellationToken cancellationToken,
        Dictionary<Guid, EnsembleScoringStrategy>? preparedModels = null)
    {
        var trainedUsers = 0;
        var skippedUsers = 0;

        foreach (var group in pooled.GroupBy(e => e.UserId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Guid.Empty groups legacy/discovery-only examples with no owning user stay folded into
            // the global model rather than spawning a bogus per-user model.
            if (group.Key == Guid.Empty)
            {
                continue;
            }

            var userExamples = group.ToList();
            SplitHeldOut(userExamples, out var userFit, out var userHoldout);
            if (userFit.Where(example => example.IsObserved).Select(example => example.ObservationId).Distinct().Count() < PerUserEnsembleRegistry.PerUserModelThreshold)
            {
                // Insufficient new fitting support is unknown, not counterevidence against an incumbent.
                // Explicit removed-user/idle retirement remains separate from a failed or sparse fit.
                skippedUsers++;
                continue;
            }

            var ensemble = preparedModels is not null && preparedModels.TryGetValue(group.Key, out var prepared)
                ? prepared
                : registry.GetOrCreateTrainableEnsembleForUser(group.Key);
            if (TrainStrategyOnExamples(ensemble, userFit, trainNeural: false, cancellationToken, userHoldout))
            {
                trainedUsers++;
            }
        }

        _pluginLog.LogInfo(
            LogSource,
            $"Per-user training: {trainedUsers} users trained, {skippedUsers} below the " +
            $"{PerUserEnsembleRegistry.PerUserModelThreshold}-example fitting threshold; existing incumbents retained on sparse input.",
            _logger);
    }

    /// <summary>
    ///     Core training logic, called under the <see cref="_trainGate"/> semaphore.
    ///     Delegates example building to <see cref="TrainingDataBuilder"/>.
    /// </summary>
    private bool TrainCore(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        bool incremental,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        LibraryItemMetadata? libraryItemMetadata,
        CancellationToken cancellationToken)
    {
        var batch = BuildTrainingExamples(
            strategy, previousResults, seriesEpisodeCounts, genreStudioIdf, libraryItemMetadata, cancellationToken);
        var trainingExamples = batch.Examples;

        var sampled = incremental && trainingExamples.Count >= EngineConstants.IncrementalMinExamplesThreshold
            ? ApplyIncrementalSampling(trainingExamples, batch.PreviousResults)
            : trainingExamples;

        cancellationToken.ThrowIfCancellationRequested();

        return TrainStrategyOnExamples(strategy, sampled, trainNeural: true, cancellationToken);
    }

    /// <summary>
    ///     Builds the pooled training examples for this run (all phases + discovery feedback) and logs the
    ///     composition. Shared by the global training path and the per-user path; incremental subsampling is
    ///     applied by the caller so the per-user path can still see each user's full example count.
    /// </summary>
    /// <param name="strategy">The strategy whose feature means impute discovery features.</param>
    /// <param name="previousResults">The previous-run recommendation results.</param>
    /// <param name="seriesEpisodeCounts">Per-series episode counts for progression parity.</param>
    /// <param name="genreStudioIdf">Library-wide genre/studio IDF rarity table.</param>
    /// <param name="libraryItemMetadata">Item metadata map from the live library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pooled examples and the same live-account recommendation partition used to build them.</returns>
    private TrainingBatch BuildTrainingExamples(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        LibraryItemMetadata? libraryItemMetadata,
        CancellationToken cancellationToken)
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        cancellationToken.ThrowIfCancellationRequested();
        // The live profile provider excludes deleted/disabled accounts. Cached feedback and
        // recommendation pools must not reintroduce those users into fitting or contrasts.
        var activeUserIds = allProfiles.Select(profile => profile.UserId).ToHashSet();
        previousResults = previousResults.Where(result => activeUserIds.Contains(result.UserId)).ToArray();

        // Load discovery feedback if available (Phase 4 data source).
        // Best-effort: if the store is unavailable or throws, training continues without it.
        var discoveryFeedback = LoadDiscoveryFeedback()?.Where(result => activeUserIds.Contains(result.UserId)).ToArray();

        // Discovery training imputes the uncomputable external-candidate features to the model's persisted
        // training-set means, so it must use the SAME means discovery inference reads (from the learned
        // sub-strategy). Null on the very first run (no standardized model yet) keeps the legacy constants.
        var featureMeans = strategy switch
        {
            EnsembleScoringStrategy ensemble => ensemble.LearnedStrategy.GetFeatureMeans(),
            LearnedScoringStrategy learned => learned.GetFeatureMeans(),
            _ => null
        };

        // Delegate example building to the TrainingDataBuilder (includes Phase 4 discovery feedback)
        var (examples, organicCount, randomNegativeCount, discoveryCount) =
            TrainingDataBuilder.BuildExamples(previousResults, allProfiles, discoveryFeedback, seriesEpisodeCounts, genreStudioIdf, libraryItemMetadata, featureMeans, cancellationToken);

        examples = TrainingMath.FilterEffectiveExamples(examples, DateTime.UtcNow, LearnedScoringStrategy.MinSampleWeight).ToList();

        var positiveCount = examples.Count(e => e.Label > 0.5);
        // Separate discovery from organic in the log so operators can see whether positive signal comes from actual watched consumption or external Seerr requests.
        _pluginLog.LogInfo(
            LogSource,
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic, {randomNegativeCount} random negatives, {discoveryCount} discovery).",
            _logger);

        return new TrainingBatch(examples, previousResults);
    }

    /// <summary>
    ///     Trains a single strategy on the supplied examples: reserves a held-out validation split, trains,
    ///     and logs ranking metrics. Extracted so the global and per-user paths share identical train + log
    ///     behaviour.
    /// </summary>
    /// <param name="strategy">The strategy to train.</param>
    /// <param name="trainingExamples">The examples to fit on (already sampled).</param>
    /// <param name="trainNeural">
    ///     Whether the ensemble should (re)train its neural sub-strategy. False on per-user passes so the MLP
    ///     is fit only once, globally.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="protectedHoldout">The pre-reserved evaluation partition, or null to split locally.</param>
    /// <returns>True if training was performed, false if insufficient data.</returns>
    private bool TrainStrategyOnExamples(
        IScoringStrategy strategy,
        List<TrainingExample> trainingExamples,
        bool trainNeural,
        CancellationToken cancellationToken,
        List<TrainingExample>? protectedHoldout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reuse the permanent user/title holdout reserved before replay and global/personal fitting.
        // Ad-hoc examples without stable identities use the legacy local split.
        List<TrainingExample> trainSplit;
        List<TrainingExample> heldOutSplit;
        if (protectedHoldout is not null)
        {
            trainSplit = trainingExamples;
            heldOutSplit = protectedHoldout;
        }
        else
        {
            SplitHeldOut(trainingExamples, out trainSplit, out heldOutSplit);
        }

        // Pass the held-out slice into the strategy so the metrics it publishes (used by the ensemble's quality gate + trend analyser) come from the same out-of-sample set the log line below reports.
        var heldOutForMetrics = heldOutSplit.Count >= 2 ? heldOutSplit : null;
        var trained = strategy switch
        {
            EnsembleScoringStrategy ensemble => ensemble.TrainValidated(trainSplit, heldOutSplit, trainNeural, cancellationToken),
            ITrainableStrategy trainable => trainable.Train(trainSplit, heldOutForMetrics),
            _ => false
        };

        if (trained)
        {
            // Compute ranking metrics on the held-out set for honest generalization assessment.
            // When no held-out split is available (small dataset), fall back to training-set metrics.
            var metricsSource = heldOutSplit.Count >= 2 ? heldOutSplit : trainSplit;
            var metricsLabel = heldOutSplit.Count >= 2 ? "validation-set" : "training-set fit";

            var (precisionAtK, recallAtK, ndcgAtK) = RankingMetrics.ComputeAll(
                metricsSource,
                strategy);

            _pluginLog.LogInfo(
                LogSource,
                $"Strategy '{strategy.Name}' training completed ({metricsLabel}) - " +
                $"P@{RankingMetrics.DefaultK}: {precisionAtK:F3}, " +
                $"R@{RankingMetrics.DefaultK}: {recallAtK:F3}, " +
                $"NDCG@{RankingMetrics.DefaultK}: {ndcgAtK:F3} " +
                $"(trained on {trainSplit.Count}, evaluated on {metricsSource.Count} examples).",
                _logger);
        }
        else
        {
            _pluginLog.LogInfo(
                LogSource,
                $"Strategy '{strategy.Name}' training skipped (insufficient training data).",
                _logger);
        }

        return trained;
    }

    /// <summary>
    ///     Loads discovery feedback for training, best-effort. Extracted verbatim from TrainCore; returns null when the store is absent or throws.
    /// </summary>
    /// <returns>The discovery feedback results, or <c>null</c>.</returns>
    private IReadOnlyList<DiscoveryFeedbackResult>? LoadDiscoveryFeedback()
    {
        IReadOnlyList<DiscoveryFeedbackResult>? discoveryFeedback = null;
        if (_discoveryFeedbackStore != null)
        {
            try
            {
                discoveryFeedback = _discoveryFeedbackStore.LoadAll();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Could not load discovery feedback for training: {ex.Message}",
                    ex,
                    _logger);
            }
        }

        return discoveryFeedback;
    }

    /// <summary>
    ///     Keeps recent examples and deterministically selects a fraction of older ones using cryptographic hash priorities.
    /// </summary>
    /// <param name="examples">All built training examples.</param>
    /// <param name="previousResults">The previous-run recommendation results (for the recency cutoff).</param>
    /// <returns>The selected training examples for the incremental run.</returns>
    private List<TrainingExample> ApplyIncrementalSampling(
        List<TrainingExample> examples,
        IReadOnlyList<RecommendationResult> previousResults)
    {
        var latestGeneratedAt = previousResults.Max(r => (DateTime?)r.GeneratedAt)
            ?? examples.Max(example => (DateTime?)example.GeneratedAtUtc)
            ?? DateTime.UtcNow;
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

        if (oldExamples.Count == 0)
        {
            return newExamples;
        }

        var batchSeed = unchecked((int)latestGeneratedAt.Ticks ^ (int)(latestGeneratedAt.Ticks >> 32) ^ examples.Count);
        var sampleCount = Math.Clamp(
            (int)(oldExamples.Count * EngineConstants.IncrementalOldSampleRatio),
            1,
            oldExamples.Count);

        var sampledOld = SelectReplayExamples(oldExamples, sampleCount, Engine.ComputeStableSeed(Guid.Empty, batchSeed));
        var combined = new List<TrainingExample>(newExamples.Count + sampleCount);
        combined.AddRange(newExamples);
        combined.AddRange(sampledOld);

        _pluginLog.LogInfo(
            LogSource,
            $"Incremental training: {newExamples.Count} new + {sampleCount} sampled old " +
            $"(from {oldExamples.Count} total old) = {combined.Count} examples.",
            _logger);

        return combined;
    }

    /// <summary>Assigns reproducible SHA-256 priorities without using a security-sensitive random generator.</summary>
    /// <param name="examples">Older observations in their existing order.</param>
    /// <param name="sampleCount">Number of distinct rows to retain.</param>
    /// <param name="batchSeed">The established deterministic batch context.</param>
    /// <returns>Selected rows without replacement; input ordering is unchanged.</returns>
    private static List<TrainingExample> SelectReplayExamples(List<TrainingExample> examples, int sampleCount, int batchSeed)
    {
        var priorities = new (ulong Priority, int Index)[examples.Count];
        Span<byte> input = stackalloc byte[24];
        Span<byte> digest = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(input, batchSeed);
        for (var i = 0; i < examples.Count; i++)
        {
            examples[i].ObservationId.TryWriteBytes(input.Slice(4, 16));
            BinaryPrimitives.WriteInt32LittleEndian(input[20..], i);
            SHA256.HashData(input, digest);
            priorities[i] = (BinaryPrimitives.ReadUInt64LittleEndian(digest), i);
        }

        Array.Sort(priorities);
        return priorities.Take(sampleCount).Select(row => examples[row.Index]).ToList();
    }

    /// <summary>
    ///     Splits the training examples into a train split and a most-recent held-out validation split.
    /// </summary>
    /// <param name="trainingExamples">The examples to split.</param>
    /// <param name="trainSplit">Receives the training split.</param>
    /// <param name="heldOutSplit">Receives the held-out validation split.</param>
    private static void SplitHeldOut(
        List<TrainingExample> trainingExamples,
        out List<TrainingExample> trainSplit,
        out List<TrainingExample> heldOutSplit)
    {
        const int minExamplesForHeldOut = 20;
        const double heldOutFraction = 0.10;

        if (trainingExamples.Any(example => example.HasStableObservationId))
        {
            // The same user/title partition is reserved before global fitting, personal warm starts and replay.
            // A later watch/request changes the outcome, not membership; validation titles are never fit as contrasts either.
            trainSplit = trainingExamples.Where(example => !example.IsPermanentHoldout).ToList();
            heldOutSplit = trainingExamples.Where(example => example.IsPermanentHoldout && example.IsObserved)
                .OrderByDescending(example => example.GeneratedAtUtc).ToList();
            return;
        }

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
    }

    private sealed record TrainingBatch(List<TrainingExample> Examples, IReadOnlyList<RecommendationResult> PreviousResults);
}
