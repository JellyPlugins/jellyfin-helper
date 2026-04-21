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
        var isDryRun = config.RecommendationsTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            "Recommendations",
            isDryRun ? "Task started (Dry Run). Recommendations will not be saved." : "Task started.",
            _logger);
        progress.Report(5);

        cancellationToken.ThrowIfCancellationRequested();

        // Train the scoring strategy using feedback from previous recommendations
        // (items recommended last run that were subsequently watched → positive signal)
        try
        {
            var previousResults = _recsCacheService.LoadResults();
            if (previousResults is { Count: > 0 })
            {
                _pluginLog.LogInfo("Recommendations", $"Training scoring strategy from {previousResults.Count} cached user results...", _logger);
                var trained = _recsEngine.TrainStrategy(previousResults);
                _pluginLog.LogInfo(
                    "Recommendations",
                    trained
                        ? "Strategy training completed."
                        : "Strategy training skipped (insufficient training data).",
                    _logger);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _pluginLog.LogWarning("Recommendations", "Strategy training failed — continuing with current weights.", ex, _logger);
        }

        progress.Report(20);
        cancellationToken.ThrowIfCancellationRequested();

        const int maxPerUser = 20;
        var results = _recsEngine.GetAllRecommendations(maxPerUser, cancellationToken);

        progress.Report(80);
        cancellationToken.ThrowIfCancellationRequested();

        var totalRecs = results.Sum(r => r.Recommendations.Count);

        if (isDryRun)
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished (Dry Run). Generated {totalRecs} recommendations for {results.Count} users. NOT saved.",
                _logger);
        }
        else
        {
            _recsCacheService.SaveResults(results);
            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished. Generated {totalRecs} recommendations for {results.Count} users. Saved to cache.",
                _logger);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}