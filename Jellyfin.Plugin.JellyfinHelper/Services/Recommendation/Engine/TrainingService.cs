using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
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
    /// <summary>
    ///     Non-blocking gate to prevent concurrent Train() invocations.
    ///     The scheduled task serializes calls, but this guard ensures correctness
    ///     if Train() is ever invoked from multiple paths simultaneously.
    /// </summary>
    private static readonly SemaphoreSlim TrainGate = new(1, 1);

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
            _pluginLog.LogInfo("Recommendations", "Training skipped - no previous recommendations available.", _logger);
            return false;
        }

        // Non-blocking guard: skip if another training run is already in progress.
        if (!TrainGate.Wait(0, CancellationToken.None))
        {
            _pluginLog.LogInfo(
                "Recommendations",
                "Training skipped - another training run is already in progress.",
                _logger);
            return false;
        }

        try
        {
            return TrainCore(strategy, previousResults, incremental, cancellationToken);
        }
        finally
        {
            TrainGate.Release();
        }
    }

    /// <summary>
    ///     Core training logic, called under the <see cref="TrainGate"/> semaphore.
    ///     Delegates example building to <see cref="TrainingDataBuilder"/>.
    /// </summary>
    private bool TrainCore(
        IScoringStrategy strategy,
        IReadOnlyList<RecommendationResult> previousResults,
        bool incremental,
        CancellationToken cancellationToken)
    {
        var allProfiles = _watchHistoryService.GetAllUserWatchProfiles();
        cancellationToken.ThrowIfCancellationRequested();

        // Delegate example building to the TrainingDataBuilder
        var dataBuilder = new TrainingDataBuilder();
        var (examples, organicCount, randomNegativeCount) =
            dataBuilder.BuildExamples(previousResults, allProfiles, cancellationToken);

        var positiveCount = examples.Count(e => e.Label > 0.5);
        _pluginLog.LogInfo(
            "Recommendations",
            $"Built {examples.Count} training examples ({positiveCount} positive, " +
            $"{examples.Count - positiveCount} negative) from {previousResults.Count} users " +
            $"({organicCount} organic, {randomNegativeCount} random negatives).",
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

        cancellationToken.ThrowIfCancellationRequested();

        // === Held-out validation split ===
        // Reserve the most recent 10% of examples (by GeneratedAtUtc) as a held-out validation set.
        // Train only on the remaining 90%. This provides honest generalization metrics
        // instead of optimistic training-set fit numbers.
        // Fallback: if <20 examples, skip the split and train on all (metrics will be training-set).
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

        var trained = (strategy is ITrainableStrategy trainable) && trainable.Train(trainSplit);

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
                "Recommendations",
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
                "Recommendations",
                $"Strategy '{strategy.Name}' training skipped (insufficient training data).",
                _logger);
        }

        return trained;
    }
}
