using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     Computes a cumulative growth timeline based on media file creation dates.
///     Uses a baseline snapshot from the first scan to enable accurate diff-based
///     growth tracking on subsequent scans.
///     Automatically selects the best granularity (daily/weekly/monthly/quarterly/yearly)
///     depending on the time span between the oldest file and today.
///     Pure aggregation logic is delegated to <see cref="TimelineAggregator" />.
/// </summary>
public sealed class GrowthTimelineService : IGrowthTimelineService, IDisposable
{
    private const string LogSource = "GrowthTimeline";

    private const string TimelineFileName = "jellyfin-helper-growth-timeline.json";
    private const string BaselineFileName = "jellyfin-helper-growth-baseline.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _baselineFilePath;
    private readonly ICleanupConfigHelper _configHelper;

    // Guards individual file I/O operations (load/save).
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    // Guards the entire load-compute-save sequence so two concurrent invocations of
    // ComputeTimelineAsync cannot both read the same baseline and then overwrite each
    // other's results (TOCTOU on the baseline/timeline files).
    private readonly SemaphoreSlim _computeLock = new(1, 1);

    private readonly IFileSystem _fileSystem;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<GrowthTimelineService> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly string _timelineFilePath;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GrowthTimelineService" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    public GrowthTimelineService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IPluginLogService pluginLog,
        IApplicationPaths applicationPaths,
        ILogger<GrowthTimelineService> logger,
        ICleanupConfigHelper configHelper)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _pluginLog = pluginLog;
        _configHelper = configHelper;
        _logger = logger;
        _timelineFilePath = Path.Join(applicationPaths.DataPath, TimelineFileName);
        _baselineFilePath = Path.Join(applicationPaths.DataPath, BaselineFileName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Computes the growth timeline by scanning top-level media directories.
    ///     On the first scan, creates a baseline snapshot and builds a historical timeline
    ///     from directory creation dates. On subsequent scans, uses an append-only snapshot
    ///     approach: all previously persisted data points are treated as immutable history,
    ///     and only the current time-bucket is updated with the actual total size/count.
    ///     This ensures that deleting files whose creation dates lie in the past does NOT
    ///     retroactively alter historical data points - the deletion shows up as a drop
    ///     at the current point in time.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result.</returns>
    public async Task<GrowthTimelineResult> ComputeTimelineAsync(CancellationToken cancellationToken)
    {
        _pluginLog.LogInfo(LogSource, "Starting growth timeline computation...", _logger);

        cancellationToken.ThrowIfCancellationRequested();

        // Serialise the entire read-compute-write sequence. Without this gate two concurrent
        // callers (e.g. a scheduled task and an API-triggered scan) both read the same baseline,
        // compute independently, and the second SaveBaseline/SaveTimeline call silently discards
        // the first caller's updates (TOCTOU on the persisted files).
        await _computeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var currentDirs = CollectDirectoryEntries(cancellationToken);

            if (currentDirs.Count == 0)
            {
                _pluginLog.LogInfo(LogSource, "No media directories found for growth timeline.", _logger);
                return await BuildEmptyStateResultAsync(now, cancellationToken).ConfigureAwait(false);
            }

            _pluginLog.LogInfo(LogSource, $"Collected {currentDirs.Count} media directories.", _logger);

            cancellationToken.ThrowIfCancellationRequested();

            var baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
            baseline = DiscardLegacyBaseline(baseline);

            List<GrowthTimelinePoint> dataPoints;

            if (baseline == null)
            {
                (dataPoints, baseline) = await BuildFirstScanTimelineAsync(currentDirs, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                dataPoints = await BuildSubsequentScanTimelineAsync(currentDirs, baseline, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Trim leading zero-value data points but keep one zero just before the first non-zero
            // as a visual baseline start. This avoids long flat 0-lines for historical buckets
            // before any media existed, while still showing a library rebuild (drop to 0 then rise).
            dataPoints = TimelineAggregator.TrimLeadingZeros(dataPoints);

            // Consolidate data points into the current granularity.
            // When the time span grows (e.g. from <90 days to >90 days), the granularity
            // upgrades (daily->weekly). Previously stored finer-grained points are merged
            // into the coarser buckets so the persisted file stays compact.
            var finalGranularity = dataPoints.Count > 0
                ? TimelineAggregator.DetermineGranularity(dataPoints[0].Date, now)
                : "monthly";
            dataPoints = TimelineAggregator.ConsolidateToGranularity(dataPoints, finalGranularity);

            // Remove consecutive data points with identical values to reduce storage size.
            // The UI will interpolate missing buckets back when rendering the chart.
            dataPoints = TimelineAggregator.DeduplicateConsecutivePoints(dataPoints);

            if (dataPoints.Count == 0)
            {
                _pluginLog.LogInfo(LogSource, "No timeline data points after processing.", _logger);
                return new GrowthTimelineResult
                {
                    ComputedAt = now,
                    Granularity = "monthly",
                    FirstScanTimestamp = baseline.FirstScanTimestamp
                };
            }

            var result = new GrowthTimelineResult
            {
                Granularity = finalGranularity,
                EarliestFileDate = dataPoints[0].Date,
                ComputedAt = now,
                TotalDirectoriesScanned = currentDirs.Count,
                FirstScanTimestamp = baseline.FirstScanTimestamp
            };

            foreach (var point in dataPoints)
            {
                result.DataPoints.Add(point);
            }

            // Persist to disk
            cancellationToken.ThrowIfCancellationRequested();
            await SaveTimelineAsync(result, cancellationToken).ConfigureAwait(false);

            _pluginLog.LogInfo(
                LogSource,
                $"Growth timeline computed: {dataPoints.Count} data points ({finalGranularity})",
                _logger);
            return result;
        }
        finally
        {
            _computeLock.Release();
        }
    }

    /// <summary>
    ///     Builds the result for a scan that found no media directories. Persists a 0-snapshot so the
    ///     timeline reflects the empty state instead of showing stale data from a previous scan; when
    ///     there is no prior timeline, returns an empty monthly result.
    /// </summary>
    /// <param name="now">The current scan timestamp.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result for the empty state.</returns>
    private async Task<GrowthTimelineResult> BuildEmptyStateResultAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Persist a 0-snapshot so that the timeline reflects the empty state
        // instead of showing stale data from a previous scan.
        var existingTimeline = await LoadTimelineAsync(cancellationToken).ConfigureAwait(false);
        if (existingTimeline is not { DataPoints.Count: > 0 })
        {
            return new GrowthTimelineResult
            {
                ComputedAt = now,
                Granularity = "monthly"
            };
        }

        var earliestExisting = existingTimeline.DataPoints[0].Date;
        var granularity = TimelineAggregator.DetermineGranularity(earliestExisting, now);
        var zeroPoints = TimelineAggregator.MergeSnapshotIntoTimeline(
            existingTimeline.DataPoints.ToList(),
            now,
            0,
            0,
            granularity);

        // Run through the same finalization path as normal scans
        zeroPoints = TimelineAggregator.TrimLeadingZeros(zeroPoints);
        zeroPoints = TimelineAggregator.ConsolidateToGranularity(zeroPoints, granularity);
        zeroPoints = TimelineAggregator.DeduplicateConsecutivePoints(zeroPoints);

        var zeroResult = new GrowthTimelineResult
        {
            ComputedAt = now,
            Granularity = granularity,
            EarliestFileDate = zeroPoints.Count > 0 ? zeroPoints[0].Date : earliestExisting,
            FirstScanTimestamp = existingTimeline.FirstScanTimestamp
        };
        foreach (var p in zeroPoints)
        {
            zeroResult.DataPoints.Add(p);
        }

        await SaveTimelineAsync(zeroResult, cancellationToken).ConfigureAwait(false);
        return zeroResult;
    }

    /// <summary>
    ///     Discards legacy baselines that used grouped keys (containing a '|' separator). These are
    ///     incompatible with the per-directory format and would produce incorrect diffs.
    /// </summary>
    /// <param name="baseline">The loaded baseline (may be null).</param>
    /// <returns>The baseline unchanged, or <see langword="null"/> when a legacy baseline was discarded.</returns>
    private GrowthTimelineBaseline? DiscardLegacyBaseline(GrowthTimelineBaseline? baseline)
    {
        if (baseline is { Directories.Count: > 0 })
        {
            var firstKey = baseline.Directories.Keys.First();
            if (firstKey.Contains('|', StringComparison.Ordinal))
            {
                _pluginLog.LogInfo(
                    LogSource,
                    $"Discarding legacy grouped baseline ({baseline.Directories.Count} entries). A new per-directory baseline will be created.",
                    _logger);
                return null;
            }
        }

        return baseline;
    }

    /// <summary>
    ///     First scan: creates and persists a baseline from the current directories and builds the
    ///     initial historical timeline from their creation dates and current sizes.
    /// </summary>
    /// <param name="currentDirs">The currently scanned directories.</param>
    /// <param name="now">The current scan timestamp.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The initial data points together with the newly created baseline.</returns>
    private async Task<(List<GrowthTimelinePoint> DataPoints, GrowthTimelineBaseline Baseline)> BuildFirstScanTimelineAsync(
        List<DirectoryEntry> currentDirs,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // === FIRST SCAN: Create baseline and build historical timeline ===
        _pluginLog.LogInfo(
            LogSource,
            $"First scan: creating baseline with {currentDirs.Count} directory entries.",
            _logger);

        var baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now };
        foreach (var dir in currentDirs)
        {
            baseline.Directories[dir.Path] = new BaselineDirectoryEntry
            {
                CreatedUtc = dir.CreatedUtc,
                Size = dir.Size,
                Count = dir.Count
            };
        }

        await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);

        // For the first scan, use creation dates with current sizes (historical reconstruction)
        var timelineEntries = currentDirs.Select(d => new FileEntry
        {
            CreatedUtc = d.CreatedUtc,
            Size = d.Size,
            CountDelta = d.Count
        }).ToList();

        timelineEntries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        var earliest = timelineEntries.Count > 0 ? timelineEntries[0].CreatedUtc : now;
        var granularity = TimelineAggregator.DetermineGranularity(earliest, now);

        _pluginLog.LogInfo(
            LogSource,
            $"Building initial timeline: {timelineEntries.Count} entries, earliest: {earliest:yyyy-MM-dd}, granularity: {granularity}",
            _logger);

        var dataPoints = TimelineAggregator.BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);
        return (dataPoints, baseline);
    }

    /// <summary>
    ///     Subsequent scan: builds data points using append-only semantics when a timeline exists
    ///     (historical points immutable, only the current bucket updated), or falls back to historical
    ///     reconstruction from the baseline when no timeline exists. Updates and persists the baseline
    ///     with the current state for the next scan.
    /// </summary>
    /// <param name="currentDirs">The currently scanned directories.</param>
    /// <param name="baseline">The existing baseline.</param>
    /// <param name="now">The current scan timestamp.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The computed data points.</returns>
    private async Task<List<GrowthTimelinePoint>> BuildSubsequentScanTimelineAsync(
        List<DirectoryEntry> currentDirs,
        GrowthTimelineBaseline baseline,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // === SUBSEQUENT SCAN: Append-only snapshot ===
        // Historical data points are immutable. We only update the current bucket
        // with the actual current total size/count. This prevents deletions of old
        // files from retroactively altering past data points.
        cancellationToken.ThrowIfCancellationRequested();

        var existingTimeline = await LoadTimelineAsync(cancellationToken).ConfigureAwait(false);

        // Calculate current absolute totals in a single pass (avoids two iterations)
        long currentTotalSize = 0;
        long currentTotalCount = 0;
        foreach (var dir in currentDirs)
        {
            currentTotalSize += dir.Size;
            currentTotalCount += dir.Count;
        }

        List<GrowthTimelinePoint> dataPoints;
        if (existingTimeline is { DataPoints.Count: > 0 })
        {
            // Append-only: preserve historical points, update current bucket
            _pluginLog.LogInfo(
                LogSource,
                $"Append-only scan: {existingTimeline.DataPoints.Count} existing points, current total: {currentTotalSize} bytes, {currentTotalCount} items.",
                _logger);

            var earliestExisting = existingTimeline.DataPoints[0].Date;
            var granularity = TimelineAggregator.DetermineGranularity(earliestExisting, now);

            dataPoints = TimelineAggregator.MergeSnapshotIntoTimeline(
                existingTimeline.DataPoints.ToList(),
                now,
                currentTotalSize,
                currentTotalCount,
                granularity);
        }
        else
        {
            // No existing timeline (e.g. first incremental scan after migration or data loss).
            // Fall back to historical reconstruction using baseline + current state.
            _pluginLog.LogInfo(
                LogSource,
                "No existing timeline found. Performing historical reconstruction from baseline.",
                _logger);

            var timelineEntries = TimelineAggregator.BuildIncrementalEntries(currentDirs, baseline, now);
            timelineEntries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

            var earliest = timelineEntries.Count > 0 ? timelineEntries[0].CreatedUtc : now;
            var granularity = TimelineAggregator.DetermineGranularity(earliest, now);

            dataPoints = TimelineAggregator.BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);
        }

        // Update baseline with current state for next scan
        TimelineAggregator.UpdateBaseline(baseline, currentDirs);
        await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);

        return dataPoints;
    }

    /// <summary>
    ///     Loads the last computed timeline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The cached timeline or null.</returns>
    public async Task<GrowthTimelineResult?> LoadTimelineAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_timelineFilePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(_timelineFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GrowthTimelineResult>(json, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Could not load cached timeline from {_timelineFilePath}",
                ex,
                _logger);
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Collects top-level media directory entries (path, creation date, total size) from all libraries.
    ///     Each top-level subdirectory in a library (e.g. a movie folder or TV show folder)
    ///     becomes one entry using its directory creation date and the total size of all files within.
    ///     Files directly in a library root are also collected as individual entries.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private List<DirectoryEntry> CollectDirectoryEntries(CancellationToken cancellationToken)
    {
        var entries = new List<DirectoryEntry>();
        var locations = LibraryPathResolver.GetDistinctLibraryLocations(_libraryManager);
        var config = _configHelper.GetConfig();
        var trashFolderName = (config.TrashFolderPath ?? string.Empty).Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip library roots that are symlinks/junctions to prevent double-counting
            // media that resides in another library or pulling external trees into the timeline.
            try
            {
                var locationAttrs = new DirectoryInfo(location).Attributes;
                if ((locationAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogDebug(
                    LogSource,
                    $"Skipping inaccessible library root during reparse-point check: {location}: {ex.Message}",
                    _logger);
                continue;
            }

            CollectLocationEntries(location, trashFolderName, entries, cancellationToken);
        }

        return entries;
    }

    /// <summary>
    ///     Scans a single (non-reparse-point) library root for top-level media directories and loose
    ///     media files, appending the resulting entries to <paramref name="entries" />.
    /// </summary>
    /// <param name="location">The library root path.</param>
    /// <param name="trashFolderName">Leaf name of the trash folder to skip (may be empty).</param>
    /// <param name="entries">The entry list to append to.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private void CollectLocationEntries(
        string location,
        string trashFolderName,
        List<DirectoryEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the full trash path for this library root (handles both relative and absolute paths)
            var fullTrashPath = Path.GetFullPath(_configHelper.GetTrashPath(location))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Collect top-level subdirectories as media items
            foreach (var subDirPath in _fileSystem.GetDirectories(location).Select(subDir => subDir.FullName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryAddSubdirectoryEntry(subDirPath, trashFolderName, fullTrashPath, entries, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Also collect loose files directly in the library root
            foreach (var file in _fileSystem.GetFiles(location))
            {
                TryAddLooseFileEntry(file, entries);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not scan {location}", ex, _logger);
        }
    }

    /// <summary>
    ///     Evaluates a single top-level subdirectory and, when it qualifies, adds a directory entry
    ///     for it to <paramref name="entries" />. Skips .trickplay/trash folders, reparse points, and
    ///     directories with no usable creation date or zero total size.
    /// </summary>
    /// <param name="subDirPath">The subdirectory path.</param>
    /// <param name="trashFolderName">Leaf name of the trash folder to skip (may be empty).</param>
    /// <param name="fullTrashPath">Resolved absolute path of the trash folder to skip (may be empty).</param>
    /// <param name="entries">The entry list to append to.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private void TryAddSubdirectoryEntry(
        string subDirPath,
        string trashFolderName,
        string fullTrashPath,
        List<DirectoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var dirName = Path.GetFileName(subDirPath);

        // Skip .trickplay and trash directories
        if (ShouldSkipDirectory(subDirPath, dirName, trashFolderName, fullTrashPath))
        {
            return;
        }

        // Skip symlinks/junctions at the top level to prevent double-counting
        // media that resides in another library or pulling external trees into
        // the timeline. Child directories are checked inside GetDirectorySize().
        try
        {
            var topLevelAttrs = new DirectoryInfo(subDirPath).Attributes;
            if ((topLevelAttrs & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogDebug(
                LogSource,
                $"Skipping inaccessible subdirectory during reparse-point check: {subDirPath}: {ex.Message}",
                _logger);
            return;
        }

        // Use directory creation date as "when this media was added". The timestamps
        // must come from a live stat (Directory.Get*TimeUtc), NOT from the
        // FileSystemMetadata the enumeration returned: Jellyfin's IFileSystem does not
        // reliably populate those on every platform (they can come back as the
        // DateTime.MinValue default), which would skip every entry. Skip only when no
        // sane date can be derived from a real stat.
        var createdUtc = ResolveEntryDateUtc(
            Directory.GetCreationTimeUtc(subDirPath),
            Directory.GetLastWriteTimeUtc(subDirPath));
        if (createdUtc is null)
        {
            return;
        }

        // Sum up all file sizes recursively within this directory
        var totalSize = GetDirectorySize(
            subDirPath,
            trashFolderName,
            fullTrashPath,
            cancellationToken);
        if (totalSize > 0)
        {
            entries.Add(
                new DirectoryEntry
                {
                    Path = subDirPath,
                    CreatedUtc = createdUtc.Value,
                    Size = totalSize,
                    Count = 1
                });
        }
    }

    /// <summary>
    ///     Evaluates a single loose file in a library root and, when it is recognised media with a
    ///     usable date, adds a directory entry for it to <paramref name="entries" />.
    /// </summary>
    /// <param name="file">The file metadata.</param>
    /// <param name="entries">The entry list to append to.</param>
    private static void TryAddLooseFileEntry(FileSystemMetadata file, List<DirectoryEntry> entries)
    {
        var ext = Path.GetExtension(file.FullName);
        if (!MediaExtensions.VideoExtensions.Contains(ext) &&
            !MediaExtensions.AudioExtensionToCodec.ContainsKey(ext))
        {
            return;
        }

        // Read the "added" date from a live stat (same fallback rule as directories).
        // See the directory branch above for why the FileSystemMetadata timestamps are
        // not trusted here.
        var createdUtc = ResolveEntryDateUtc(
            File.GetCreationTimeUtc(file.FullName),
            File.GetLastWriteTimeUtc(file.FullName));
        if (createdUtc is null)
        {
            return;
        }

        entries.Add(
            new DirectoryEntry
            {
                Path = file.FullName,
                CreatedUtc = createdUtc.Value,
                Size = file.Length,
                Count = 1
            });
    }

    /// <summary>
    ///     Determines whether a directory should be skipped during traversal.
    ///     Matches .trickplay directories and trash directories by leaf name (relative paths)
    ///     or resolved full path (absolute paths).
    /// </summary>
    private static bool ShouldSkipDirectory(
        string fullName,
        string dirName,
        string trashFolderName,
        string fullTrashPath)
    {
        if (dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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
    ///     Resolves the "added" date for a timeline entry from a live stat's creation/last-write
    ///     timestamps. Single source of truth for the historical rule, shared by the directory and
    ///     loose-file paths: prefer creation time; if it is a pre-1990 sentinel (filesystems that do
    ///     not track creation time, e.g. Linux ext4, report a near-epoch value), fall back to
    ///     last-write time; if both are pre-1990 the date is unusable and the caller skips the entry.
    /// </summary>
    /// <param name="creationTimeUtc">The entry's creation timestamp (UTC).</param>
    /// <param name="lastWriteTimeUtc">The entry's last-write timestamp (UTC).</param>
    /// <returns>The resolved UTC date, or <see langword="null"/> when neither timestamp is usable.</returns>
    private static DateTime? ResolveEntryDateUtc(DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
    {
        if (creationTimeUtc.Year >= 1990)
        {
            return DateTime.SpecifyKind(creationTimeUtc, DateTimeKind.Utc);
        }

        if (lastWriteTimeUtc.Year >= 1990)
        {
            return DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc);
        }

        return null;
    }

    /// <summary>
    ///     Calculates the total size of all files within a directory tree (iterative, stack-based).
    ///     Symlinks and junction points are skipped to prevent cycles. The explicit stack eliminates
    ///     the StackOverflowException risk that a recursive implementation would carry on very deep
    ///     or pathologically wide library trees.
    /// </summary>
    /// <param name="directoryPath">The directory to measure.</param>
    /// <param name="trashFolderName">Leaf name of the trash folder to skip (may be empty).</param>
    /// <param name="fullTrashPath">Resolved absolute path of the trash folder to skip (may be empty).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Total size in bytes of all files inside the directory tree, excluding skipped paths.</returns>
    internal long GetDirectorySize(
        string directoryPath,
        string trashFolderName,
        string fullTrashPath,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(directoryPath);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            try
            {
                foreach (var file in _fileSystem.GetFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += file.Length;
                }

                EnqueueChildDirectories(current, trashFolderName, fullTrashPath, stack, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogDebug(
                    LogSource,
                    $"Skipping inaccessible directory: {current}: {ex.Message}",
                    _logger);
            }
        }

        return total;
    }

    /// <summary>
    ///     Pushes traversable child directories of <paramref name="current" /> onto the stack,
    ///     skipping .trickplay/trash folders and reparse points (symlinks/junctions) to prevent cycles.
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

            // Skip .trickplay and trash subdirectories
            if (ShouldSkipDirectory(subDirPath, dirName, trashFolderName, fullTrashPath))
            {
                continue;
            }

            // Never follow symlinks or junction points - they can form cycles (A -> B -> A).
            FileAttributes attributes;
            try
            {
                attributes = new DirectoryInfo(subDirPath).Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogDebug(
                    LogSource,
                    $"Skipping inaccessible subdirectory during attribute check: {subDirPath}: {ex.Message}",
                    _logger);
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            stack.Push(subDirPath);
        }
    }

    /// <summary>
    ///     Loads the baseline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The baseline or null if not found.</returns>
    private async Task<GrowthTimelineBaseline?> LoadBaselineAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_baselineFilePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(_baselineFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<GrowthTimelineBaseline>(json, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not load baseline from {_baselineFilePath}", ex, _logger);
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Persists the baseline to disk.
    /// </summary>
    /// <param name="baseline">The baseline to save.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task SaveBaselineAsync(GrowthTimelineBaseline baseline, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(baseline, JsonOptions);
            await AtomicFile.WriteAllTextAsync(_baselineFilePath, json, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not save baseline to {_baselineFilePath}", ex, _logger);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Persists the timeline result to disk.
    /// </summary>
    /// <param name="result">The timeline result to save.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task SaveTimelineAsync(GrowthTimelineResult result, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            await AtomicFile.WriteAllTextAsync(_timelineFilePath, json, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not save timeline to {_timelineFilePath}", ex, _logger);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    ///     Releases the managed resources used by the <see cref="GrowthTimelineService" />.
    /// </summary>
    /// <param name="disposing">true to release managed resources; false for native resources only.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileLock.Dispose();
            _computeLock.Dispose();
        }
    }

    /// <summary>
    ///     Internal struct for timeline construction - a size contribution at a point in time.
    /// </summary>
    internal struct FileEntry
    {
        public DateTime CreatedUtc;
        public long Size;
        public long CountDelta;
    }

    /// <summary>
    ///     Internal struct for collecting directory metadata during scanning.
    ///     Includes the path for baseline comparison.
    /// </summary>
    internal struct DirectoryEntry
    {
        public string Path;
        public DateTime CreatedUtc;
        public long Size;
        public long Count;
    }
}
