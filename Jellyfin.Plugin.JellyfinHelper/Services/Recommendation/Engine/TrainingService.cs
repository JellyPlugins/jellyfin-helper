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
    ///     Non-blocking gate to prevent concurrent Train() invocations.
    ///     The scheduled task serializes calls, but this guard ensures correctness
    ///     if Train() is ever invoked from multiple paths simultaneously.
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
    ///     Compares previously recommended items against current watch data.
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
    /// <param name="cancellationToken">Token to cancel the training operation.</param>
    /// <returns>True if training was performed, false if skipped.</returns>
    internal bool Train(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        IReadOnlyDictionary<Guid, int>? seriesEpisodeCounts = null,
        bool incremental = false,
        IReadOnlyDictionary<string, double>? genreStudioIdf = null,
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
            return TrainCore(strategy, previousResults, seriesEpisodeCounts, incremental, genreStudioIdf, cancellationToken);
        }
        finally
        {
            _trainGate.Release();
        }
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
        CancellationToken cancellationToken)
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        cancellationToken.ThrowIfCancellationRequested();

        // Load discovery feedback if available (Phase 4 data source).
        // Best-effort: if the store is unavailable or throws, training continues without it.
        var discoveryFeedback = LoadDiscoveryFeedback();

        // Delegate example building to the TrainingDataBuilder (includes Phase 4 discovery feedback)
        var (examples, organicCount, randomNegativeCount, discoveryCount) =
            TrainingDataBuilder.BuildExamples(previousResults, allProfiles, discoveryFeedback, seriesEpisodeCounts, genreStudioIdf, cancellationToken);

        var positiveCount = examples.Count(e => e.Label > 0.5);
        // Separate discovery from organic in the log so operators can see whether positive
        // signal comes from actual watched consumption or external Seerr requests. Previously
        // both were folded into "organic" which hid unhealthy training-data mixes.
        _pluginLog.LogInfo(
            LogSource,
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic, {randomNegativeCount} random negatives, {discoveryCount} discovery).",
            _logger);

        List<TrainingExample> trainingExamples = examples;
        if (incremental && examples.Count >= EngineConstants.IncrementalMinExamplesThreshold)
        {
            trainingExamples = BuildIncrementalTrainingExamples(previousResults, examples);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // === Held-out validation split ===
        // Reserve the most recent 10% of examples (by GeneratedAtUtc) as held-out validation, train on
        // the remaining 90%, for honest generalization metrics instead of optimistic training-set fit.
        // Fallback: with <20 examples, skip the split and train on all (metrics become training-set).
        const int minExamplesForHeldOut = 20;
        const double heldOutFraction = 0.10;

        List<TrainingExample> trainSplit;
        List<TrainingExample> heldOutSplit;

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

        // Pass the held-out slice into the strategy so the metrics it publishes (used by the
        // ensemble's quality gate + trend analyser) come from the same out-of-sample set the
        // log line below reports. This keeps the two sources of truth in sync.
        var trained = strategy is ITrainableStrategy trainable
            && trainable.Train(trainSplit, heldOutSplit.Count >= 2 ? heldOutSplit : null);

        LogTrainingOutcome(strategy, trained, trainSplit, heldOutSplit);

        return trained;
    }

    /// <summary>
    ///     Loads discovery feedback for training on a best-effort basis.
    ///     Extracted verbatim from <see cref="TrainCore"/> to reduce cognitive complexity.
    /// </summary>
    /// <returns>The loaded discovery feedback, or null when unavailable.</returns>
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
    ///     Builds the incremental training set by keeping recent examples and subsampling older ones.
    ///     Extracted verbatim from <see cref="TrainCore"/> to reduce cognitive complexity.
    /// </summary>
    /// <param name="previousResults">The recommendation results from the previous run.</param>
    /// <param name="examples">The full set of built training examples.</param>
    /// <returns>The incremental training example set.</returns>
    private List<TrainingExample> BuildIncrementalTrainingExamples(
        IReadOnlyList<RecommendationResult> previousResults,
        List<TrainingExample> examples)
    {
        List<TrainingExample> trainingExamples;
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

        if (oldExamples.Count > 0)
        {
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
            trainingExamples = combined;

            _pluginLog.LogInfo(
                LogSource,
                $"Incremental training: {newExamples.Count} new + {sampleCount} sampled old " +
                $"(from {oldExamples.Count} total old) = {trainingExamples.Count} examples.",
                _logger);
        }
        else
        {
            trainingExamples = newExamples;
        }

        return trainingExamples;
    }

    /// <summary>
    ///     Logs the ranking-metric outcome of a training run.
    ///     Extracted verbatim from <see cref="TrainCore"/> to reduce cognitive complexity.
    /// </summary>
    /// <param name="strategy">The scoring strategy that was trained.</param>
    /// <param name="trained">Whether training was performed.</param>
    /// <param name="trainSplit">The examples used for training.</param>
    /// <param name="heldOutSplit">The held-out validation examples.</param>
    private void LogTrainingOutcome(
        IScoringStrategy strategy,
        bool trained,
        List<TrainingExample> trainSplit,
        List<TrainingExample> heldOutSplit)
    {
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
    }
}
