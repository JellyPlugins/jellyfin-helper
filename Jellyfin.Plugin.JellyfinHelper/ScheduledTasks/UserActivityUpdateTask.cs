using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     Scheduled sub-task that refreshes the user watch activity data
///     by building an activity report and persisting it to the cache.
/// </summary>
public class UserActivityUpdateTask
{
    private readonly IUserActivityInsightsService _userActivityInsightsService;
    private readonly IUserActivityCacheService _userActivityCacheService;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserActivityUpdateTask" /> class.
    /// </summary>
    /// <param name="userActivityInsightsService">The user activity insights service.</param>
    /// <param name="userActivityCacheService">The user activity cache service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public UserActivityUpdateTask(
        IUserActivityInsightsService userActivityInsightsService,
        IUserActivityCacheService userActivityCacheService,
        IPluginLogService pluginLog,
        ILogger logger)
    {
        _userActivityInsightsService = userActivityInsightsService;
        _userActivityCacheService = userActivityCacheService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Executes the user activity update task.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="taskMode">The current task mode. Only Activate persists results.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken, TaskMode taskMode = TaskMode.Activate)
    {
        // Deactivate mode: true no-op — skip all expensive work
        if (taskMode == TaskMode.Deactivate)
        {
            _pluginLog.LogInfo("UserActivity", "User activity update skipped (Deactivated).", _logger);
            progress.Report(100);
            return Task.CompletedTask;
        }

        _pluginLog.LogInfo("UserActivity", "Updating user watch activity data...", _logger);
        progress.Report(10);

        cancellationToken.ThrowIfCancellationRequested();

        var result = _userActivityInsightsService.BuildActivityReport();
        progress.Report(80);

        cancellationToken.ThrowIfCancellationRequested();

        if (taskMode == TaskMode.Activate)
        {
            _userActivityCacheService.SaveResult(result);
            _pluginLog.LogInfo(
                "UserActivity",
                $"User activity update completed (Active): {result.TotalItemsWithActivity} items, " +
                $"{result.TotalPlayCount} plays across {result.TotalUsersAnalyzed} users. Saved to cache.",
                _logger);
        }
        else if (taskMode == TaskMode.DryRun)
        {
            // DryRun: do NOT save to cache — no side effects
            _pluginLog.LogInfo(
                "UserActivity",
                $"User activity update completed (Dry Run): {result.TotalItemsWithActivity} items, " +
                $"{result.TotalPlayCount} plays across {result.TotalUsersAnalyzed} users. NOT saved.",
                _logger);
        }

        progress.Report(100);
        return Task.CompletedTask;
    }
}