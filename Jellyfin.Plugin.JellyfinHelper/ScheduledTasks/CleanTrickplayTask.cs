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
/// A scheduled task to clean up orphaned trickplay folders.
/// Supports configuration-driven library filtering, orphan age, trash/delete mode, and storage tracking.
/// </summary>
public class CleanTrickplayTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CleanTrickplayTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanTrickplayTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public CleanTrickplayTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<CleanTrickplayTask> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Executes the trickplay folder cleanup.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var effectiveDryRun = CleanupConfigHelper.IsDryRunTrickplay();
        var config = CleanupConfigHelper.GetConfig();

        if (effectiveDryRun)
        {
            _logger.LogInformation("Starting trickplay folder cleanup (Dry Run). No folders will be deleted.");
        }
        else
        {
            _logger.LogInformation("Starting trickplay folder cleanup.");
        }

        if (config.OrphanMinAgeDays > 0)
        {
            _logger.LogInformation("Orphan minimum age: {Days} days", config.OrphanMinAgeDays);
        }

        if (config.UseTrash && !effectiveDryRun)
        {
            _logger.LogInformation("Trash mode enabled. Items will be moved to trash instead of permanent deletion.");
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
            _logger.LogDebug("Scanning library folder: {Folder}", folder);
            var (deleted, bytesFreed) = CleanDirectory(folder, effectiveDryRun, cancellationToken);
            totalDeleted += deleted;
            totalBytesFreed += bytesFreed;
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        if (effectiveDryRun)
        {
            _logger.LogInformation("Trickplay folder cleanup (Dry Run) finished. Would have deleted {Count} folders.", totalDeleted);
        }
        else
        {
            _logger.LogInformation("Trickplay folder cleanup finished. Deleted {Count} folders, freed {Bytes} bytes.", totalDeleted, totalBytesFreed);

            if (totalDeleted > 0)
            {
                CleanupTrackingService.RecordCleanup(totalBytesFreed, totalDeleted, _logger);
            }
        }

        // Purge expired trash items if trash is enabled
        if (config.UseTrash && !effectiveDryRun)
        {
            PurgeExpiredTrashForAllLibraries(libraryFolders, config.TrashRetentionDays);
        }

        return Task.CompletedTask;
    }

    private (int Deleted, long BytesFreed) CleanDirectory(string path, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        long bytesFreed = 0;
        var config = CleanupConfigHelper.GetConfig();

        try
        {
            // Get all directories recursively
            var directories = _fileSystem.GetDirectories(path, true).ToList();

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
                    files = _fileSystem.GetFiles(parentPath).ToArray();
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
                if (!CleanupConfigHelper.IsOldEnoughForDeletion(dir.FullName))
                {
                    _logger.LogDebug("Skipping too-new orphan (min age {Days}d): {Path}", config.OrphanMinAgeDays, dir.FullName);
                    continue;
                }

                if (dryRun)
                {
                    _logger.LogInformation("[Dry Run] Would delete orphaned trickplay folder: {Path}", dir.FullName);
                    deletedCount++;
                }
                else if (config.UseTrash)
                {
                    var trashPath = CleanupConfigHelper.GetTrashPath(path);
                    long size = TrashService.MoveToTrash(dir.FullName, trashPath, _logger);
                    if (size > 0)
                    {
                        bytesFreed += size;
                        deletedCount++;
                    }
                }
                else
                {
                    _logger.LogInformation("Deleting orphaned trickplay folder: {Path}", dir.FullName);
                    try
                    {
                        long size = FileSystemHelper.CalculateDirectorySize(_fileSystem, dir.FullName, _logger);
                        Directory.Delete(dir.FullName, true);
                        bytesFreed += size;
                        deletedCount++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError(ex, "Error deleting directory {Path}", dir.FullName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory {Path}", path);
        }

        return (deletedCount, bytesFreed);
    }

    private void PurgeExpiredTrashForAllLibraries(IReadOnlyList<string> libraryFolders, int retentionDays)
    {
        foreach (var folder in libraryFolders)
        {
            var trashPath = CleanupConfigHelper.GetTrashPath(folder);
            if (Directory.Exists(trashPath))
            {
                var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, retentionDays, _logger);
                if (itemsPurged > 0)
                {
                    _logger.LogInformation("Purged {Count} expired items from trash ({Bytes} bytes): {Path}", itemsPurged, bytesFreed, trashPath);
                }
            }
        }
    }
}
