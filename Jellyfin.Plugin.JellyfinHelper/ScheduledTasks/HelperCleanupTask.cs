using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using IFileSystem = MediaBrowser.Model.IO.IFileSystem;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     A single master scheduled task that orchestrates all cleanup sub-tasks sequentially.
///     Each sub-task can be individually configured as Activate, Deactivate, or DryRun
///     via the plugin settings.
/// </summary>
public class HelperCleanupTask : IScheduledTask
{
    private readonly IStatisticsCacheService _cacheService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IFileSystem _fileSystem;
    private readonly IGrowthTimelineService _growthService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HelperCleanupTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPluginLogService _pluginLog;
    private readonly IRecommendationCacheService _recsCacheService;
    private readonly IRecommendationEngine _recsEngine;
    private readonly IMediaStatisticsService _statisticsService;
    private readonly ILinkRepairService _linkRepairService;
    private readonly ISeerrIntegrationService _seerrService;
    private readonly ICleanupTrackingService _trackingService;
    private readonly ITrashService _trashService;
    private readonly IUserActivityCacheService _userActivityCacheService;
    private readonly IUserActivityInsightsService _userActivityInsightsService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HelperCleanupTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="statisticsService">The media statistics service.</param>
    /// <param name="cacheService">The statistics cache service.</param>
    /// <param name="growthService">The growth timeline service.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    /// <param name="linkRepairService">The link repair service.</param>
    /// <param name="seerrService">The Seerr integration service.</param>
    /// <param name="userActivityInsightsService">The user activity insights service.</param>
    /// <param name="userActivityCacheService">The user activity cache service.</param>
    /// <param name="recsEngine">The recommendation engine.</param>
    /// <param name="recsCacheService">The recommendation cache service.</param>
    public HelperCleanupTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILoggerFactory loggerFactory,
        IMediaStatisticsService statisticsService,
        IStatisticsCacheService cacheService,
        IGrowthTimelineService growthService,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService,
        ILinkRepairService linkRepairService,
        ISeerrIntegrationService seerrService,
        IUserActivityInsightsService userActivityInsightsService,
        IUserActivityCacheService userActivityCacheService,
        IRecommendationEngine recsEngine,
        IRecommendationCacheService recsCacheService)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _pluginLog = pluginLog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HelperCleanupTask>();
        _statisticsService = statisticsService;
        _cacheService = cacheService;
        _growthService = growthService;
        _configHelper = configHelper;
        _trackingService = trackingService;
        _trashService = trashService;
        _linkRepairService = linkRepairService;
        _seerrService = seerrService;
        _userActivityInsightsService = userActivityInsightsService;
        _userActivityCacheService = userActivityCacheService;
        _recsEngine = recsEngine;
        _recsCacheService = recsCacheService;
    }

    /// <inheritdoc />
    public string Name => "Helper Cleanup";

    /// <inheritdoc />
    public string Key => "HelperCleanup";

    /// <inheritdoc />
    public string Description =>
        "Runs all configured cleanup and repair tasks sequentially (Trickplay, Empty Folders, Orphaned Subtitles, Link Repair, Seerr Cleanup, User Activity, Smart Recommendations).";

    /// <inheritdoc />
    public string Category => "Jellyfin Helper";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configHelper.GetConfig();

        // Define sub-tasks with their mode and weight (for progress calculation)
        var subTasks = new (string Name, TaskMode Mode, Func<IProgress<double>, CancellationToken, Task> Execute)[]
        {
            ("Trickplay Cleanup", config.TrickplayTaskMode, RunTrickplayCleanup),
            ("Empty Media Folder Cleanup", config.EmptyMediaFolderTaskMode, RunEmptyMediaFolderCleanup),
            ("Orphaned Subtitle Cleanup", config.OrphanedSubtitleTaskMode, RunOrphanedSubtitleCleanup),
            ("Link Repair", config.LinkRepairTaskMode, RunLinkRepair),
            ("Seerr Cleanup", config.SeerrCleanupTaskMode, (p, ct) => RunSeerrCleanup(config, p, ct)),
            ("User Watch Activity", config.RecommendationsTaskMode, RunUserActivityUpdate),
            ("Smart Recommendations", config.RecommendationsTaskMode, (p, ct) => RunRecommendationsUpdate(config, p, ct))
        };

        var totalTasks = subTasks.Length;

        for (var i = 0; i < totalTasks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (name, mode, execute) = subTasks[i];

            if (mode == TaskMode.Deactivate)
            {
                _pluginLog.LogInfo("HelperCleanup", $"Skipping {name} (deactivated in settings).", _logger);
                progress.Report((double)(i + 1) / totalTasks * 100);
                continue;
            }

            var modeLabel = mode == TaskMode.DryRun ? "Dry Run" : "Active";
            _pluginLog.LogInfo("HelperCleanup", $"Starting {name} ({modeLabel})...", _logger);

            var succeeded = true;
            try
            {
                // Create a sub-progress that maps to our segment of the overall progress
                var subProgress = new SubProgress(
                    progress,
                    (double)i / totalTasks * 100,
                    (double)(i + 1) / totalTasks * 100);
                await execute(subProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pluginLog.LogWarning("HelperCleanup", $"Helper Cleanup was cancelled during {name}.", logger: _logger);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                succeeded = false;
                _pluginLog.LogError(
                    "HelperCleanup",
                    $"Error executing {name}. Continuing with next task.",
                    ex,
                    _logger);
            }

            _pluginLog.LogInfo(
                "HelperCleanup",
                succeeded ? $"Finished {name}." : $"Finished {name} (with errors).",
                _logger);
            progress.Report((double)(i + 1) / totalTasks * 100);
        }

        // Purge expired trash items if trash is enabled
        if (config is { UseTrash: true, TrashRetentionDays: >= 0 })
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pluginLog.LogInfo(
                    "HelperCleanup",
                    $"Running trash purge (retention: {config.TrashRetentionDays} days)...",
                    _logger);

                var libraryLocations = LibraryPathResolver.GetDistinctLibraryLocations(_libraryManager);
                long totalBytesFreed = 0;
                var totalItemsPurged = 0;

                foreach (var location in libraryLocations)
                {
                    var candidatePath = _configHelper.GetTrashPath(location);
                    var libraryRoot = Path.GetFullPath(location)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var trashPath = Path.GetFullPath(candidatePath);

                    var pathComparison = OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                    var isUnderLibrary =
                        trashPath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, pathComparison);
                    if (!isUnderLibrary)
                    {
                        _pluginLog.LogWarning(
                            "HelperCleanup",
                            $"Trash purge skipped for {location}: resolved trash path {trashPath} is outside library root.",
                            logger: _logger);
                        continue;
                    }

                    var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(
                        trashPath,
                        config.TrashRetentionDays,
                        _logger);
                    totalBytesFreed += bytesFreed;
                    totalItemsPurged += itemsPurged;
                }

                _pluginLog.LogInfo(
                    "HelperCleanup",
                    totalItemsPurged > 0
                        ? $"Trash purge completed: {totalItemsPurged} items removed, {totalBytesFreed} bytes freed."
                        : "Trash purge completed: no expired items found.",
                    _logger);
            }
            catch (OperationCanceledException)
            {
                _pluginLog.LogWarning(
                    "HelperCleanup",
                    "Helper Cleanup was cancelled during trash purge.",
                    logger: _logger);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _pluginLog.LogError("HelperCleanup", "Error during trash purge. Continuing.", ex, _logger);
            }
        }

        // Run a statistics scan at the end to refresh persisted data
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _pluginLog.LogInfo("HelperCleanup", "Running post-cleanup statistics scan...", _logger);
            var result = _statisticsService.CalculateStatistics();
            _cacheService.SaveLatestResult(result);
            _pluginLog.LogInfo("HelperCleanup", "Post-cleanup statistics scan completed and persisted.", _logger);
        }
        catch (OperationCanceledException)
        {
            _pluginLog.LogWarning(
                "HelperCleanup",
                "Helper Cleanup was cancelled during post-cleanup statistics scan.",
                logger: _logger);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _pluginLog.LogWarning("HelperCleanup", "Failed to run post-cleanup statistics scan.", ex, _logger);
        }

        // Recompute growth timeline (independent of statistics scan)
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _pluginLog.LogInfo("HelperCleanup", "Recomputing growth timeline...", _logger);
            await _growthService.ComputeTimelineAsync(cancellationToken).ConfigureAwait(false);
            _pluginLog.LogInfo("HelperCleanup", "Growth timeline recomputed and persisted.", _logger);
        }
        catch (OperationCanceledException)
        {
            _pluginLog.LogWarning(
                "HelperCleanup",
                "Helper Cleanup was cancelled during growth timeline computation.",
                logger: _logger);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _pluginLog.LogWarning("HelperCleanup", "Failed to recompute growth timeline.", ex, _logger);
        }

        _pluginLog.LogInfo("HelperCleanup", "Helper Cleanup finished.", _logger);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }

    private Task RunTrickplayCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new CleanTrickplayTask(
            _libraryManager,
            _fileSystem,
            _pluginLog,
            _loggerFactory.CreateLogger<CleanTrickplayTask>(),
            _configHelper,
            _trackingService,
            _trashService);
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private Task RunEmptyMediaFolderCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new CleanEmptyMediaFoldersTask(
            _libraryManager,
            _fileSystem,
            _pluginLog,
            _loggerFactory.CreateLogger<CleanEmptyMediaFoldersTask>(),
            _configHelper,
            _trackingService,
            _trashService);
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private Task RunOrphanedSubtitleCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new CleanOrphanedSubtitlesTask(
            _libraryManager,
            _fileSystem,
            _pluginLog,
            _loggerFactory.CreateLogger<CleanOrphanedSubtitlesTask>(),
            _configHelper,
            _trackingService,
            _trashService);
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private async Task RunSeerrCleanup(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Check if Seerr is configured
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            _pluginLog.LogInfo("SeerrCleanup", "Seerr not configured. Skipping.", _logger);
            progress.Report(100);
            return;
        }

        if (config.SeerrCleanupAgeDays <= 0)
        {
            _pluginLog.LogWarning(
                "SeerrCleanup",
                $"Invalid Seerr cleanup age '{config.SeerrCleanupAgeDays}'. Skipping.",
                logger: _logger);
            progress.Report(100);
            return;
        }

        var dryRun = config.SeerrCleanupTaskMode == TaskMode.DryRun;

        _pluginLog.LogInfo(
            "SeerrCleanup",
            dryRun ? "Task started (Dry Run). No requests will be deleted." : "Task started.",
            _logger);

        _pluginLog.LogInfo(
            "SeerrCleanup",
            $"Max age: {config.SeerrCleanupAgeDays} days.",
            _logger);

        var result = await _seerrService.CleanupExpiredRequestsAsync(
            config.SeerrUrl,
            config.SeerrApiKey,
            config.SeerrCleanupAgeDays,
            dryRun,
            cancellationToken).ConfigureAwait(false);

        _pluginLog.LogInfo(
            "SeerrCleanup",
            dryRun
                ? $"Task finished (Dry Run). Checked: {result.TotalChecked}, Expired: {result.ExpiredFound}, Would delete: {result.ExpiredFound}"
                : $"Task finished. Checked: {result.TotalChecked}, Expired: {result.ExpiredFound}, Deleted: {result.Deleted}, Failed: {result.Failed}",
            _logger);

        progress.Report(100);
    }

    private Task RunUserActivityUpdate(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _pluginLog.LogInfo("HelperCleanup", "Updating user watch activity data...", _logger);
        progress.Report(10);

        cancellationToken.ThrowIfCancellationRequested();

        var result = _userActivityInsightsService.BuildActivityReport();
        progress.Report(80);

        cancellationToken.ThrowIfCancellationRequested();
        _userActivityCacheService.SaveResult(result);

        _pluginLog.LogInfo(
            "HelperCleanup",
            $"User activity update completed: {result.TotalItemsWithActivity} items, " +
            $"{result.TotalPlayCount} plays across {result.TotalUsersAnalyzed} users.",
            _logger);

        progress.Report(100);
        return Task.CompletedTask;
    }

    private Task RunRecommendationsUpdate(PluginConfiguration config, IProgress<double> progress, CancellationToken cancellationToken)
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
        var results = _recsEngine.GetAllRecommendations(maxPerUser);

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

    private Task RunLinkRepair(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new RepairLinksTask(
            _loggerFactory.CreateLogger<RepairLinksTask>(),
            _libraryManager,
            _pluginLog,
            _linkRepairService,
            _configHelper);
        return task.ExecuteAsync(progress, cancellationToken);
    }

    /// <summary>
    ///     Helper class that maps sub-task progress (0-100) to a segment of the overall progress.
    /// </summary>
    private sealed class SubProgress(IProgress<double> parent, double start, double end) : IProgress<double>
    {
        public void Report(double value)
        {
            // Map 0-100 sub-progress to our segment
            var mapped = start + (value / 100.0 * (end - start));
            parent.Report(mapped);
        }
    }
}