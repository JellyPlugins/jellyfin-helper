using System;
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
///         folder is left untouched — including subfolders that may not contain videos themselves
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
    protected override bool IsDryRun()
    {
        return ConfigHelper.IsDryRunEmptyMediaFolders();
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
            // Get only the direct child directories of the library root (top-level media folders).
            // Each top-level folder represents a single movie, show, etc.
            var topLevelDirs = FileSystem.GetDirectories(libraryPath).ToList();

            foreach (var topDir in topLevelDirs.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
            {
                // Skip .trickplay folders — they are handled by CleanTrickplayTask
                if (topDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip trash folder
                var trashFolderName = Path.GetFileName(ConfigHelper.GetTrashPath(libraryPath));
                if (topDir.Name.Equals(trashFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip boxset/collection folders — these are Jellyfin-internal and must never be deleted.
                // They typically have "[boxset]" in the folder name or reside under a collections' path.
                if (topDir.Name.Contains("[boxset]", StringComparison.OrdinalIgnoreCase)
                    || topDir.Name.Contains("[collection]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check the entire tree in a single pass: does it contain any files at all,
                // any video files, any audio files, or any non-metadata files?
                // This avoids traversing the tree multiple times.
                var (hasAnyFiles, hasVideoFiles, hasAudioFiles, hasNonMetadataFiles) =
                    AnalyzeDirectoryRecursive(topDir.FullName, cancellationToken);

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
                    var trashPath = ConfigHelper.GetTrashPath(libraryPath);
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
                        var size = FileSystemHelper.CalculateDirectorySize(FileSystem, topDir.FullName);
                        Directory.Delete(topDir.FullName, true);
                        bytesFreed += size;
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
    ///     Analyzes a directory tree in a single recursive pass, determining whether
    ///     any files exist, whether any of them are video files, whether any are audio files,
    ///     and whether any non-metadata files exist (files that are not images or NFO/XML).
    /// </summary>
    /// <param name="directoryPath">The directory to analyze.</param>
    /// <param name="cancellationToken">A token to cancel the recursive scan.</param>
    /// <returns>
    ///     A tuple indicating whether any files exist, whether any video files exist,
    ///     whether any audio files exist, and whether any non-metadata files exist.
    /// </returns>
    private (bool HasAnyFiles, bool HasVideoFiles, bool HasAudioFiles, bool HasNonMetadataFiles)
        AnalyzeDirectoryRecursive(string directoryPath, CancellationToken cancellationToken)
    {
        // Allow cancellation to interrupt deep recursion
        cancellationToken.ThrowIfCancellationRequested();

        var hasAnyFiles = false;
        var hasAudioFiles = false;
        var hasNonMetadataFiles = false;

        // Check files in the directory itself
        var files = FileSystem.GetFiles(directoryPath);
        foreach (var file in files)
        {
            hasAnyFiles = true;
            var ext = Path.GetExtension(file.FullName);
            if (MediaExtensions.VideoExtensions.Contains(ext))
            {
                // Video found — no need to scan further (video takes priority)
                return (true, true, hasAudioFiles, true);
            }

            if (MediaExtensions.AudioExtensions.Contains(ext))
            {
                hasAudioFiles = true;
                hasNonMetadataFiles = true;
            }
            else if (!MediaExtensions.ImageExtensions.Contains(ext)
                     && !MediaExtensions.NfoExtensions.Contains(ext))
            {
                // File is not a video, audio, image, or NFO → it's a "non-metadata" file
                // (e.g. subtitles, text files, or other residual files from a deleted video)
                hasNonMetadataFiles = true;
            }
        }

        // Check subdirectories recursively
        var subDirs = FileSystem.GetDirectories(directoryPath);
        foreach (var subDir in subDirs)
        {
            var (subHasAnyFiles, subHasVideoFiles, subHasAudioFiles, subHasNonMetadataFiles) =
                AnalyzeDirectoryRecursive(subDir.FullName, cancellationToken);
            hasAnyFiles |= subHasAnyFiles;
            hasAudioFiles |= subHasAudioFiles;
            hasNonMetadataFiles |= subHasNonMetadataFiles;
            if (subHasVideoFiles)
            {
                // Video found deeper in the tree — no need to scan further
                return (true, true, hasAudioFiles, true);
            }
        }

        return (hasAnyFiles, false, hasAudioFiles, hasNonMetadataFiles);
    }
}