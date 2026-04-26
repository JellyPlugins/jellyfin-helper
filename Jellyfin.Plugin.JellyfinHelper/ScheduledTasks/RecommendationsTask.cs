using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     Scheduled sub-task that trains the scoring strategy from previous results
///     and generates fresh recommendations for all users.
///     Training, playlist sync, cache persistence, and incremental updates only run when TaskMode is Activate.
///     DryRun mode generates recommendations but does NOT persist them to disk or train models.
///     The UI fetches results on-demand via the API and caches them in the browser.
///     Deactivate mode skips the task entirely (true no-op), but cleans up any
///     previously created recommendation playlists as a best-effort step.
/// </summary>
public class RecommendationsTask
{
    private readonly IRecommendationEngine _recsEngine;
    private readonly IRecommendationCacheService _recsCacheService;
    private readonly IPluginLogService _pluginLog;
    private readonly IRecommendationPlaylistService? _playlistService;
    private readonly ILogger _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationsTask" /> class.
    /// </summary>
    /// <param name="recsEngine">The recommendation engine.</param>
    /// <param name="recsCacheService">The recommendation cache service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="playlistService">The recommendation playlist service (optional, null disables playlist sync).</param>
    /// <param name="logger">The logger instance.</param>
    public RecommendationsTask(
        IRecommendationEngine recsEngine,
        IRecommendationCacheService recsCacheService,
        IPluginLogService pluginLog,
        IRecommendationPlaylistService? playlistService,
        ILogger logger)
    {
        _recsEngine = recsEngine;
        _recsCacheService = recsCacheService;
        _pluginLog = pluginLog;
        _playlistService = playlistService;
        _logger = logger;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationsTask" /> class
    ///     without the playlist service (backward compatibility for existing callers and tests).
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
        : this(recsEngine, recsCacheService, pluginLog, null, logger)
    {
    }

    /// <summary>
    ///     Executes the recommendations update task.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public async Task ExecuteAsync(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Deactivate mode: true no-op — skip all expensive work.
        // However, clean up any previously created recommendation playlists
        // so users who switch from Activate to Deactivate don't keep stale playlists.
        if (config.RecommendationsTaskMode == TaskMode.Deactivate)
        {
            _pluginLog.LogInfo("Recommendations", "Task skipped (Deactivated).", _logger);

            if (_playlistService != null)
            {
                await CleanupOldPlaylistsAsync(cancellationToken).ConfigureAwait(false);
            }

            progress.Report(100);
            return;
        }

        var isActive = config.RecommendationsTaskMode == TaskMode.Activate;
        var isDryRun = config.RecommendationsTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            "Recommendations",
            isDryRun
                ? "Task started (Dry Run). Recommendations will be generated but NOT saved to disk. No model training."
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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
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

        var maxPerUser = Math.Clamp(config.MaxRecommendationsPerUser, 1, EngineConstants.MaxRecommendationsPerUserLimit);
        var results = _recsEngine.GetAllRecommendations(maxPerUser, cancellationToken);

        progress.Report(80);
        cancellationToken.ThrowIfCancellationRequested();

        var totalRecs = results.Sum(r => r.Recommendations.Count);

        if (isActive)
        {
            _recsCacheService.SaveResults(results);

            // Sync recommendations to Jellyfin playlists if enabled
            if (config.SyncRecommendationsToPlaylist && _playlistService != null)
            {
                try
                {
                    var syncResult = await _playlistService.UpdatePlaylistsForAllUsersAsync(results, cancellationToken).ConfigureAwait(false);
                    _pluginLog.LogInfo(
                        "Recommendations",
                        $"Playlist sync: {syncResult.PlaylistsCreated} created, {syncResult.TotalItemsAdded} items added, {syncResult.OldPlaylistsRemoved} old removed.",
                        _logger);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
                {
                    _pluginLog.LogWarning("Recommendations", "Playlist sync failed — recommendations were saved but playlists could not be updated.", ex, _logger);
                }
            }
            else if (_playlistService != null)
            {
                // Playlist sync was disabled — clean up any existing playlists from previous runs
                await CleanupOldPlaylistsAsync(cancellationToken).ConfigureAwait(false);
            }

            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished (Active). Generated {totalRecs} recommendations for {results.Count} users. Saved to cache.",
                _logger);
        }
        else
        {
            _pluginLog.LogInfo(
                "Recommendations",
                $"Task finished (Dry Run). Generated {totalRecs} recommendations for {results.Count} users. NOT saved to disk.",
                _logger);
        }

        progress.Report(100);
    }

    /// <summary>
    ///     Attempts to remove all recommendation playlists from a previous run.
    ///     This is called when playlist sync is disabled or the task is deactivated
    ///     to clean up stale playlists.
    ///     Errors are logged but do not fail the task.
    /// </summary>
    private async Task CleanupOldPlaylistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await _playlistService!.RemoveAllRecommendationPlaylistsAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                _pluginLog.LogInfo("Recommendations", $"Cleaned up {removed} old recommendation playlists (sync disabled).", _logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            _pluginLog.LogWarning("Recommendations", "Failed to clean up old recommendation playlists.", ex, _logger);
        }
    }
}