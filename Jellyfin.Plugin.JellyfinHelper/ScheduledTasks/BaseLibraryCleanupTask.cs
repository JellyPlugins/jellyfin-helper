using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
/// Abstract base class for library cleanup tasks that follow a common execution pattern:
/// load config → log start → iterate library locations → process each location → log summary → record cleanup.
/// Concrete subclasses only need to implement the location-specific scanning and cleanup logic.
/// </summary>
public abstract class BaseLibraryCleanupTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BaseLibraryCleanupTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    protected BaseLibraryCleanupTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger logger,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService)
    {
        LibraryManager = libraryManager;
        FileSystem = fileSystem;
        PluginLog = pluginLog;
        Logger = logger;
        ConfigHelper = configHelper;
        TrackingService = trackingService;
        TrashService = trashService;
    }

    /// <summary>
    /// Gets the library manager.
    /// </summary>
    protected ILibraryManager LibraryManager { get; }

    /// <summary>
    /// Gets the file system abstraction.
    /// </summary>
    protected IFileSystem FileSystem { get; }

    /// <summary>
    /// Gets the plugin log service.
    /// </summary>
    protected IPluginLogService PluginLog { get; }

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the cleanup configuration helper.
    /// </summary>
    protected ICleanupConfigHelper ConfigHelper { get; }

    /// <summary>
    /// Gets the cleanup tracking service.
    /// </summary>
    protected ICleanupTrackingService TrackingService { get; }

    /// <summary>
    /// Gets the trash service.
    /// </summary>
    protected ITrashService TrashService { get; }

    /// <summary>
    /// Gets the task name used as log prefix (e.g. "TrickplayCleaner", "EmptyFolderCleaner").
    /// </summary>
    protected abstract string TaskName { get; }

    /// <summary>
    /// Gets the label for deleted items (e.g. "folders", "files") used in summary messages.
    /// </summary>
    protected abstract string ItemLabel { get; }

    /// <summary>
    /// Determines whether this task is currently in dry-run mode.
    /// </summary>
    /// <returns>True if dry-run mode is active; otherwise false.</returns>
    protected abstract bool IsDryRun();

    /// <summary>
    /// Processes a single library location, scanning for orphaned items and deleting/trashing them.
    /// </summary>
    /// <param name="libraryPath">The path to the library location.</param>
    /// <param name="dryRun">Whether this is a dry run (no actual deletions).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (items deleted, bytes freed).</returns>
    protected abstract (int Deleted, long BytesFreed) ProcessLocation(string libraryPath, bool dryRun, CancellationToken cancellationToken);

    /// <summary>
    /// Executes the cleanup task using the Template Method pattern.
    /// Orchestrates: config loading, start logging, library iteration, summary logging, and cleanup recording.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var dryRun = IsDryRun();
        var config = ConfigHelper.GetConfig();

        // Log task start
        if (dryRun)
        {
            PluginLog.LogInfo(TaskName, $"Task started (Dry Run). No {ItemLabel} will be deleted.", Logger);
        }
        else
        {
            PluginLog.LogInfo(TaskName, "Task started.", Logger);
        }

        // Log orphan age if configured
        if (config.OrphanMinAgeDays > 0)
        {
            PluginLog.LogInfo(TaskName, $"Orphan minimum age: {config.OrphanMinAgeDays} days", Logger);
        }

        // Log trash mode if active
        if (config.UseTrash && !dryRun)
        {
            PluginLog.LogInfo(TaskName, "Trash mode enabled. Items will be moved to trash instead of permanent deletion.", Logger);
        }

        // Get filtered library locations
        var libraryFolders = ConfigHelper.GetFilteredLibraryLocations(LibraryManager);

        int totalDeleted = 0;
        long totalBytesFreed = 0;

        // Iterate over library locations
        for (int i = 0; i < libraryFolders.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var folder = libraryFolders[i];
            PluginLog.LogDebug(TaskName, $"Scanning library folder: {folder}", Logger);
            var (deleted, bytesFreed) = ProcessLocation(folder, dryRun, cancellationToken);
            totalDeleted += deleted;
            totalBytesFreed += bytesFreed;
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        // Log summary
        if (dryRun)
        {
            PluginLog.LogInfo(TaskName, $"Task finished (Dry Run). Would have deleted {totalDeleted} {ItemLabel}.", Logger);
        }
        else
        {
            PluginLog.LogInfo(TaskName, $"Task finished. Deleted {totalDeleted} {ItemLabel}, freed {totalBytesFreed} bytes.", Logger);
        }

        // Record cleanup statistics
        if (!dryRun && totalDeleted > 0)
        {
            TrackingService.RecordCleanup(totalBytesFreed, totalDeleted, Logger);
        }

        return Task.CompletedTask;
    }
}