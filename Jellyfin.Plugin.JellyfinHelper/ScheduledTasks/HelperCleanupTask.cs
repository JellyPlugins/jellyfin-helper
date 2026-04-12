using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
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
/// A single master scheduled task that orchestrates all cleanup sub-tasks sequentially.
/// Each sub-task can be individually configured as Activate, Deactivate, or DryRun
/// via the plugin settings.
/// </summary>
public class HelperCleanupTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HelperCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HelperCleanupTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public HelperCleanupTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _applicationPaths = applicationPaths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HelperCleanupTask>();
    }

    /// <inheritdoc />
    public string Name => "Helper Cleanup";

    /// <inheritdoc />
    public string Key => "HelperCleanup";

    /// <inheritdoc />
    public string Description => "Runs all configured cleanup and repair tasks sequentially (Trickplay, Empty Folders, Orphaned Subtitles, STRM Repair).";

    /// <inheritdoc />
    public string Category => "Jellyfin Helper";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = CleanupConfigHelper.GetConfig();

        // Define sub-tasks with their mode and weight (for progress calculation)
        var subTasks = new (string Name, TaskMode Mode, Func<IProgress<double>, CancellationToken, Task> Execute)[]
        {
            ("Trickplay Cleanup", config.TrickplayTaskMode, RunTrickplayCleanup),
            ("Empty Media Folder Cleanup", config.EmptyMediaFolderTaskMode, RunEmptyMediaFolderCleanup),
            ("Orphaned Subtitle Cleanup", config.OrphanedSubtitleTaskMode, RunOrphanedSubtitleCleanup),
            ("STRM File Repair", config.StrmRepairTaskMode, RunStrmRepair),
        };

        int totalTasks = subTasks.Length;

        for (int i = 0; i < totalTasks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (name, mode, execute) = subTasks[i];

            if (mode == TaskMode.Deactivate)
            {
                PluginLogService.LogInfo("HelperCleanup", $"Skipping {name} (deactivated in settings).", _logger);
                progress.Report((double)(i + 1) / totalTasks * 100);
                continue;
            }

            string modeLabel = mode == TaskMode.DryRun ? "Dry Run" : "Active";
            PluginLogService.LogInfo("HelperCleanup", $"Starting {name} ({modeLabel})...", _logger);

            try
            {
                // Create a sub-progress that maps to our segment of the overall progress
                var subProgress = new SubProgress(progress, (double)i / totalTasks * 100, (double)(i + 1) / totalTasks * 100);
                await execute(subProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PluginLogService.LogWarning("HelperCleanup", $"Helper Cleanup was cancelled during {name}.", logger: _logger);
                throw;
            }
            catch (Exception ex)
            {
                PluginLogService.LogError("HelperCleanup", $"Error executing {name}. Continuing with next task.", ex, _logger);
            }

            PluginLogService.LogInfo("HelperCleanup", $"Finished {name}.", _logger);
            progress.Report((double)(i + 1) / totalTasks * 100);
        }

        // Purge expired trash items if trash is enabled
        if (config.UseTrash && config.TrashRetentionDays >= 0)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PluginLogService.LogInfo("HelperCleanup", $"Running trash purge (retention: {config.TrashRetentionDays} days)...", _logger);

                var libraryLocations = LibraryPathResolver.GetDistinctLibraryLocations(_libraryManager);
                long totalBytesFreed = 0;
                int totalItemsPurged = 0;

                foreach (var location in libraryLocations)
                {
                    if (string.IsNullOrWhiteSpace(config.TrashFolderPath))
                    {
                        PluginLogService.LogWarning("HelperCleanup", $"Trash purge skipped for {location}: trash folder path is empty.", logger: _logger);
                        continue;
                    }

                    var candidatePath = Path.Combine(location, config.TrashFolderPath);
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
                        PluginLogService.LogWarning("HelperCleanup", $"Trash purge skipped for {location}: resolved trash path {trashPath} is outside library root.", logger: _logger);
                        continue;
                    }

                    var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, config.TrashRetentionDays, _logger);
                    totalBytesFreed += bytesFreed;
                    totalItemsPurged += itemsPurged;
                }

                if (totalItemsPurged > 0)
                {
                    PluginLogService.LogInfo("HelperCleanup", $"Trash purge completed: {totalItemsPurged} items removed, {totalBytesFreed} bytes freed.", _logger);
                }
                else
                {
                    PluginLogService.LogInfo("HelperCleanup", "Trash purge completed: no expired items found.", _logger);
                }
            }
            catch (OperationCanceledException)
            {
                PluginLogService.LogWarning("HelperCleanup", "Helper Cleanup was cancelled during trash purge.", logger: _logger);
                throw;
            }
            catch (Exception ex)
            {
                PluginLogService.LogError("HelperCleanup", "Error during trash purge. Continuing.", ex, _logger);
            }
        }

        // Run a statistics scan at the end to refresh persisted data
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PluginLogService.LogInfo("HelperCleanup", "Running post-cleanup statistics scan...", _logger);
            var statsService = new MediaStatisticsService(_libraryManager, _fileSystem, _loggerFactory.CreateLogger<MediaStatisticsService>());
            var historyService = new StatisticsHistoryService(_applicationPaths, _loggerFactory.CreateLogger<StatisticsHistoryService>());
            var result = statsService.CalculateStatistics();
            historyService.SaveSnapshot(result);
            historyService.SaveLatestResult(result);
            PluginLogService.LogInfo("HelperCleanup", "Post-cleanup statistics scan completed and persisted.", _logger);

            // Recompute growth timeline
            cancellationToken.ThrowIfCancellationRequested();
            PluginLogService.LogInfo("HelperCleanup", "Recomputing growth timeline...", _logger);
            var growthService = new GrowthTimelineService(
                _libraryManager,
                _fileSystem,
                _applicationPaths,
                _loggerFactory.CreateLogger<GrowthTimelineService>());
            growthService.ComputeTimeline();
            PluginLogService.LogInfo("HelperCleanup", "Growth timeline recomputed and persisted.", _logger);
        }
        catch (OperationCanceledException)
        {
            PluginLogService.LogWarning("HelperCleanup", "Helper Cleanup was cancelled during post-cleanup statistics scan.", logger: _logger);
            throw;
        }
        catch (Exception ex)
        {
            PluginLogService.LogWarning("HelperCleanup", "Failed to run post-cleanup statistics scan.", ex, _logger);
        }

        PluginLogService.LogInfo("HelperCleanup", "Helper Cleanup finished.", _logger);
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
            _loggerFactory.CreateLogger<CleanTrickplayTask>());
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private Task RunEmptyMediaFolderCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new CleanEmptyMediaFoldersTask(
            _libraryManager,
            _fileSystem,
            _loggerFactory.CreateLogger<CleanEmptyMediaFoldersTask>());
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private Task RunOrphanedSubtitleCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new CleanOrphanedSubtitlesTask(
            _libraryManager,
            _fileSystem,
            _loggerFactory.CreateLogger<CleanOrphanedSubtitlesTask>());
        return task.ExecuteAsync(progress, cancellationToken);
    }

    private Task RunStrmRepair(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var task = new RepairStrmFilesTask(
            _loggerFactory.CreateLogger<RepairStrmFilesTask>(),
            _libraryManager,
            new FileSystem(),
            _loggerFactory.CreateLogger<StrmRepairService>());
        return task.ExecuteAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Helper class that maps sub-task progress (0-100) to a segment of the overall progress.
    /// </summary>
    private sealed class SubProgress : IProgress<double>
    {
        private readonly IProgress<double> _parent;
        private readonly double _start;
        private readonly double _end;

        public SubProgress(IProgress<double> parent, double start, double end)
        {
            _parent = parent;
            _start = start;
            _end = end;
        }

        public void Report(double value)
        {
            // Map 0-100 sub-progress to our segment
            double mapped = _start + (value / 100.0 * (_end - _start));
            _parent.Report(mapped);
        }
    }
}
