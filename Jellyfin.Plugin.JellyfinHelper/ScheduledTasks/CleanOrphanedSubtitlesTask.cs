using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services;
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
public class CleanOrphanedSubtitlesTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CleanOrphanedSubtitlesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanOrphanedSubtitlesTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public CleanOrphanedSubtitlesTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<CleanOrphanedSubtitlesTask> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Executes the orphaned subtitle cleanup.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var effectiveDryRun = CleanupConfigHelper.IsDryRunOrphanedSubtitles();
        var config = CleanupConfigHelper.GetConfig();

        if (effectiveDryRun)
        {
            _logger.LogInformation("Starting orphaned subtitle cleanup (Dry Run). No files will be deleted.");
        }
        else
        {
            _logger.LogInformation("Starting orphaned subtitle cleanup.");
        }

        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);

        int totalDeleted = 0;
        long totalBytesFreed = 0;

        for (int i = 0; i < libraryFolders.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var folder = libraryFolders[i];
            _logger.LogDebug("Scanning library folder for orphaned subtitles: {Folder}", folder);
            var (deleted, bytesFreed) = CleanDirectory(folder, effectiveDryRun, cancellationToken);
            totalDeleted += deleted;
            totalBytesFreed += bytesFreed;
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        if (effectiveDryRun)
        {
            _logger.LogInformation("Orphaned subtitle cleanup (Dry Run) finished. Would have deleted {Count} files.", totalDeleted);
        }
        else
        {
            _logger.LogInformation("Orphaned subtitle cleanup finished. Deleted {Count} files, freed {Bytes} bytes.", totalDeleted, totalBytesFreed);
        }

        if (!effectiveDryRun && totalDeleted > 0)
        {
            CleanupTrackingService.RecordCleanup(totalBytesFreed, totalDeleted, _logger);
        }

        return Task.CompletedTask;
    }

    private (int Deleted, long BytesFreed) CleanDirectory(string rootPath, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        long bytesFreed = 0;
        var config = CleanupConfigHelper.GetConfig();

        try
        {
            // Process directories: for each directory, check all subtitle files
            var allDirs = new List<string> { rootPath };
            try
            {
                allDirs.AddRange(
                    _fileSystem.GetDirectories(rootPath, true)
                        .Select(d => d.FullName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not enumerate subdirectories of {Path}", rootPath);
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
                var trashFolderName = Path.GetFileName(CleanupConfigHelper.GetTrashPath(rootPath));
                if (Path.GetFileName(dirPath).Equals(trashFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!fileCache.TryGetValue(dirPath, out var files))
                {
                    try
                    {
                        files = _fileSystem.GetFiles(dirPath, false).ToArray();
                        fileCache[dirPath] = files;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not list files in {Path}", dirPath);
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
                    if (!CleanupConfigHelper.IsFileOldEnoughForDeletion(file.FullName))
                    {
                        _logger.LogDebug("Skipping too-new orphaned subtitle (min age {Days}d): {Path}", config.OrphanMinAgeDays, file.FullName);
                        continue;
                    }

                    if (dryRun)
                    {
                        _logger.LogInformation("[Dry Run] Would delete orphaned subtitle: {Path}", file.FullName);
                        deletedCount++;
                    }
                    else if (config.UseTrash)
                    {
                        var trashPath = CleanupConfigHelper.GetTrashPath(rootPath);
                        long size = TrashService.MoveFileToTrash(file.FullName, trashPath, _logger);
                        if (size > 0)
                        {
                            bytesFreed += size;
                            deletedCount++;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Deleting orphaned subtitle: {Path}", file.FullName);
                        try
                        {
                            long size = file.Length;
                            File.Delete(file.FullName);
                            bytesFreed += size;
                            deletedCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogError(ex, "Error deleting file {Path}", file.FullName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory for orphaned subtitles: {Path}", rootPath);
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
