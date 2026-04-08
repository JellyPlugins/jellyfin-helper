using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FiletreeCleaner.ScheduledTasks;

/// <summary>
/// A scheduled task to clean up media folders that contain files but absolutely no video files
/// anywhere in their entire directory tree.
/// </summary>
/// <remarks>
/// <para>
/// This plugin targets a common scenario: when a movie or episode is deleted, only the video file
/// is removed while the surrounding folder with metadata (.nfo), artwork (.jpg), subtitles (.srt)
/// etc. remains as an orphaned folder.
/// </para>
/// <para>
/// The scan operates on <strong>top-level folders</strong> (direct children of each library root).
/// For each top-level folder, the entire directory tree is checked recursively. If absolutely NO
/// video file exists anywhere in the tree, the entire top-level folder is deleted.
/// If at least one video file exists anywhere (even in a deeply nested subdirectory), the entire
/// folder is left untouched — including subfolders that may not contain videos themselves
/// (e.g. empty Season folders created by Sonarr as "wanted" placeholders).
/// </para>
/// <para>
/// Completely empty folders (containing zero files in the entire tree) are intentionally skipped,
/// as they are often pre-created by tools like Radarr/Sonarr for upcoming media.
/// </para>
/// </remarks>
public class CleanEmptyMediaFoldersTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CleanEmptyMediaFoldersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanEmptyMediaFoldersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public CleanEmptyMediaFoldersTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<CleanEmptyMediaFoldersTask> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public virtual string Name => "Empty Media Folder Cleaner";

    /// <inheritdoc />
    public virtual string Key => "EmptyMediaFolderCleaner";

    /// <inheritdoc />
    public virtual string Description => "Deletes top-level media folders whose entire directory tree contains files but absolutely no video files.";

    /// <inheritdoc />
    public string Category => "Jellyfin Cleaner";

    /// <inheritdoc />
    public virtual Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(false, progress, cancellationToken);
    }

    /// <summary>
    /// Executes the task internally.
    /// </summary>
    /// <param name="dryRun">A value indicating whether to perform a dry run.</param>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    internal Task ExecuteInternalAsync(bool dryRun, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            _logger.LogInformation("Starting empty media folder cleanup (Dry Run). No folders will be deleted.");
        }
        else
        {
            _logger.LogInformation("Starting empty media folder cleanup.");
        }

        var libraryFolders = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int totalDeleted = 0;

        for (int i = 0; i < libraryFolders.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var folder = libraryFolders[i];
            _logger.LogDebug("Scanning library folder: {Folder}", folder);
            totalDeleted += CleanLibraryRoot(folder, dryRun, cancellationToken);
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        if (dryRun)
        {
            _logger.LogInformation("Empty media folder cleanup (Dry Run) finished. Would have deleted {Count} folders.", totalDeleted);
        }
        else
        {
            _logger.LogInformation("Empty media folder cleanup finished. Deleted {Count} folders.", totalDeleted);
        }

        return Task.CompletedTask;
    }

    private int CleanLibraryRoot(string libraryRootPath, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        try
        {
            // Get only the direct child directories of the library root (top-level media folders).
            // Each top-level folder represents a single movie, show, etc.
            var topLevelDirs = _fileSystem.GetDirectories(libraryRootPath, false).ToList();

            foreach (var topDir in topLevelDirs.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
            {
                // Skip .trickplay folders – they are handled by CleanTrickplayTask
                if (topDir.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check the entire tree in a single pass: does it contain any files at all,
                // and does it contain any video files? This avoids traversing the tree twice.
                var (hasAnyFiles, hasVideoFiles) = AnalyzeDirectoryRecursive(topDir.FullName);

                // If the folder tree is completely empty (no files at all), skip it.
                // Empty folders are often pre-created by tools like Radarr/Sonarr for "wanted" media.
                if (!hasAnyFiles)
                {
                    continue;
                }

                // If the folder contains files but zero video files → it's an orphaned media folder
                if (!hasVideoFiles)
                {
                    if (dryRun)
                    {
                        _logger.LogInformation("[Dry Run] Would delete empty media folder: {Path}", topDir.FullName);
                        deletedCount++;
                    }
                    else
                    {
                        _logger.LogInformation("Deleting empty media folder: {Path}", topDir.FullName);
                        try
                        {
                            Directory.Delete(topDir.FullName, true);
                            deletedCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogError(ex, "Error deleting directory {Path}", topDir.FullName);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory {Path}", libraryRootPath);
        }

        return deletedCount;
    }

    /// <summary>
    /// Analyzes a directory tree in a single recursive pass, determining whether
    /// any files exist and whether any of them are video files.
    /// </summary>
    /// <param name="directoryPath">The directory to analyze.</param>
    /// <returns>A tuple indicating whether any files exist and whether any video files exist.</returns>
    private (bool HasAnyFiles, bool HasVideoFiles) AnalyzeDirectoryRecursive(string directoryPath)
    {
        bool hasAnyFiles = false;

        // Check files in the directory itself
        var files = _fileSystem.GetFiles(directoryPath, false);
        foreach (var file in files)
        {
            hasAnyFiles = true;
            if (MediaExtensions.VideoExtensions.Contains(Path.GetExtension(file.FullName)))
            {
                // Video found – no need to scan further
                return (true, true);
            }
        }

        // Check subdirectories recursively
        var subDirs = _fileSystem.GetDirectories(directoryPath, false);
        foreach (var subDir in subDirs)
        {
            var (subHasAnyFiles, subHasVideoFiles) = AnalyzeDirectoryRecursive(subDir.FullName);
            hasAnyFiles |= subHasAnyFiles;
            if (subHasVideoFiles)
            {
                // Video found deeper in the tree – no need to scan further
                return (true, true);
            }
        }

        return (hasAnyFiles, false);
    }

    /// <inheritdoc />
    public virtual IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }
}