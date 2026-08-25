using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     Computes library insights: top-largest directories and recently added/changed media.
///     Scans top-level subdirectories of each library location from the filesystem.
/// </summary>
public sealed class LibraryInsightsService : ILibraryInsightsService
{
    /// <summary>
    ///     Number of days to look back for recent entries.
    /// </summary>
    internal const int RecentDaysWindow = 30;

    /// <summary>
    ///     Maximum number of largest entries per collection type (movies / tvshows).
    /// </summary>
    internal const int TopLargestPerType = 10;

    /// <summary>
    ///     Threshold in hours: if the difference between creation and last-write time
    ///     is less than this, the entry is considered "added" rather than "changed".
    /// </summary>
    internal const int AddedVsChangedThresholdHours = 1;

    /// <summary>
    ///     Maximum number of recent entries returned in the result.
    /// </summary>
    internal const int MaxRecentEntries = 100;

    private readonly ICleanupConfigHelper _configHelper;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryInsightsService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LibraryInsightsService" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public LibraryInsightsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ICleanupConfigHelper configHelper,
        IPluginLogService pluginLog,
        ILogger<LibraryInsightsService> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _configHelper = configHelper;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LibraryInsightsResult> ComputeInsightsAsync(CancellationToken cancellationToken)
    {
        var entries = await Task.Run(() => CollectAllEntries(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return BuildResult(entries);
    }

    /// <summary>
    ///     Collects all top-level media directory entries with library metadata from all libraries.
    /// </summary>
    private List<LibraryInsightEntry> CollectAllEntries(CancellationToken cancellationToken)
    {
        var entries = new List<LibraryInsightEntry>();
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var config = _configHelper.GetConfig();
        var trashFolderName = (config.TrashFolderPath ?? string.Empty).Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var vf in virtualFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var collectionType = vf.CollectionType;

            // Skip music, boxset and book libraries - not relevant for size/recency insights
            // (music/books are many small files, boxsets are logical groupings). These still
            // count toward the storage growth timeline, which scans raw directory sizes.
            if (collectionType is CollectionTypeOptions.music or CollectionTypeOptions.boxsets
                or CollectionTypeOptions.books)
            {
                continue;
            }

            var libraryName = vf.Name ?? "Unknown";
            var collectionTypeStr = collectionType?.ToString() ?? "mixed";

            foreach (var location in vf.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Resolve the full trash path for this library root (handles both relative and absolute paths)
                    var fullTrashPath = Path.GetFullPath(_configHelper.GetTrashPath(location))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    CollectEntriesFromLocation(
                        location,
                        libraryName,
                        collectionTypeStr,
                        trashFolderName,
                        fullTrashPath,
                        entries,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                               or NotSupportedException)
                {
                    _pluginLog.LogWarning("LibraryInsights", $"Could not scan {location}", ex, _logger);
                }
            }
        }

        return entries;
    }

    /// <summary>
    ///     Scans a single library location for top-level media directories and loose files.
    /// </summary>
    private void CollectEntriesFromLocation(
        string location,
        string libraryName,
        string collectionType,
        string trashFolderName,
        string fullTrashPath,
        List<LibraryInsightEntry> entries,
        CancellationToken cancellationToken)
    {
        // Collect top-level subdirectories as media items
        foreach (var subDirPath in _fileSystem.GetDirectories(location).Select(subDir => subDir.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAddSubdirectoryEntry(
                subDirPath,
                libraryName,
                collectionType,
                trashFolderName,
                fullTrashPath,
                entries,
                cancellationToken);
        }

        // Collect loose files directly in the library root
        foreach (var file in _fileSystem.GetFiles(location))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAddLooseFileEntry(file, libraryName, collectionType, entries);
        }
    }

    /// <summary>
    ///     Evaluates a single top-level subdirectory and, when it qualifies, adds a media entry
    ///     for it to <paramref name="entries" />. Skips .trickplay/trash folders and directories
    ///     with no usable creation date or zero size.
    /// </summary>
    /// <param name="subDirPath">The subdirectory path.</param>
    /// <param name="libraryName">The owning library name.</param>
    /// <param name="collectionType">The collection type string.</param>
    /// <param name="trashFolderName">Leaf name of the trash folder to skip (may be empty).</param>
    /// <param name="fullTrashPath">Resolved absolute path of the trash folder to skip (may be empty).</param>
    /// <param name="entries">The entry list to append to.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private void TryAddSubdirectoryEntry(
        string subDirPath,
        string libraryName,
        string collectionType,
        string trashFolderName,
        string fullTrashPath,
        List<LibraryInsightEntry> entries,
        CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(subDirPath);

        // Skip .trickplay directories
        if (dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip trash directories: match by leaf name (relative paths) or resolved full path (absolute paths)
        if (ShouldSkipAsTrash(subDirPath, dirName, trashFolderName, fullTrashPath))
        {
            return;
        }

        var createdUtc = Directory.GetCreationTimeUtc(subDirPath);
        if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
        {
            createdUtc = Directory.GetLastWriteTimeUtc(subDirPath);
        }

        if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
        {
            return;
        }

        var (totalSize, newestFileTime) = GetDirectorySizeAndNewestTime(
            subDirPath, trashFolderName, fullTrashPath, cancellationToken);
        if (totalSize <= 0)
        {
            return;
        }

        // Use the newest file modification time if it's more recent than the directory timestamp
        var dirModifiedUtc = Directory.GetLastWriteTimeUtc(subDirPath);
        var modifiedUtc = newestFileTime > dirModifiedUtc ? newestFileTime : dirModifiedUtc;

        var changeType = DetermineChangeType(createdUtc, modifiedUtc);

        entries.Add(
            new LibraryInsightEntry
            {
                Name = dirName,
                Size = totalSize,
                CreatedUtc = createdUtc,
                ModifiedUtc = modifiedUtc,
                LibraryName = libraryName,
                CollectionType = collectionType,
                ChangeType = changeType
            });
    }

    /// <summary>
    ///     Evaluates a single loose file in a library root and, when it is recognised media,
    ///     adds an entry for it to <paramref name="entries" />.
    /// </summary>
    /// <param name="file">The file metadata.</param>
    /// <param name="libraryName">The owning library name.</param>
    /// <param name="collectionType">The collection type string.</param>
    /// <param name="entries">The entry list to append to.</param>
    private static void TryAddLooseFileEntry(
        FileSystemMetadata file,
        string libraryName,
        string collectionType,
        List<LibraryInsightEntry> entries)
    {
        var ext = Path.GetExtension(file.FullName);
        if (!MediaExtensions.VideoExtensions.Contains(ext) &&
            !MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
        {
            return;
        }

        var createdUtc = File.GetCreationTimeUtc(file.FullName);
        if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
        {
            createdUtc = File.GetLastWriteTimeUtc(file.FullName);
        }

        if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
        {
            return;
        }

        var modifiedUtc = File.GetLastWriteTimeUtc(file.FullName);
        var changeType = DetermineChangeType(createdUtc, modifiedUtc);

        entries.Add(
            new LibraryInsightEntry
            {
                Name = Path.GetFileNameWithoutExtension(file.FullName),
                Size = file.Length,
                CreatedUtc = createdUtc,
                ModifiedUtc = modifiedUtc,
                LibraryName = libraryName,
                CollectionType = collectionType,
                ChangeType = changeType
            });
    }

    /// <summary>
    ///     Builds the result from the collected entries: top-largest and recent.
    /// </summary>
    /// <param name="entries">All collected insight entries.</param>
    /// <returns>The aggregated insights result.</returns>
    internal static LibraryInsightsResult BuildResult(List<LibraryInsightEntry> entries)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-RecentDaysWindow);

        // === Largest ===
        // Separate by collection type, take top N per type
        var movieEntries = entries
            .Where(e => IsMovieType(e.CollectionType))
            .OrderByDescending(e => e.Size)
            .Take(TopLargestPerType);

        var showEntries = entries
            .Where(e => IsTvShowType(e.CollectionType))
            .OrderByDescending(e => e.Size)
            .Take(TopLargestPerType);

        // For "mixed" or other types, include them in movies category
        var otherEntries = entries
            .Where(e => !IsMovieType(e.CollectionType) && !IsTvShowType(e.CollectionType))
            .OrderByDescending(e => e.Size)
            .Take(TopLargestPerType);

        var largest = movieEntries
            .Concat(showEntries)
            .Concat(otherEntries)
            .OrderByDescending(e => e.Size)
            .ToList();

        var largestTotalSize = largest.Sum(e => e.Size);

        // === Recent (last 30 days) ===
        var recentQuery = entries
            .Where(e => GetRelevantDate(e) >= cutoff)
            .OrderByDescending(GetRelevantDate)
            .ToList();

        var recentTotalCount = recentQuery.Count;

        var recentAll = recentQuery.Count > MaxRecentEntries
            ? recentQuery.Take(MaxRecentEntries).ToList()
            : recentQuery;

        // === Library sizes ===
        var librarySizes = entries
            .GroupBy(e => e.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Size), StringComparer.OrdinalIgnoreCase);

