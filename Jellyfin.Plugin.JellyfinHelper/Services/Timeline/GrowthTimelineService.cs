using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
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
    private const string TimelineFileName = "jellyfin-helper-growth-timeline.json";
    private const string BaselineFileName = "jellyfin-helper-growth-baseline.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _baselineFilePath;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
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
    ///     retroactively alter historical data points — the deletion shows up as a drop
    ///     at the current point in time.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result.</returns>
    public async Task<GrowthTimelineResult> ComputeTimelineAsync(CancellationToken cancellationToken)
    {
        _pluginLog.LogInfo("GrowthTimeline", "Starting growth timeline computation...", _logger);

        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var currentDirs = CollectDirectoryEntries(cancellationToken);

        if (currentDirs.Count == 0)
        {
            _pluginLog.LogInfo("GrowthTimeline", "No media directories found for growth timeline.", _logger);

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

        _pluginLog.LogInfo("GrowthTimeline", $"Collected {currentDirs.Count} media directories.", _logger);

        cancellationToken.ThrowIfCancellationRequested();

        var baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);

        // Discard legacy baselines that used grouped keys (contain '|' separator).
        // These are incompatible with the per-directory format and would produce incorrect diffs.
        if (baseline is { Directories.Count: > 0 })
        {
            var firstKey = baseline.Directories.Keys.First();
            if (firstKey.Contains('|', StringComparison.Ordinal))
            {
                _pluginLog.LogInfo(
                    "GrowthTimeline",
                    $"Discarding legacy grouped baseline ({baseline.Directories.Count} entries). A new per-directory baseline will be created.",
                    _logger);
                baseline = null;
            }
        }

        List<GrowthTimelinePoint> dataPoints;

        if (baseline == null)
        {
            // === FIRST SCAN: Create baseline and build historical timeline ===
            _pluginLog.LogInfo(
                "GrowthTimeline",
                $"First scan: creating baseline with {currentDirs.Count} directory entries.",
                _logger);

            baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now };
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
                "GrowthTimeline",
                $"Building initial timeline: {timelineEntries.Count} entries, earliest: {earliest:yyyy-MM-dd}, granularity: {granularity}",
                _logger);

            dataPoints = TimelineAggregator.BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);
        }
        else
        {
            // === SUBSEQUENT SCAN: Append-only snapshot ===
            // Historical data points are immutable. We only update the current bucket
            // with the actual current total size/count. This prevents deletions of old
            // files from retroactively altering past data points.
            cancellationToken.ThrowIfCancellationRequested();

            var existingTimeline = await LoadTimelineAsync(cancellationToken).ConfigureAwait(false);

            // Calculate current absolute totals in a single pass (avoids two iterations)
            long currentTotalSize = 0;
            var currentTotalCount = 0;
            foreach (var dir in currentDirs)
            {
                currentTotalSize += dir.Size;
                currentTotalCount += dir.Count;
            }

            if (existingTimeline is { DataPoints.Count: > 0 })
            {
                // Append-only: preserve historical points, update current bucket
                _pluginLog.LogInfo(
                    "GrowthTimeline",
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
                    "GrowthTimeline",
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
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Trim leading zero-value data points but keep one zero just before the first non-zero
        // as a visual baseline start. This avoids long flat 0-lines for historical buckets
        // before any media existed, while still showing a library rebuild (drop to 0 then rise).
        dataPoints = TimelineAggregator.TrimLeadingZeros(dataPoints);

        // Consolidate data points into the current granularity.
        // When the time span grows (e.g. from <90 days to >90 days), the granularity
        // upgrades (daily→weekly). Previously stored finer-grained points are merged
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
            _pluginLog.LogInfo("GrowthTimeline", "No timeline data points after processing.", _logger);
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
            TotalFilesScanned = currentDirs.Count,
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
            "GrowthTimeline",
            $"Growth timeline computed: {dataPoints.Count} data points ({finalGranularity})",
            _logger);
        return result;
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
                "GrowthTimeline",
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

            try
            {
                // Resolve the full trash path for this library root (handles both relative and absolute paths)
                var fullTrashPath = Path.GetFullPath(_configHelper.GetTrashPath(location))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Collect top-level subdirectories as media items
                foreach (var subDir in _fileSystem.GetDirectories(location))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dirName = Path.GetFileName(subDir.FullName);

                    // Skip .trickplay and trash directories
                    if (ShouldSkipDirectory(subDir.FullName, dirName, trashFolderName, fullTrashPath))
                    {
                        continue;
                    }

                    // Use directory creation date as "when this media was added"
                    var createdUtc = Directory.GetCreationTimeUtc(subDir.FullName);
                    if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
                    {
                        continue;
                    }

                    // Sum up all file sizes recursively within this directory
                    var totalSize = GetDirectorySize(subDir.FullName, trashFolderName, fullTrashPath, cancellationToken);
                    if (totalSize > 0)
                    {
                        entries.Add(
                            new DirectoryEntry
                            {
                                Path = subDir.FullName,
                                CreatedUtc = createdUtc,
                                Size = totalSize,
                                Count = 1
                            });
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Also collect loose files directly in the library root
                foreach (var file in _fileSystem.GetFiles(location))
                {
                    var ext = Path.GetExtension(file.FullName);
                    if (!MediaExtensions.VideoExtensions.Contains(ext) &&
                        !MediaExtensions.AudioExtensions.Contains(ext))
                    {
                        continue;
                    }

                    var createdUtc = File.GetCreationTimeUtc(file.FullName);
                    if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
                    {
                        createdUtc = File.GetLastWriteTimeUtc(file.FullName);
                    }

                    if (createdUtc == DateTime.MinValue || createdUtc.Year < 1990)
                    {
                        continue;
                    }

                    entries.Add(
                        new DirectoryEntry
                        {
                            Path = file.FullName,
                            CreatedUtc = createdUtc,
                            Size = file.Length,
                            Count = 1
                        });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _pluginLog.LogWarning("GrowthTimeline", $"Could not scan {location}", ex, _logger);
            }
        }

        return entries;
    }

    /// <summary>
    ///     Determines whether a directory should be skipped during traversal.
    ///     Matches .trickplay directories and trash directories by leaf name (relative paths)
    ///     or resolved full path (absolute paths).
    /// </summary>
    private static bool ShouldSkipDirectory(string fullName, string dirName, string trashFolderName, string fullTrashPath)
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
    ///     Calculates the total size of all files within a directory (recursively).
    /// </summary>
    private long GetDirectorySize(string directoryPath, string trashFolderName, string fullTrashPath, CancellationToken cancellationToken)
    {
        long total = 0;
        try
        {
            foreach (var file in _fileSystem.GetFiles(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
            }

            foreach (var subDir in _fileSystem.GetDirectories(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirName = Path.GetFileName(subDir.FullName);

                // Skip .trickplay and trash subdirectories
                if (ShouldSkipDirectory(subDir.FullName, dirName, trashFolderName, fullTrashPath))
                {
                    continue;
                }

                total += GetDirectorySize(subDir.FullName, trashFolderName, fullTrashPath, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogDebug(
                "GrowthTimeline",
                $"Skipping inaccessible directory: {directoryPath}: {ex.Message}",
                _logger);
        }

        return total;
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
            _pluginLog.LogWarning("GrowthTimeline", $"Could not load baseline from {_baselineFilePath}", ex, _logger);
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
            var directory = Path.GetDirectoryName(_baselineFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(baseline, JsonOptions);
            await File.WriteAllTextAsync(_baselineFilePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("GrowthTimeline", $"Could not save baseline to {_baselineFilePath}", ex, _logger);
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
            var directory = Path.GetDirectoryName(_timelineFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(result, JsonOptions);
            await File.WriteAllTextAsync(_timelineFilePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("GrowthTimeline", $"Could not save timeline to {_timelineFilePath}", ex, _logger);
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
        }
    }

    /// <summary>
    ///     Internal struct for timeline construction — a size contribution at a point in time.
    /// </summary>
    internal struct FileEntry
    {
        public DateTime CreatedUtc;
        public long Size;
        public int CountDelta;
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
        public int Count;
    }
}