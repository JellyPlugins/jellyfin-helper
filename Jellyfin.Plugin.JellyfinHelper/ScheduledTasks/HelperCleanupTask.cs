using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Strm;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
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
    private readonly IApplicationPaths _applicationPaths;
    private readonly IStatisticsCacheService _cacheService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IFileSystem _fileSystem;
    private readonly IGrowthTimelineService _growthService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HelperCleanupTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPluginLogService _pluginLog;
    private readonly IMediaStatisticsService _statisticsService;
    private readonly IStrmRepairService _strmRepairService;
    private readonly ICleanupTrackingService _trackingService;
    private readonly ITrashService _trashService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HelperCleanupTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="statisticsService">The media statistics service.</param>
    /// <param name="cacheService">The statistics cache service.</param>
    /// <param name="growthService">The growth timeline service.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    /// <param name="strmRepairService">The strm repair service.</param>
    public HelperCleanupTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        IPluginLogService pluginLog,
        ILoggerFactory loggerFactory,
        IMediaStatisticsService statisticsService,
        IStatisticsCacheService cacheService,
        IGrowthTimelineService growthService,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService,
        IStrmRepairService strmRepairService)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _applicationPaths = applicationPaths;
        _pluginLog = pluginLog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HelperCleanupTask>();
        _statisticsService = statisticsService;
        _cacheService = cacheService;
        _growthService = growthService;
        _configHelper = configHelper;
        _trackingService = trackingService;
        _trashService = trashService;
        _strmRepairService = strmRepairService;
    }

    /// <inheritdoc />
    public string Name => "Helper Cleanup";

    /// <inheritdoc />
    public string Key => "HelperCleanup";

    /// <inheritdoc />
    public string Description =>
        "Runs all configured cleanup and repair tasks sequentially (Trickplay, Empty Folders, Orphaned Subtitles, STRM Repair).";

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
            ("STRM File Repair", config.StrmRepairTaskMode, RunStrmRepair)
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
            catch (Exception ex)
            {
                _pluginLog.LogError(
                    "HelperCleanup",
                    $"Error executing {name}. Continuing with next task.",
                    ex,
                    _logger);
            }

            _pluginLog.LogInfo("HelperCleanup", $"Finished {name}.", _logger);
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
            catch (Exception ex)
            {
                _pluginLog.LogError("HelperCleanup", "Error during trash purge. Continuing.", ex, _logger);
            }
        }

        // TODO: Remove legacy history file cleanup in v1.1.0 — the file was replaced by the growth timeline in v1.0.x
        CleanupLegacyHistoryFile();

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
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
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

    /// <summary>
    ///     Deletes the legacy statistics history file that was replaced by the growth timeline.
    ///     TODO: Remove this method in v1.1.0 once all users have upgraded past v1.0.9.
    /// </summary>
    private void CleanupLegacyHistoryFile()
    {
        const string legacyFileName = "jellyfin-helper-statistics-history.json";
        var legacyFilePath = Path.Join(_applicationPaths.DataPath, legacyFileName);

        try
        {
            if (!File.Exists(legacyFilePath))
            {
                return;
            }

            File.Delete(legacyFilePath);
            _pluginLog.LogInfo("HelperCleanup", $"Deleted legacy statistics history file: {legacyFilePath}", _logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                "HelperCleanup",
                $"Could not delete legacy statistics history file: {legacyFilePath}",
                ex,
                _logger);
        }
    }

    private Task RunStrmRepair(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new RepairStrmFilesTask(
            _loggerFactory.CreateLogger<RepairStrmFilesTask>(),
            _libraryManager,
            _pluginLog,
            _strmRepairService,
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