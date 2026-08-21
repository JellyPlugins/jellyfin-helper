using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     Abstract base class for library cleanup tasks that follow a common execution pattern:
///     load config → log start → iterate library locations → process each location → log summary → record cleanup.
///     Concrete subclasses only need to implement the location-specific scanning and cleanup logic.
/// </summary>
public abstract class BaseLibraryCleanupTask
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="BaseLibraryCleanupTask" /> class.
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
    ///     Gets the library manager.
    /// </summary>
    private ILibraryManager LibraryManager { get; }

    /// <summary>
    ///     Gets the file system abstraction.
    /// </summary>
    protected IFileSystem FileSystem { get; }

    /// <summary>
    ///     Gets the plugin log service.
    /// </summary>
    protected IPluginLogService PluginLog { get; }

    /// <summary>
    ///     Gets the logger instance.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    ///     Gets the cleanup configuration helper.
    /// </summary>
    protected ICleanupConfigHelper ConfigHelper { get; }

    /// <summary>
    ///     Gets the cleanup tracking service.
    /// </summary>
    private ICleanupTrackingService TrackingService { get; }

    /// <summary>
    ///     Gets the trash service.
    /// </summary>
    protected ITrashService TrashService { get; }

    /// <summary>
    ///     Gets the task name used as log prefix (e.g. "TrickplayCleaner", "EmptyFolderCleaner").
    /// </summary>
    protected abstract string TaskName { get; }

    /// <summary>
    ///     Gets the label for deleted items (e.g. "folders", "files") used in summary messages.
    /// </summary>
    protected abstract string ItemLabel { get; }

    /// <summary>
    ///     Gets the current task mode (Activate / DryRun / Deactivate).
    /// </summary>
    /// <returns>The configured <see cref="TaskMode" />.</returns>
    protected abstract TaskMode GetTaskMode();

    /// <summary>
    ///     Determines whether this task is currently in dry-run mode.
    ///     Returns <see langword="true"/> only when <see cref="GetTaskMode"/> returns <see cref="TaskMode.DryRun"/>.
    /// </summary>
    /// <returns>True if dry-run mode is active; otherwise false.</returns>
    protected bool IsDryRun() => CleanupConfigHelper.IsDryRun(GetTaskMode());

    /// <summary>
    ///     Processes a single library location, scanning for orphaned items and deleting/trashing them.
    /// </summary>
    /// <param name="libraryPath">The path to the library location.</param>
    /// <param name="dryRun">Whether this is a dry run (no actual deletions).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (items deleted, bytes freed).</returns>
    protected abstract (int Deleted, long BytesFreed) ProcessLocation(
        string libraryPath,
        bool dryRun,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Executes the cleanup task using the Template Method pattern.
    ///     Orchestrates: config loading, start logging, library iteration, summary logging, and cleanup recording.
    ///     The synchronous scan work is offloaded to a thread-pool thread via <see cref="Task.Run(Action)" />
    ///     so the Jellyfin scheduler thread is never blocked.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the cleanup finishes.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (GetTaskMode() == TaskMode.Deactivate)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        return Task.Run(() => RunCleanup(progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Contains the synchronous scan logic executed on a thread-pool thread by <see cref="ExecuteAsync" />.
    /// </summary>
    private void RunCleanup(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var dryRun = IsDryRun();
        var config = ConfigHelper.GetConfig();

        // Log task start
        PluginLog.LogInfo(
            TaskName,
            dryRun ? $"Task started (Dry Run). No {ItemLabel} will be deleted." : "Task started.",
            Logger);

        // Log orphan age if configured
        if (config.OrphanMinAgeDays > 0)
        {
            PluginLog.LogInfo(TaskName, $"Orphan minimum age: {config.OrphanMinAgeDays} days", Logger);
        }

        // Log trash mode if active
        if (config.UseTrash && !dryRun)
        {
            PluginLog.LogInfo(
                TaskName,
                "Trash mode enabled. Items will be moved to trash instead of permanent deletion.",
                Logger);
        }

        // Get filtered library locations
        var libraryFolders = ConfigHelper.GetFilteredLibraryLocations(LibraryManager);

        if (libraryFolders.Count == 0)
        {
            PluginLog.LogInfo(TaskName, "No library folders configured. Nothing to do.", Logger);
            progress.Report(100);
            return;
        }

        var totalDeleted = 0;
        long totalBytesFreed = 0;

        // Iterate over library locations
        for (var i = 0; i < libraryFolders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folder = libraryFolders[i];
            PluginLog.LogDebug(TaskName, $"Scanning library folder: {folder}", Logger);
            var (deleted, bytesFreed) = ProcessLocation(folder, dryRun, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            totalDeleted += deleted;
            totalBytesFreed += bytesFreed;
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        // Log summary
        PluginLog.LogInfo(
            TaskName,
            dryRun
                ? $"Task finished (Dry Run). Would have deleted {totalDeleted} {ItemLabel}."
                : $"Task finished. Deleted {totalDeleted} {ItemLabel}, freed {totalBytesFreed} bytes.",
            Logger);

        // Record cleanup statistics
        if (!dryRun && totalDeleted > 0)
        {
            TrackingService.RecordCleanup(totalBytesFreed, totalDeleted, Logger);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Filesystem seams (overridable for tests).
    //
    // The symlink/junction guards in the concrete tasks read reparse-point attributes with real
    // System.IO calls, which the mocked IFileSystem model can never trigger. Routing them through
    // these thin wrappers lets a test subclass drive the guard branches deterministically without
    // creating real symlinks (which require elevated privileges, unavailable in CI). Production
    // always runs the real System.IO implementations below.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     Determines whether <paramref name="path" /> is an existing reparse point
    ///     (symbolic link or junction).
    /// </summary>
    /// <param name="path">The directory path to inspect.</param>
    /// <returns><see langword="true" /> if the path is a reparse point; otherwise <see langword="false" />.</returns>
    protected virtual bool IsReparsePoint(string path) =>
        ReparsePointGuard.IsReparsePoint(path);

    /// <summary>
    ///     Deletes only the reparse-point link node at <paramref name="path" />, never following it
    ///     to (or deleting) its target.
    /// </summary>
    /// <param name="path">The reparse-point directory whose link node should be removed.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when <paramref name="path" /> is no longer a reparse point at deletion time
    ///     (concurrent replacement detected — fail closed to avoid deleting a real directory).
    /// </exception>
    protected virtual void DeleteReparsePointLinkNode(string path) =>
        ReparsePointGuard.DeleteLinkNode(path, InvokeDirectoryDelete);

    /// <summary>
    ///     Thin seam around <see cref="DirectoryInfo.Delete()" />. This is a zero-logic passthrough
    ///     to a single BCL call with no branching or error mapping of our own — the guard logic that
    ///     protects it lives in <see cref="ReparsePointGuard.DeleteLinkNode" /> and is fully unit
    ///     tested via this seam being overridden. Excluded from coverage because the only way to run
    ///     the real body is against an actual reparse-point node on disk, which needs
    ///     junction/symlink creation privileges not available in the unit-test environment; a test
    ///     would assert nothing beyond "DirectoryInfo.Delete was invoked".
    /// </summary>
    /// <param name="info">The <see cref="DirectoryInfo" /> whose link node should be removed.</param>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    protected virtual void InvokeDirectoryDelete(DirectoryInfo info) => info.Delete();
}