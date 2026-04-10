using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

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
                _logger.LogInformation("Skipping {TaskName} (deactivated in settings).", name);
                progress.Report((double)(i + 1) / totalTasks * 100);
                continue;
            }

            string modeLabel = mode == TaskMode.DryRun ? "Dry Run" : "Active";
            _logger.LogInformation("Starting {TaskName} ({Mode})...", name, modeLabel);

            try
            {
                // Create a sub-progress that maps to our segment of the overall progress
                var subProgress = new SubProgress(progress, (double)i / totalTasks * 100, (double)(i + 1) / totalTasks * 100);
                await execute(subProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Helper Cleanup was cancelled during {TaskName}.", name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing {TaskName}. Continuing with next task.", name);
            }

            _logger.LogInformation("Finished {TaskName}.", name);
            progress.Report((double)(i + 1) / totalTasks * 100);
        }

        // Run a statistics scan at the end to refresh persisted data
        try
        {
            _logger.LogInformation("Running post-cleanup statistics scan...");
            var statsService = new MediaStatisticsService(_libraryManager, _fileSystem, _loggerFactory.CreateLogger<MediaStatisticsService>());
            var historyService = new StatisticsHistoryService(_applicationPaths, _loggerFactory.CreateLogger<StatisticsHistoryService>());
            var result = statsService.CalculateStatistics();
            historyService.SaveSnapshot(result);
            historyService.SaveLatestResult(result);
            _logger.LogInformation("Post-cleanup statistics scan completed and persisted.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to run post-cleanup statistics scan.");
        }

        _logger.LogInformation("Helper Cleanup finished.");
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
            new System.IO.Abstractions.FileSystem(),
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