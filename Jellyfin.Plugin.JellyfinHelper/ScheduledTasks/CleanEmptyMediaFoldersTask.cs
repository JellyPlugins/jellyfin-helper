using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
///     A scheduled task to clean up orphaned media folders that contain non-metadata files but absolutely no video files anywhere in their entire directory tree.
/// </summary>
/// <remarks>
///     Targets the common case where deleting a movie/episode removes only the video file, leaving the folder of metadata (.nfo), artwork (.jpg), subtitles (.srt).
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

        // Hoist trash-path computation outside the loop - libraryPath is constant per call. Use case-sensitive comparison on Linux, case-insensitive on Windows/macOS, matching the same pattern used by CleanTrickplayTask and CleanOrphanedSubtitlesTask.
        var trashPath = ConfigHelper.GetTrashPath(libraryPath);
        var trashRoot = Path.GetFullPath(trashPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trashRootSep = trashRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        try
        {
            var topLevelDirs = FileSystem.GetDirectories(libraryPath).ToList();

            foreach (var topDir in topLevelDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShouldDeleteOrphanFolder(topDir, trashRoot, trashRootSep, pathComparison, config, cancellationToken, out var treeBytes))
                {
                    continue;
                }

                DeleteOrphanFolder(topDir, trashPath, treeBytes, dryRun, config, ref deletedCount, ref bytesFreed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            PluginLog.LogError(TaskName, $"Error scanning directory: {libraryPath}", ex, Logger);
        }

        return (deletedCount, bytesFreed);
    }

    /// <summary>
    ///     Evaluates a single top-level directory and decides whether it is an orphaned media folder eligible for deletion.
    /// </summary>
    /// <param name="topDir">The top-level directory to evaluate.</param>
    /// <param name="trashRoot">The normalized trash root path.</param>
    /// <param name="trashRootSep">The trash root path with a trailing separator.</param>
    /// <param name="pathComparison">The OS-aware string comparison for path matching.</param>
    /// <param name="config">The active plugin configuration.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <param name="treeBytes">The accumulated byte count of the analyzed tree.</param>
    /// <returns><c>true</c> when the folder is a deletion-eligible orphan.</returns>
    private bool ShouldDeleteOrphanFolder(
        FileSystemMetadata topDir,
        string trashRoot,
        string trashRootSep,
        StringComparison pathComparison,
        PluginConfiguration config,
        CancellationToken cancellationToken,
        out long treeBytes)
    {
        treeBytes = 0;

        // Skip .trickplay folders - they are handled by CleanTrickplayTask
        if (topDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Skip the trash folder and anything nested inside it.
        var normalizedDirPath = Path.GetFullPath(topDir.FullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedDirPath.Equals(trashRoot, pathComparison)
            || normalizedDirPath.StartsWith(trashRootSep, pathComparison))
        {
            return false;
        }

        // Skip boxset/collection folders - these are Jellyfin-internal and must never be deleted.
        // They typically have "[boxset]" in the folder name or reside under a collections' path.
        if (topDir.Name.Contains("[boxset]", StringComparison.OrdinalIgnoreCase)
            || topDir.Name.Contains("[collection]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Symlink guard: NEVER traverse into, or delete, a top-level reparse point (symlink/junction).
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
            return false;
        }

        if (topIsReparsePoint)
        {
            PluginLog.LogWarning(
                TaskName,
                $"Skipping symlinked directory (reparse point): {topDir.FullName}",
                logger: Logger);
            return false;
        }

        // Check the entire tree in a single pass: does it contain any files at all, any video files, any audio files, or any non-metadata files? The accumulated byte count is returned here so that the hard-delete path does not need a second traversal via CalculateDirectorySize.
        var (hasAnyFiles, hasVideoFiles, hasAudioFiles, hasNonMetadataFiles, analyzedBytes, hasUnresolvedLink) =
            AnalyzeDirectoryRecursive(topDir.FullName, cancellationToken);
        treeBytes = analyzedBytes;

        // If any subtree was hidden behind a symlink/junction (or an unreadable subdir), the orphan verdict is unproven: video files could live behind that link.
        if (hasUnresolvedLink)
        {
            PluginLog.LogWarning(
                TaskName,
                $"Skipping folder with an unresolved symlinked/unreadable subdirectory (orphan status unproven): {topDir.FullName}",
                logger: Logger);
            return false;
        }

        // If the folder tree is completely empty (no files at all), skip it.
        // Empty folders are often pre-created by tools like Radarr/Sonarr for "wanted" media.
        if (!hasAnyFiles)
        {
            return false;
        }

        // If the folder contains video files anywhere in the tree, it's an active media folder, so skip.
        if (hasVideoFiles)
        {
            return false;
        }

        // If the folder contains audio files, it belongs to a music library, so skip it.
        // Music folders only have audio files (no video), so they must not be treated as orphaned.
        if (hasAudioFiles)
        {
            return false;
        }

        // If the folder contains ONLY metadata/artwork files (images + NFO) but no video, audio, or other files, it's likely a placeholder created by Sonarr/Radarr for upcoming media, so skip it.
        if (!hasNonMetadataFiles)
        {
            PluginLog.LogDebug(
                TaskName,
                $"Skipping metadata-only folder (likely a wanted-list placeholder): {topDir.FullName}",
                Logger);
            return false;
        }

        // Check orphan age
        if (!ConfigHelper.IsOldEnoughForDeletion(topDir.FullName))
        {
            PluginLog.LogDebug(
                TaskName,
                $"Skipping too-new orphan (min age {config.OrphanMinAgeDays}d): {topDir.FullName}",
                Logger);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Performs the deletion action for a confirmed orphan folder: dry-run report, move-to-trash, or hard delete, updating the running counters accordingly.
    /// </summary>
    private void DeleteOrphanFolder(
        FileSystemMetadata topDir,
        string trashPath,
        long treeBytes,
        bool dryRun,
        PluginConfiguration config,
        ref int deletedCount,
        ref long bytesFreed)
    {
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
                return;
            }

            bytesFreed += size;
            deletedCount++;
        }
        else
        {
            PluginLog.LogInfo(TaskName, $"Deleting orphaned media folder: {topDir.FullName}", Logger);
            try
            {
                // Reuse the byte count already accumulated during the analysis pass instead of re-traversing the tree with CalculateDirectorySize.
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

    /// <summary>
    ///     Analyzes a directory tree in a single iterative pass (explicit stack, no recursion depth limit).
    /// </summary>
    private (bool HasAnyFiles, bool HasVideoFiles, bool HasAudioFiles, bool HasNonMetadataFiles, long TotalBytes, bool HasUnresolvedLink)
        AnalyzeDirectoryRecursive(string directoryPath, CancellationToken cancellationToken)
    {
        var state = new DirectoryScanState();

        var stack = new Stack<string>();
        stack.Push(directoryPath);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            if (ScanFilesForVideo(current, state))
            {
                // A video file means the folder is kept regardless. Return 0 bytes: the caller only uses treeBytes in the hard-delete branch, which never runs when hasVideoFiles==true.
                return (true, true, state.HasAudioFiles, true, 0, false);
            }

            EnqueueSubdirectories(current, stack, state);
        }

        return (state.HasAnyFiles, false, state.HasAudioFiles, state.HasNonMetadataFiles, state.TotalBytes, state.HasUnresolvedLink);
    }

    /// <summary>
    ///     Enumerates and classifies the files directly under , updating . Returns true as soon as a video file is found (short-circuit).
    /// </summary>
    private bool ScanFilesForVideo(string current, DirectoryScanState state)
    {
        IEnumerable<FileSystemMetadata> files;
        try
        {
            files = FileSystem.GetFiles(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files of this directory were not analyzed, so the orphan verdict for the enclosing tree is unproven (a video could live in the subtree we failed to read).
            state.HasUnresolvedLink = true;
            PluginLog.LogWarning(TaskName, $"Could not list files in: {current}", ex, Logger);
            return false;
        }

        foreach (var file in files)
        {
            // A directory symlink can surface as a FILE entry on some mounts (e.g. Docker Desktop for Windows bind mounts) rather than as a subdirectory.
            bool fileIsReparsePoint;
            try
            {
                fileIsReparsePoint = IsReparsePointAnyType(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.HasUnresolvedLink = true;
                PluginLog.LogWarning(TaskName, $"Could not stat entry, treating tree as unresolved: {file.FullName}", ex, Logger);
                continue;
            }

            if (fileIsReparsePoint)
            {
                state.HasUnresolvedLink = true;
                continue;
            }

            state.HasAnyFiles = true;
            state.TotalBytes += file.Length;
            var ext = Path.GetExtension(file.FullName);
            if (MediaExtensions.VideoExtensions.Contains(ext))
            {
                return true;
            }

            if (MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
            {
                state.HasAudioFiles = true;
                state.HasNonMetadataFiles = true;
            }
            else if (!MediaExtensions.ImageExtensions.Contains(ext)
                     && !MediaExtensions.NfoExtensions.Contains(ext))
            {
                state.HasNonMetadataFiles = true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Enumerates the subdirectories of and pushes traversable (non reparse-point) children onto , flagging unresolved links on .
    /// </summary>
    private void EnqueueSubdirectories(string current, Stack<string> stack, DirectoryScanState state)
    {
        IEnumerable<FileSystemMetadata> subDirs;
        try
        {
            subDirs = FileSystem.GetDirectories(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Subdirectories could not be listed, so any subtree beneath them was not analyzed.
            // Fail closed for the same reason as the file-enumeration failure above.
            state.HasUnresolvedLink = true;
            PluginLog.LogWarning(TaskName, $"Could not list subdirectories in: {current}", ex, Logger);
            return;
        }

        foreach (var subDirPath in subDirs.Select(subDir => subDir.FullName))
        {
            // Skip reparse-point subdirectories to prevent following symlinks or junctions into foreign trees during recursive analysis.
            bool subIsReparsePoint;
            try
            {
                subIsReparsePoint = IsReparsePoint(subDirPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Could not determine, so treat as an unresolved link so the caller does not
                // delete a folder whose subtree we failed to inspect.
                state.HasUnresolvedLink = true;
                PluginLog.LogWarning(
                    TaskName,
                    $"Could not stat subdirectory, not traversing: {subDirPath}",
                    ex,
                    Logger);
                continue;
            }

            if (subIsReparsePoint)
            {
                // The subtree behind this link was not analyzed, so the orphan verdict for the
                // enclosing folder is unproven. Flag it so ProcessLocation suppresses deletion.
                state.HasUnresolvedLink = true;
                continue;
            }

            stack.Push(subDirPath);
        }
    }

    /// <summary>
    ///     Mutable accumulator for a single <see cref="AnalyzeDirectoryRecursive"/> traversal.
    /// </summary>
    private sealed class DirectoryScanState
    {
        public bool HasAnyFiles { get; set; }

        public bool HasAudioFiles { get; set; }

        public bool HasNonMetadataFiles { get; set; }

        public bool HasUnresolvedLink { get; set; }

        public long TotalBytes { get; set; }
    }
}