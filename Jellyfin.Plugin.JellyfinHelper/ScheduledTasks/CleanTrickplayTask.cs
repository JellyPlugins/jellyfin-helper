using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     A scheduled task to clean up orphaned trickplay folders.
///     Supports configuration-driven library filtering, orphan age, trash/delete mode, and storage tracking.
/// </summary>
public class CleanTrickplayTask : BaseLibraryCleanupTask
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanTrickplayTask" /> class.
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
    protected override TaskMode GetTaskMode()
    {
        return ConfigHelper.GetTrickplayTaskMode();
    }

    /// <inheritdoc />
    protected override (int Deleted, long BytesFreed) ProcessLocation(
        string libraryPath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        long bytesFreed = 0;
        var config = ConfigHelper.GetConfig();

        try
        {
            var directories = GetSubdirectoriesIterative(libraryPath);

            // Resolve the trash folder path so we can skip any directories inside it.
            // Without this, previously trashed .trickplay folders would be re-detected as orphans
            // and moved to trash again on every run, accumulating timestamp prefixes until the
            // path exceeds the OS limit (PATH_MAX).
            // Path.GetFullPath normalizes trailing separators, relative segments, and mixed slashes.
            var trashPath = ConfigHelper.GetTrashPath(libraryPath);
            var trashRoot = Path.GetFullPath(trashPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Use case-sensitive comparison on Linux, case-insensitive on Windows/macOS.
            var pathComparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // Cache files per parent directory to avoid repeated filesystem calls.
            // Use OS-aware case sensitivity: Linux paths are case-sensitive (Ordinal),
            // Windows/macOS paths are case-insensitive (OrdinalIgnoreCase).
            var fileCacheComparer = OperatingSystem.IsLinux()
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            var fileCache = new Dictionary<string, FileSystemMetadata[]>(fileCacheComparer);

            foreach (var dirFullName in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (itemDeleted, itemBytes) = ProcessTrickplayDirectory(
                    dirFullName,
                    trashRoot,
                    pathComparison,
                    fileCache,
                    config,
                    trashPath,
                    dryRun);
                deletedCount += itemDeleted;
                bytesFreed += itemBytes;
            }
        }
        catch (Exception ex)
        {
            PluginLog.LogError(TaskName, $"Error scanning directory: {libraryPath}", ex, Logger);
        }

        return (deletedCount, bytesFreed);
    }

    /// <summary>
    ///     Evaluates a single candidate directory and, if it is an orphaned trickplay folder,
    ///     applies the configured dry-run/trash/delete action. Returns the per-item deletion count
    ///     and bytes freed so the caller can accumulate them.
    /// </summary>
    private (int Deleted, long BytesFreed) ProcessTrickplayDirectory(
        string dirFullName,
        string trashRoot,
        StringComparison pathComparison,
        Dictionary<string, FileSystemMetadata[]> fileCache,
        PluginConfiguration config,
        string trashPath,
        bool dryRun)
    {
        if (!IsDeletableOrphanedTrickplayDirectory(dirFullName, trashRoot, pathComparison, fileCache, config))
        {
            return (0, 0);
        }

        if (dryRun)
        {
            PluginLog.LogInfo(
                TaskName,
                $"[Dry Run] Would delete orphaned trickplay folder: {dirFullName}",
                Logger);
            return (1, 0);
        }

        if (config.UseTrash)
        {
            PluginLog.LogInfo(TaskName, $"Moving orphaned trickplay folder to trash: {dirFullName}", Logger);
            var size = TrashService.MoveToTrash(dirFullName, trashPath, Logger);
            if (size <= 0)
            {
                return (0, 0);
            }

            return (1, size);
        }

        PluginLog.LogInfo(TaskName, $"Deleting orphaned trickplay folder: {dirFullName}", Logger);
        try
        {
            var size = FileSystemHelper.CalculateDirectorySize(dirFullName);
            Directory.Delete(dirFullName, true);
            return (1, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogError(TaskName, $"Failed to delete directory: {dirFullName}", ex, Logger);
            return (0, 0);
        }
    }

    /// <summary>
    ///     Determines whether the candidate directory is an orphaned trickplay folder that is safe to
    ///     delete under the configured mode. Runs all eligibility guards (trash-root skip, .trickplay
    ///     suffix, parent-is-trickplay skip, media-exists check with file cache, orphan-age check) and
    ///     the reparse-point (symlink) guard that fails closed on stat errors. Returns <c>false</c> for
    ///     any directory that must be skipped; returns <c>true</c> only when the directory is a deletable
    ///     orphan.
    /// </summary>
    /// <param name="dirFullName">The candidate trickplay directory to evaluate.</param>
    /// <param name="trashRoot">The normalized trash root used to skip already-trashed items.</param>
    /// <param name="pathComparison">The OS-aware path comparison to use for the trash-root check.</param>
    /// <param name="fileCache">The per-parent-directory file listing cache (populated on demand).</param>
    /// <param name="config">The plugin configuration (used for orphan-age messaging).</param>
    /// <returns><c>true</c> if the directory is a deletable orphaned trickplay folder; otherwise <c>false</c>.</returns>
    private bool IsDeletableOrphanedTrickplayDirectory(
        string dirFullName,
        string trashRoot,
        StringComparison pathComparison,
        Dictionary<string, FileSystemMetadata[]> fileCache,
        PluginConfiguration config)
    {
        // Skip the trash root itself and any directories inside it to prevent
        // re-trashing already-trashed items.
        var normalizedDirPath = Path.GetFullPath(dirFullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedDirPath.Equals(trashRoot, pathComparison)
            || normalizedDirPath.StartsWith(trashRoot + Path.DirectorySeparatorChar, pathComparison))
        {
            return false;
        }

        var dirName = Path.GetFileName(dirFullName);
        if (!dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check if parent is also a .trickplay folder (skip nested ones if any, based on script logic)
        var parentPath = Path.GetDirectoryName(dirFullName);
        if (string.IsNullOrEmpty(parentPath))
        {
            return false;
        }

        var parentName = Path.GetFileName(parentPath);
        if (parentName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (MediaExistsForTrickplay(dirName, parentPath, fileCache))
        {
            return false;
        }

        // Check orphan age
        if (!ConfigHelper.IsOldEnoughForDeletion(dirFullName))
        {
            PluginLog.LogDebug(
                TaskName,
                $"Skipping too-new orphan (min age {config.OrphanMinAgeDays}d): {dirFullName}",
                Logger);
            return false;
        }

        return !IsSkippedReparsePoint(dirFullName);
    }

    /// <summary>
    ///     Determines whether a media file with the trickplay folder's base name exists in the parent
    ///     directory, populating the per-parent file cache on demand. A file-listing failure is treated
    ///     as "media exists" (skip, fail closed) so an unreadable parent never triggers a deletion.
    /// </summary>
    /// <param name="dirName">The trickplay directory name (with the <c>.trickplay</c> suffix).</param>
    /// <param name="parentPath">The parent directory whose files are inspected.</param>
    /// <param name="fileCache">The per-parent-directory file listing cache (populated on demand).</param>
    /// <returns><c>true</c> if matching media exists or the parent could not be listed; otherwise <c>false</c>.</returns>
    private bool MediaExistsForTrickplay(
        string dirName,
        string parentPath,
        Dictionary<string, FileSystemMetadata[]> fileCache)
    {
        var trickplayBaseName = dirName[..^".trickplay".Length];

        // Check if any media file exists in parent with the same basename (cached)
        if (!fileCache.TryGetValue(parentPath, out var files))
        {
            try
            {
                files = FileSystem.GetFiles(parentPath).ToArray();
                fileCache[parentPath] = files;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not list files in: {parentPath}", ex, Logger);
                return true;
            }
        }

        return files.Any(f =>
            MediaExtensions.VideoExtensions.Contains(Path.GetExtension(f.FullName)) &&
            Path.GetFileNameWithoutExtension(f.FullName)
                .Equals(trickplayBaseName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Applies the reparse-point (symlink/junction) guard used by ALL modes (dry-run, trash,
    ///     hard-delete): a reparse-point trickplay dir is never trashed, never recursively deleted, and
    ///     never reported as a dry-run deletion. Trashing would relocate the link node while its target
    ///     stays behind, and Directory.Delete(recursive) could be redirected into the link's real
    ///     target. A stat failure is treated as "skip" (fail closed) and must not surface as a
    ///     misleading delete error.
    /// </summary>
    /// <param name="dirFullName">The candidate trickplay directory to stat.</param>
    /// <returns><c>true</c> if the directory is a reparse point or could not be stat'd; otherwise <c>false</c>.</returns>
    private bool IsSkippedReparsePoint(string dirFullName)
    {
        bool dirIsReparsePoint;
        try
        {
            dirIsReparsePoint = IsReparsePoint(dirFullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not stat directory, skipping: {dirFullName}", ex, Logger);
            return true;
        }

        if (dirIsReparsePoint)
        {
            PluginLog.LogWarning(TaskName, $"Skipping symlinked trickplay directory (reparse point): {dirFullName}", logger: Logger);
            return true;
        }

        return false;
    }

    // Enumerate directories lazily with per-directory error isolation instead of
    // materialising the whole tree upfront. An IOException on one directory
    // no longer aborts the entire scan; each failed entry is logged and skipped.
    private IEnumerable<string> GetSubdirectoriesIterative(string root)
    {
        var stack = new Stack<string>();
        IEnumerable<FileSystemMetadata> topLevel;
        try
        {
            topLevel = FileSystem.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLog.LogWarning(TaskName, $"Could not enumerate subdirectories of: {root}", ex, Logger);
            yield break;
        }

        foreach (var d in topLevel)
        {
            stack.Push(d.FullName);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            // Do not enumerate children of reparse-point (symlink/junction) directories.
            // The per-entry guard in the caller handles the yielded entry; not traversing
            // here prevents following links into foreign trees. A stat failure is treated
            // as "do not traverse" (fail closed) so the iterator does not fault mid-scan.
            bool currentIsReparsePoint;
            try
            {
                currentIsReparsePoint = IsReparsePoint(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(
                    TaskName,
                    $"Could not stat directory, not traversing: {current}",
                    ex,
                    Logger);
                continue;
            }

            if (currentIsReparsePoint)
            {
                continue;
            }

            IEnumerable<FileSystemMetadata> children;
            try
            {
                children = FileSystem.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not enumerate subdirectories of: {current}", ex, Logger);
                continue;
            }

            foreach (var child in children)
            {
                stack.Push(child.FullName);
            }
        }
    }
}