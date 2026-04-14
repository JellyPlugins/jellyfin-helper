using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
/// A scheduled task to clean up orphaned trickplay folders.
/// Supports configuration-driven library filtering, orphan age, trash/delete mode, and storage tracking.
/// </summary>
public class CleanTrickplayTask : BaseLibraryCleanupTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CleanTrickplayTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    public CleanTrickplayTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger<CleanTrickplayTask> logger,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService)
        : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
    {
    }

    /// <inheritdoc />
    protected override string TaskName => "TrickplayCleaner";

    /// <inheritdoc />
    protected override string ItemLabel => "folders";

    /// <inheritdoc />
    protected override bool IsDryRun() => ConfigHelper.IsDryRunTrickplay();

    /// <inheritdoc />
    protected override (int Deleted, long BytesFreed) ProcessLocation(string libraryPath, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        long bytesFreed = 0;
        var config = ConfigHelper.GetConfig();

        try
        {
            // Get all directories recursively
            var directories = FileSystem.GetDirectories(libraryPath, true).ToList();

            // Cache files per parent directory to avoid repeated filesystem calls
            var fileCache = new Dictionary<string, FileSystemMetadata[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in directories.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
            {
                if (!dir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check if parent is also a .trickplay folder (skip nested ones if any, based on script logic)
                var parentPath = Path.GetDirectoryName(dir.FullName);
                if (string.IsNullOrEmpty(parentPath))
                {
                    continue;
                }

                var parentName = Path.GetFileName(parentPath);
                if (parentName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string trickplayBaseName = dir.Name[..^".trickplay".Length];

                // Check if any media file exists in parent with the same basename (cached)
                if (!fileCache.TryGetValue(parentPath, out var files))
                {
                    files = FileSystem.GetFiles(parentPath).ToArray();
                    fileCache[parentPath] = files;
                }

                bool mediaExists = files.Any(f =>
                    MediaExtensions.VideoExtensions.Contains(Path.GetExtension(f.FullName)) &&
                    Path.GetFileNameWithoutExtension(f.FullName).Equals(trickplayBaseName, StringComparison.OrdinalIgnoreCase));

                if (mediaExists)
                {
                    continue;
                }

                // Check orphan age
                if (!ConfigHelper.IsOldEnoughForDeletion(dir.FullName))
                {
                    PluginLog.LogDebug(TaskName, $"Skipping too-new orphan (min age {config.OrphanMinAgeDays}d): {dir.FullName}", Logger);
                    continue;
                }

                if (dryRun)
                {
                    PluginLog.LogInfo(TaskName, $"[Dry Run] Would delete orphaned trickplay folder: {dir.FullName}", Logger);
                    deletedCount++;
                }
                else if (config.UseTrash)
                {
                    var trashPath = ConfigHelper.GetTrashPath(libraryPath);
                    long size = TrashService.MoveToTrash(dir.FullName, trashPath, Logger);
                    if (size > 0)
                    {
                        bytesFreed += size;
                        deletedCount++;
                    }
                }
                else
                {
                    PluginLog.LogInfo(TaskName, $"Deleting orphaned trickplay folder: {dir.FullName}", Logger);
                    try
                    {
                        long size = FileSystemHelper.CalculateDirectorySize(FileSystem, dir.FullName, Logger);
                        Directory.Delete(dir.FullName, true);
                        bytesFreed += size;
                        deletedCount++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        PluginLog.LogError(TaskName, $"Failed to delete directory: {dir.FullName}", ex, Logger);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.LogError(TaskName, $"Error scanning directory: {libraryPath}", ex, Logger);
        }

        return (deletedCount, bytesFreed);
    }
}