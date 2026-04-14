using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;

/// <summary>
/// A scheduled task to clean up orphaned subtitle files (.srt, .ass, .sub, etc.)
/// that no longer have a corresponding video file with the same base name.
/// </summary>
/// <remarks>
/// <para>
/// Subtitle files typically follow a naming convention where the base name matches the video file:
/// <c>Movie Name (2021).mkv</c> → <c>Movie Name (2021).en.srt</c> or <c>Movie Name (2021).srt</c>.
/// </para>
/// <para>
/// This task scans all directories recursively and for each subtitle file, checks whether any video
/// file with a matching base name exists in the same directory. The matching is flexible:
/// <c>Movie.en.srt</c> matches <c>Movie.mkv</c> because we strip language suffixes from the subtitle name.
/// </para>
/// <para>
/// Note: Only subtitle files are cleaned. Images like <c>backdrop.jpg</c>, <c>poster.jpg</c> etc.
/// are NOT touched because they typically don't follow the video-name pattern and serve the entire folder.
/// </para>
/// </remarks>
public class CleanOrphanedSubtitlesTask : BaseLibraryCleanupTask
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CleanOrphanedSubtitlesTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trackingService">The cleanup tracking service.</param>
    /// <param name="trashService">The trash service.</param>
    public CleanOrphanedSubtitlesTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        ILogger<CleanOrphanedSubtitlesTask> logger,
        ICleanupConfigHelper configHelper,
        ICleanupTrackingService trackingService,
        ITrashService trashService)
        : base(libraryManager, fileSystem, pluginLog, logger, configHelper, trackingService, trashService)
    {
    }

    /// <inheritdoc />
    protected override string TaskName => "SubtitleCleaner";

    /// <inheritdoc />
    protected override string ItemLabel => "files";

    /// <inheritdoc />
    protected override bool IsDryRun() => ConfigHelper.IsDryRunOrphanedSubtitles();

    /// <inheritdoc />
    protected override (int Deleted, long BytesFreed) ProcessLocation(string libraryPath, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        long bytesFreed = 0;
        var config = ConfigHelper.GetConfig();

        try
        {
            // Process directories: for each directory, check all subtitle files
            var allDirs = new List<string> { libraryPath };
            try
            {
                allDirs.AddRange(
                    FileSystem.GetDirectories(libraryPath, true)
                        .Select(d => d.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLog.LogWarning(TaskName, $"Could not enumerate subdirectories of: {libraryPath}", ex, Logger);
            }

            // Cache files per directory
            var fileCache = new Dictionary<string, FileSystemMetadata[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var dirPath in allDirs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Skip .trickplay directories
                if (Path.GetFileName(dirPath).EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip trash directories
                var trashFolderName = Path.GetFileName(ConfigHelper.GetTrashPath(libraryPath));
                if (Path.GetFileName(dirPath).Equals(trashFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!fileCache.TryGetValue(dirPath, out var files))
                {
                    try
                    {
                        files = FileSystem.GetFiles(dirPath, false).ToArray();
                        fileCache[dirPath] = files;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        PluginLog.LogWarning(TaskName, $"Could not list files in: {dirPath}", ex, Logger);
                        continue;
                    }
                }

                // Get all video base names in this directory
                var videoBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (MediaExtensions.VideoExtensions.Contains(Path.GetExtension(file.FullName)))
                    {
                        videoBaseNames.Add(Path.GetFileNameWithoutExtension(file.FullName));
                    }
                }

                // If there are no videos in this directory at all, skip — subtitles here
                // are likely managed by the folder itself (season folder, etc.)
                // The EmptyMediaFolder task handles entire orphaned folders.
                if (videoBaseNames.Count == 0)
                {
                    continue;
                }

                // Check each subtitle file
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!MediaExtensions.SubtitleExtensions.Contains(Path.GetExtension(file.FullName)))
                    {
                        continue;
                    }

                    // Extract the base name of the subtitle, stripping language suffixes
                    // e.g., "Movie.en.srt" → "Movie", "Movie.en.forced.srt" → "Movie"
                    var subtitleBaseName = GetSubtitleBaseName(file.FullName);

                    if (videoBaseNames.Contains(subtitleBaseName))
                    {
                        continue; // Video exists, subtitle is valid
                    }

                    // Check orphan age
                    if (!ConfigHelper.IsFileOldEnoughForDeletion(file.FullName))
                    {
                        PluginLog.LogDebug(TaskName, $"Skipping too-new orphaned subtitle (min age {config.OrphanMinAgeDays}d): {file.FullName}", Logger);
                        continue;
                    }

                    if (dryRun)
                    {
                        PluginLog.LogInfo(TaskName, $"[Dry Run] Would delete orphaned subtitle: {file.FullName}", Logger);
                        deletedCount++;
                    }
                    else if (config.UseTrash)
                    {
                        var trashPath = ConfigHelper.GetTrashPath(libraryPath);
                        long size = TrashService.MoveFileToTrash(file.FullName, trashPath, Logger);
                        if (size > 0)
                        {
                            bytesFreed += size;
                            deletedCount++;
                        }
                    }
                    else
                    {
                        PluginLog.LogInfo(TaskName, $"Deleting orphaned subtitle: {file.FullName}", Logger);
                        try
                        {
                            long size = file.Length;
                            File.Delete(file.FullName);
                            bytesFreed += size;
                            deletedCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            PluginLog.LogError(TaskName, $"Failed to delete: {file.FullName}", ex, Logger);
                        }
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
    /// Extracts the base name of a subtitle file, stripping language and format suffixes.
    /// For example:
    ///   "Movie Name (2021).en.srt" → "Movie Name (2021)"
    ///   "Movie Name (2021).en.forced.srt" → "Movie Name (2021)"
    ///   "Movie Name (2021).srt" → "Movie Name (2021)"
    ///   "Movie Name (2021).de.hi.ass" → "Movie Name (2021)".
    /// </summary>
    /// <param name="filePath">The full path to the subtitle file.</param>
    /// <returns>The base name without language and format suffixes.</returns>
    internal static string GetSubtitleBaseName(string filePath)
    {
        // Start with filename without extension: "Movie.en.forced"
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

        // Known subtitle suffixes to strip (language codes, flags)
        // We strip from right to left as long as the last segment matches a known pattern
        var parts = nameWithoutExt.Split('.');
        if (parts.Length <= 1)
        {
            return nameWithoutExt;
        }

        // Strip known language/flag suffixes from the end
        int endIndex = parts.Length - 1;
        while (endIndex > 0 && IsSubtitleSuffix(parts[endIndex]))
        {
            endIndex--;
        }

        // Rejoin the parts up to endIndex
        return string.Join('.', parts, 0, endIndex + 1);
    }

    /// <summary>
    /// Determines whether a string segment is a known subtitle suffix (language code or flag).
    /// Uses explicit allowlists to avoid false positives with non-language segments like "DTS", "HDR", etc.
    /// </summary>
    private static bool IsSubtitleSuffix(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        return MediaExtensions.SubtitleFlags.Contains(segment) || MediaExtensions.KnownLanguageCodes.Contains(segment);
    }
}