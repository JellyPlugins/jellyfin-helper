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
/// A scheduled task to clean up orphaned trickplay folders.
/// </summary>
public class CleanTrickplayTask : IScheduledTask
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

    /// <inheritdoc />
    public virtual string Name => "Trickplay Folder Cleaner";

    /// <inheritdoc />
    public virtual string Key => "TrickplayFolderCleaner";

    /// <inheritdoc />
    public virtual string Description => "Deletes .trickplay folders that no longer have a corresponding media file.";

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
            _logger.LogInformation("Starting trickplay folder cleanup (Dry Run). No folders will be deleted.");
        }
        else
        {
            _logger.LogInformation("Starting trickplay folder cleanup.");
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
            totalDeleted += CleanDirectory(folder, dryRun, cancellationToken);
            progress.Report((double)(i + 1) / libraryFolders.Count * 100);
        }

        if (dryRun)
        {
            _logger.LogInformation("Trickplay folder cleanup (Dry Run) finished. Would have deleted {Count} folders.", totalDeleted);
        }
        else
        {
            _logger.LogInformation("Trickplay folder cleanup finished. Deleted {Count} folders.", totalDeleted);
        }

        return Task.CompletedTask;
    }

    private int CleanDirectory(string path, bool dryRun, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
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

                if (dryRun)
                {
                    _logger.LogInformation("[Dry Run] Would delete orphaned trickplay folder: {Path}", dir.FullName);
                    deletedCount++;
                }
                else
                {
                    _logger.LogInformation("Deleting orphaned trickplay folder: {Path}", dir.FullName);
                    try
                    {
                        Directory.Delete(dir.FullName, true);
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

        return deletedCount;
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
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            }
        ];
    }
}