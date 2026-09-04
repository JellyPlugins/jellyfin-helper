using System;
using System.Collections.Generic;
using System.Linq;
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

            // Build the pooled examples once; the global pass and every per-user pass share this exact set.
            var pooled = BuildTrainingExamples(
                globalEnsemble, previousResults, seriesEpisodeCounts, incremental, genreStudioIdf, libraryItemMetadata, cancellationToken);

            // Global pass: trains the learned model, the neural MLP (once), and the global blend state.
            var globalTrained = TrainStrategyOnExamples(globalEnsemble, pooled, trainNeural: true, cancellationToken);

            TrainPerUserModels(registry, pooled, cancellationToken);

            return globalTrained;
        }
        finally
        {
            _trainGate.Release();
        }
    }

    /// <summary>
    ///     Trains one dedicated learned model per user whose example count clears the per-user threshold.
    ///     Each per-user pass fits only the linear learned weights (trainNeural: false) on that user's own
    ///     examples, warm-started from the global model on first creation.
    /// </summary>
    /// <param name="registry">The per-user ensemble registry.</param>
    /// <param name="pooled">The pooled training examples (tagged with UserId).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private void TrainPerUserModels(
        IPerUserEnsembleRegistry registry,
        List<TrainingExample> pooled,
        CancellationToken cancellationToken)
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
            if (userExamples.Count < PerUserEnsembleRegistry.PerUserModelThreshold)
            {
                // Below threshold: no per-user model. The user keeps scoring on the global model (registry
                // fallback), so their recommendations are unchanged rather than fit on too little data.
                skippedUsers++;
                continue;
            }

            var ensemble = registry.GetOrCreateTrainableEnsembleForUser(group.Key);
            if (TrainStrategyOnExamples(ensemble, userExamples, trainNeural: false, cancellationToken))
            {
                trainedUsers++;
            }
        }

        _pluginLog.LogInfo(
            LogSource,
            $"Per-user training: {trainedUsers} users trained, {skippedUsers} below the " +
            $"{PerUserEnsembleRegistry.PerUserModelThreshold}-example threshold (kept on the global model).",
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
        var trainingExamples = BuildTrainingExamples(
            strategy, previousResults, seriesEpisodeCounts, incremental, genreStudioIdf, libraryItemMetadata, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return TrainStrategyOnExamples(strategy, trainingExamples, trainNeural: true, cancellationToken);
    }

    /// <summary>
    ///     Builds the pooled training examples for this run (all phases + discovery feedback), logs the
    ///     composition, and applies incremental sampling when requested. Shared by the global training path
    ///     and the per-user path so both operate on the identical example set.
    /// </summary>
    /// <param name="strategy">The strategy whose feature means impute discovery features.</param>
    /// <param name="previousResults">The previous-run recommendation results.</param>
    /// <param name="seriesEpisodeCounts">Per-series episode counts for progression parity.</param>
    /// <param name="incremental">Whether to subsample older examples.</param>
    /// <param name="genreStudioIdf">Library-wide genre/studio IDF rarity table.</param>
    /// <param name="libraryItemMetadata">Item metadata map from the live library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pooled training examples (post incremental sampling).</returns>
    private List<TrainingExample> BuildTrainingExamples(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts,
        bool incremental,
        IReadOnlyDictionary<string, double>? genreStudioIdf,
        LibraryItemMetadata? libraryItemMetadata,
        CancellationToken cancellationToken)
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        cancellationToken.ThrowIfCancellationRequested();

        // Load discovery feedback if available (Phase 4 data source).
        // Best-effort: if the store is unavailable or throws, training continues without it.
        var discoveryFeedback = LoadDiscoveryFeedback();

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

        var positiveCount = examples.Count(e => e.Label > 0.5);
        // Separate discovery from organic in the log so operators can see whether positive signal comes from actual watched consumption or external Seerr requests.
        _pluginLog.LogInfo(
            LogSource,
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic, {randomNegativeCount} random negatives, {discoveryCount} discovery).",
            _logger);

        return incremental && examples.Count >= EngineConstants.IncrementalMinExamplesThreshold
            ? ApplyIncrementalSampling(examples, previousResults)
            : examples;
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
    /// <returns>True if training was performed, false if insufficient data.</returns>
    private bool TrainStrategyOnExamples(
        IScoringStrategy strategy,
        List<TrainingExample> trainingExamples,
        bool trainNeural,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reserve the most recent 10% of examples (by GeneratedAtUtc) as held-out validation, train on the remaining 90%, for honest generalization metrics instead of optimistic training-set fit.
        SplitHeldOut(trainingExamples, out var trainSplit, out var heldOutSplit);

        // Pass the held-out slice into the strategy so the metrics it publishes (used by the ensemble's quality gate + trend analyser) come from the same out-of-sample set the log line below reports.
        var heldOutForMetrics = heldOutSplit.Count >= 2 ? heldOutSplit : null;
        var trained = strategy switch
        {
            EnsembleScoringStrategy ensemble => ensemble.Train(trainSplit, heldOutForMetrics, trainNeural),
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
    ///     Applies incremental-training example selection: keeps recent examples and reservoir-samples a fraction of the older ones.
    /// </summary>
    /// <param name="examples">All built training examples.</param>
    /// <param name="previousResults">The previous-run recommendation results (for the recency cutoff).</param>
    /// <returns>The selected training examples for the incremental run.</returns>
    private List<TrainingExample> ApplyIncrementalSampling(
        List<TrainingExample> examples,
        IReadOnlyList<RecommendationResult> previousResults)
    {
        var latestGeneratedAt = previousResults.Max(r => (DateTime?)r.GeneratedAt) ?? DateTime.UtcNow;
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

        var rng = new Random(Engine.ComputeStableSeed(Guid.Empty, examples.Count));
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

        _pluginLog.LogInfo(
            LogSource,
            $"Incremental training: {newExamples.Count} new + {sampleCount} sampled old " +
            $"(from {oldExamples.Count} total old) = {combined.Count} examples.",
            _logger);

        return combined;
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
}
