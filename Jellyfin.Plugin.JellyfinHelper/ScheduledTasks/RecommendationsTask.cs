using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     Scheduled sub-task that trains the scoring strategy from previous results
///     and generates fresh recommendations for all users.
///     Training and incremental updates only run when TaskMode is Activate.
///     DryRun mode generates recommendations but does NOT persist them or train models.
///     Deactivate mode skips the task entirely (true no-op).
/// </summary>
public class RecommendationsTask
{
    private readonly IRecommendationEngine _recsEngine;
    private readonly IRecommendationCacheService _recsCacheService;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationsTask" /> class.
    /// </summary>
    /// <param name="recsEngine">The recommendation engine.</param>
    /// <param name="recsCacheService">The recommendation cache service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public RecommendationsTask(
        IRecommendationEngine recsEngine,
        IRecommendationCacheService recsCacheService,
        IPluginLogService pluginLog,
        ILogger logger)
    {
        _recsEngine = recsEngine;
        _recsCacheService = recsCacheService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Executes the recommendations update task.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Deactivate mode: true no-op — skip all expensive work
        if (config.RecommendationsTaskMode == TaskMode.Deactivate)
        {
            _pluginLog.LogInfo("Recommendations", "Task skipped (Deactivated).", _logger);
            progress.Report(100);
            return Task.CompletedTask;
        }

        var isActive = config.RecommendationsTaskMode == TaskMode.Activate;
        var isDryRun = config.RecommendationsTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            "Recommendations",
            isDryRun
                ? "Task started (Dry Run). Recommendations will be generated but NOT saved. No model training."
                : "Task started (Active). Full training + generation + persistence.",
            _logger);
        progress.Report(5);

        cancellationToken.ThrowIfCancellationRequested();

        // Train the scoring strategy ONLY when TaskMode is Activate.
        // DryRun must NOT train because training writes ML weights to disk (side effect).
        // Incremental training is enabled only in Activate mode.
        if (isActive)
        {
            try
            {
                var previousResults = _recsCacheService.LoadResults();
                if (previousResults is { Count: > 0 })
                {
                    _pluginLog.LogInfo("Recommendations", $"Training scoring strategy from {previousResults.Count} cached user results (incremental=true)...", _logger);
                    var trained = _recsEngine.TrainStrategy(previousResults, incremental: true, cancellationToken: cancellationToken);
                    _pluginLog.LogInfo(
                        "Recommendations",
                        trained
                            ? "Strategy training completed (incremental)."
                            : "Strategy training skipped (insufficient training data).",
                        _logger);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _pluginLog.LogWarning("Recommendations", "Strategy training failed — continuing with current weights.", ex, _logger);
            }
        }
        else
        {
            _pluginLog.LogInfo("Recommendations", "Training skipped (DryRun mode — no model updates).", _logger);
        }

        progress.Report(20);
        cancellationToken.ThrowIfCancellationRequested();

        var maxPerUser = Math.Clamp(config.MaxRecommendationsPerUser, 1, 100);
        var results = _recsEngine.GetAllRecommendations(maxPerUser, cancellationToken);

        progress.Report(80);
        cancellationToken.ThrowIfCancellationRequested();

        var totalRecs = results.Sum(r => r.Recommendations.Count);

        if (isActive)
        {
            _recsCacheService.SaveResults(results);
            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished (Active). Generated {totalRecs} recommendations for {results.Count} users. Saved to cache.",
                _logger);
        }
        else
        {
            // DryRun: do NOT save results to cache — no side effects
            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished (Dry Run). Generated {totalRecs} recommendations for {results.Count} users. NOT saved.",
                _logger);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}