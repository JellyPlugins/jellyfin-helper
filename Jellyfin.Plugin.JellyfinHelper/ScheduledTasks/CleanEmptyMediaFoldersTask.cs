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
///     A scheduled task to clean up orphaned media folders that contain non-metadata files
///     but absolutely no video files anywhere in their entire directory tree.
///     Supports configuration-driven library filtering, orphan age, trash/delete mode, and storage tracking.
/// </summary>
/// <remarks>
///     <para>
///         This plugin targets a common scenario: when a movie or episode is deleted, only the video file
///         is removed while the surrounding folder with metadata (.nfo), artwork (.jpg), subtitles (.srt)
///         etc. remains as an orphaned folder.
///     </para>
///     <para>
///         The scan operates on <strong>top-level folders</strong> (direct children of each library root).
///         For each top-level folder, the entire directory tree is checked recursively. A folder is only
///         considered orphaned and eligible for deletion when it contains <strong>non-metadata files</strong>
///         (e.g. subtitles, text files) but absolutely NO video file anywhere in the tree.
///         If at least one video file exists anywhere (even in a deeply nested subdirectory), the entire
///         folder is left untouched - including subfolders that may not contain videos themselves
///         (e.g. empty Season folders created by Sonarr as "wanted" placeholders).
///     </para>
///     <para>
///         Completely empty folders (containing zero files in the entire tree) are intentionally skipped,
///         as they are often pre-created by tools like Radarr/Sonarr for upcoming media.
///     </para>
///     <para>
///         Folders that contain <strong>only metadata/artwork files</strong> (images like .jpg/.png and
///         NFO/XML files) but no video or other files are also skipped, as they are typically placeholders
///         created by Sonarr/Radarr for wanted media that hasn't been downloaded yet.
///     </para>
/// </remarks>
public class CleanEmptyMediaFoldersTask : BaseLibraryCleanupTask
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanEmptyMediaFoldersTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    public CleanEmptyMediaFoldersTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger<CleanEmptyMediaFoldersTask> logger,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService)
        : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
    {
    }

    /// <inheritdoc />
    protected override string TaskName => "EmptyFolderCleaner";

    /// <inheritdoc />
    protected override string ItemLabel => "folders";

    /// <inheritdoc />
    protected override TaskMode GetTaskMode()
    {
        return ConfigHelper.GetEmptyMediaFolderTaskMode();
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

        // Hoist trash-path computation outside the loop - libraryPath is constant per call.
        // Use case-sensitive comparison on Linux, case-insensitive on Windows/macOS,
        // matching the same pattern used by CleanTrickplayTask and CleanOrphanedSubtitlesTask.
        var trashPath = ConfigHelper.GetTrashPath(libraryPath);
        var trashRoot = Path.GetFullPath(trashPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trashRootSep = trashRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        try
        {
            // Get only the direct child directories of the library root (top-level media folders).
            // Each top-level folder represents a single movie, show, etc.
            var topLevelDirs = FileSystem.GetDirectories(libraryPath).ToList();

            foreach (var topDir in topLevelDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Skip .trickplay folders - they are handled by CleanTrickplayTask
                if (topDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip the trash folder and anything nested inside it.
                // Use full-path prefix comparison (same pattern as CleanTrickplayTask) rather than
                // a bare name equality check, which breaks when the trash path is multi-segment
                // or when a previous run has added a timestamp prefix to the folder.
                var normalizedDirPath = Path.GetFullPath(topDir.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalizedDirPath.Equals(trashRoot, pathComparison)
                    || normalizedDirPath.StartsWith(trashRootSep, pathComparison))
                {
                    continue;
                }

                // Skip boxset/collection folders - these are Jellyfin-internal and must never be deleted.
                // They typically have "[boxset]" in the folder name or reside under a collections' path.
                if (topDir.Name.Contains("[boxset]", StringComparison.OrdinalIgnoreCase)
                    || topDir.Name.Contains("[collection]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Symlink guard: NEVER traverse into — or delete — a top-level reparse point
                // (symlink/junction). Its target may hold live media (Radarr/Sonarr commonly place
                // symlinked media folders directly under a library root), and this task has no
                // orphan evidence for an entry it did not analyze. Deleting the link node on age
                // alone would remove valid library entries. Mirror CleanOrphanedSubtitlesTask: skip.
                bool topIsReparsePoint;
                try
                {
                    topIsReparsePoint = IsReparsePoint(topDir.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A stat failure on one entry must not abort the whole library scan (the outer
                    // catch would otherwise stop it). Skip this entry only and continue.
                    PluginLog.LogWarning(
                        TaskName,
                        $"Could not stat directory, skipping: {topDir.FullName}",
                        ex,
                        Logger);
                    continue;
                }

                if (topIsReparsePoint)
                {
                    PluginLog.LogWarning(
                        TaskName,
                        $"Skipping symlinked directory (reparse point): {topDir.FullName}",
                        logger: Logger);
                    continue;
                }

                // Check the entire tree in a single pass: does it contain any files at all,
                // any video files, any audio files, or any non-metadata files?
                // The accumulated byte count is returned here so that the hard-delete path
                // does not need a second traversal via CalculateDirectorySize.
                var (hasAnyFiles, hasVideoFiles, hasAudioFiles, hasNonMetadataFiles, treeBytes, hasUnresolvedLink) =
                    AnalyzeDirectoryRecursive(topDir.FullName, cancellationToken);

                // If any subtree was hidden behind a symlink/junction (or an unreadable subdir),
                // the orphan verdict is unproven: video files could live behind that link. Never
                // delete such a folder — a false orphan here would destroy real media.
                if (hasUnresolvedLink)
                {
                    PluginLog.LogWarning(
                        TaskName,
                        $"Skipping folder with an unresolved symlinked/unreadable subdirectory (orphan status unproven): {topDir.FullName}",
                        logger: Logger);
                    continue;
                }

                // If the folder tree is completely empty (no files at all), skip it.
                // Empty folders are often pre-created by tools like Radarr/Sonarr for "wanted" media.
                if (!hasAnyFiles)
                {
                    continue;
                }

                // If the folder contains video files anywhere in the tree → active media folder → skip.
                if (hasVideoFiles)
                {
                    continue;
                }

                // If the folder contains audio files, it belongs to a music library → skip it.
                // Music folders only have audio files (no video), so they must not be treated as orphaned.
                if (hasAudioFiles)
                {
                    continue;
                }

                // If the folder contains ONLY metadata/artwork files (images + NFO) but no video,
                // audio, or other files, it's likely a placeholder created by Sonarr/Radarr
                // for upcoming media → skip it.
                if (!hasNonMetadataFiles)
                {
                    PluginLog.LogDebug(
                        TaskName,
                        $"Skipping metadata-only folder (likely a wanted-list placeholder): {topDir.FullName}",
                        Logger);
                    continue;
                }

                // The folder has non-metadata files (e.g. subtitles, text files) but no video files
                // anywhere in the tree → it's an orphaned media folder whose video was deleted.

                // Check orphan age
                if (!ConfigHelper.IsOldEnoughForDeletion(topDir.FullName))
                {
                    PluginLog.LogDebug(
                        TaskName,
                        $"Skipping too-new orphan (min age {config.OrphanMinAgeDays}d): {topDir.FullName}",
                        Logger);
                    continue;
                }

                if (dryRun)
                {
                    PluginLog.LogInfo(
                        TaskName,
                        $"[Dry Run] Would delete orphaned media folder: {topDir.FullName}",
                        Logger);
                    deletedCount++;
                }
                else if (config.UseTrash)
                {
                    PluginLog.LogInfo(TaskName, $"Moving orphaned media folder to trash: {topDir.FullName}", Logger);
                    var size = TrashService.MoveToTrash(topDir.FullName, trashPath, Logger);
                    if (size <= 0)
                    {
                        continue;
                    }

                    bytesFreed += size;
                    deletedCount++;
                }
                else
                {
                    PluginLog.LogInfo(TaskName, $"Deleting orphaned media folder: {topDir.FullName}", Logger);
                    try
                    {
                        // Reuse the byte count already accumulated during the analysis pass
                        // instead of re-traversing the tree with CalculateDirectorySize.
                        // Note: reparse-point directories are already skipped before this point.
                        Directory.Delete(topDir.FullName, true);
                        bytesFreed += treeBytes;
                        deletedCount++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        PluginLog.LogError(TaskName, $"Failed to delete directory: {topDir.FullName}", ex, Logger);
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

    /// <summary>
    ///     Analyzes a directory tree in a single iterative pass (explicit stack, no recursion depth limit).
    ///     Returns early as soon as a video file is found anywhere in the subtree.
    ///     <para>
    ///         <c>HasUnresolvedLink</c> is set when a reparse-point subdirectory (or a subdirectory
    ///         that could not be stat'd) was skipped: its contents were NOT analyzed, so the orphan
    ///         verdict for the whole tree is unproven and the caller must NOT delete the folder.
    ///     </para>
    /// </summary>
    private (bool HasAnyFiles, bool HasVideoFiles, bool HasAudioFiles, bool HasNonMetadataFiles, long TotalBytes, bool HasUnresolvedLink)
        AnalyzeDirectoryRecursive(string directoryPath, CancellationToken cancellationToken)
    {
        var hasAnyFiles = false;
        var hasAudioFiles = false;
        var hasNonMetadataFiles = false;
        var hasUnresolvedLink = false;
        long totalBytes = 0;

        var stack = new Stack<string>();
        stack.Push(directoryPath);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            IEnumerable<FileSystemMetadata> files;
            try
            {
                files = FileSystem.GetFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not list files in: {current}", ex, Logger);
                continue;
            }

            foreach (var file in files)
            {
                hasAnyFiles = true;
                totalBytes += file.Length;
                var ext = Path.GetExtension(file.FullName);
                if (MediaExtensions.VideoExtensions.Contains(ext))
                {
                    // Return 0 bytes: the caller only uses treeBytes in the hard-delete branch,
                    // which never runs when hasVideoFiles==true. HasUnresolvedLink is irrelevant
                    // here — a video file means the folder is kept regardless.
                    return (true, true, hasAudioFiles, true, 0, false);
                }

                if (MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
                {
                    hasAudioFiles = true;
                    hasNonMetadataFiles = true;
                }
                else if (!MediaExtensions.ImageExtensions.Contains(ext)
                         && !MediaExtensions.NfoExtensions.Contains(ext))
                {
                    hasNonMetadataFiles = true;
                }
            }

            IEnumerable<FileSystemMetadata> subDirs;
            try
            {
                subDirs = FileSystem.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not list subdirectories in: {current}", ex, Logger);
                continue;
            }

            foreach (var subDir in subDirs)
            {
                // Skip reparse-point subdirectories to prevent following symlinks or
                // junctions into foreign trees during recursive analysis. A stat failure is
                // treated as "do not traverse" (fail closed) and must not abort the scan.
                bool subIsReparsePoint;
                try
                {
                    subIsReparsePoint = IsReparsePoint(subDir.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Could not determine — treat as an unresolved link so the caller does not
                    // delete a folder whose subtree we failed to inspect.
                    hasUnresolvedLink = true;
                    PluginLog.LogWarning(
                        TaskName,
                        $"Could not stat subdirectory, not traversing: {subDir.FullName}",
                        ex,
                        Logger);
                    continue;
                }

                if (subIsReparsePoint)
                {
                    // The subtree behind this link was not analyzed, so the orphan verdict for the
                    // enclosing folder is unproven. Flag it so ProcessLocation suppresses deletion.
                    hasUnresolvedLink = true;
                    continue;
                }

                stack.Push(subDir.FullName);
            }
        }

        return (hasAnyFiles, false, hasAudioFiles, hasNonMetadataFiles, totalBytes, hasUnresolvedLink);
    }
}