        return new LibraryInsightsResult
        {
            Largest = largest,
            LargestTotalSize = largestTotalSize,
            Recent = recentAll,
            RecentTotalCount = recentTotalCount,
            LibrarySizes = librarySizes,
            ComputedAtUtc = now
        };
    }

    /// <summary>
    ///     Returns the relevant date for a recent entry: the LATER of created/modified. Using the max
    ///     (rather than "modified when changed, created when added") guarantees the recency filter can
    ///     never hide a genuinely new item behind a stale, preserved mtime that predates its created
    ///     time (common when media is copied with -p / restored from backup - the mtime is older than
    ///     the moment Jellyfin first saw the file).
    /// </summary>
    private static DateTime GetRelevantDate(LibraryInsightEntry entry)
    {
        return entry.ModifiedUtc > entry.CreatedUtc ? entry.ModifiedUtc : entry.CreatedUtc;
    }

    /// <summary>
    ///     Determines whether the entry is "added" (new) or "changed" (modified after creation).
    /// </summary>
    /// <param name="createdUtc">The creation date in UTC.</param>
    /// <param name="modifiedUtc">The last modified date in UTC.</param>
    /// <returns>"changed" only when modified is meaningfully AFTER created; otherwise "added".</returns>
    internal static string DetermineChangeType(DateTime createdUtc, DateTime modifiedUtc)
    {
        // Signed, not Math.Abs: a modified time EARLIER than created (a stale/preserved mtime) is not
        // a real "change" - it must classify as "added" so GetRelevantDate ranks it by the newer
        // created time. Only a modified time that is genuinely later than created by the threshold
        // counts as "changed".
        var hoursAfterCreation = (modifiedUtc - createdUtc).TotalHours;
        return hoursAfterCreation >= AddedVsChangedThresholdHours ? "changed" : "added";
    }

    private static bool IsMovieType(string collectionType)
    {
        return string.Equals(collectionType, "movies", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(collectionType, "homevideos", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(collectionType, "musicvideos", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTvShowType(string collectionType)
    {
        return string.Equals(collectionType, "tvshows", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Determines whether a subdirectory should be skipped as a trash folder,
    ///     matching by leaf name (for relative trash paths) or resolved full path (for absolute trash paths).
    /// </summary>
    private static bool ShouldSkipAsTrash(string fullName, string dirName, string trashFolderName, string fullTrashPath)
    {
        if (!string.IsNullOrEmpty(trashFolderName) &&
            string.Equals(dirName, trashFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedFullName = Path.GetFullPath(fullName)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrEmpty(fullTrashPath) &&
               string.Equals(normalizedFullName, fullTrashPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Calculates the total size and newest file modification time within a directory (iterative, stack-based).
    /// </summary>
    private (long Size, DateTime NewestFileTime) GetDirectorySizeAndNewestTime(
        string directoryPath,
        string trashFolderName,
        string fullTrashPath,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var newestTime = DateTime.MinValue;
        var stack = new Stack<string>();
        stack.Push(directoryPath);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var file in _fileSystem.GetFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += file.Length;
                    if (file.LastWriteTimeUtc > newestTime)
                    {
                        newestTime = file.LastWriteTimeUtc;
                    }
                }

                EnqueueChildDirectories(current, trashFolderName, fullTrashPath, stack, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogDebug(
                    "LibraryInsights",
                    $"Skipping inaccessible directory: {current}: {ex.Message}",
                    _logger);
            }
        }

        return (total, newestTime);
    }

    /// <summary>
    ///     Pushes traversable child directories of <paramref name="current" /> onto the stack,
    ///     skipping reparse points (symlinks/junctions), .trickplay directories, and trash folders.
    /// </summary>
    /// <param name="current">The directory whose children are being enumerated.</param>
    /// <param name="trashFolderName">Leaf name of the trash folder to skip (may be empty).</param>
    /// <param name="fullTrashPath">Resolved absolute path of the trash folder to skip (may be empty).</param>
    /// <param name="stack">The traversal stack to push child directories onto.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private void EnqueueChildDirectories(
        string current,
        string trashFolderName,
        string fullTrashPath,
        Stack<string> stack,
        CancellationToken cancellationToken)
    {
        foreach (var subDirPath in _fileSystem.GetDirectories(current).Select(subDir => subDir.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(subDirPath);

            // Check for reparse point (symlink/junction) to avoid cycles
            FileAttributes attributes;
            try
            {
                attributes = new DirectoryInfo(subDirPath).Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogDebug(
                    "LibraryInsights",
                    $"Skipping inaccessible subdirectory during attribute check: {subDirPath}: {ex.Message}",
                    _logger);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ShouldSkipAsTrash(subDirPath, dirName, trashFolderName, fullTrashPath))
            {
                continue;
            }

            stack.Push(subDirPath);
        }
    }
}