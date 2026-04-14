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
/// Computes a cumulative growth timeline based on media file creation dates.
/// Uses a baseline snapshot from the first scan to enable accurate diff-based
/// growth tracking on subsequent scans.
/// Automatically selects the best granularity (daily/weekly/monthly/quarterly/yearly)
/// depending on the time span between the oldest file and today.
/// </summary>
public class GrowthTimelineService
{
    private const string TimelineFileName = "jellyfin-helper-growth-timeline.json";
    private const string BaselineFileName = "jellyfin-helper-growth-baseline.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly string _timelineFilePath;
    private readonly string _baselineFilePath;
    private readonly ILogger<GrowthTimelineService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrowthTimelineService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public GrowthTimelineService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        ILogger<GrowthTimelineService> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
        _timelineFilePath = Path.Combine(applicationPaths.DataPath, TimelineFileName);
        _baselineFilePath = Path.Combine(applicationPaths.DataPath, BaselineFileName);
    }

    /// <summary>
    /// Computes the growth timeline by scanning top-level media directories.
    /// On the first scan, creates a baseline snapshot and builds a historical timeline
    /// from directory creation dates. On subsequent scans, uses an append-only snapshot
    /// approach: all previously persisted data points are treated as immutable history,
    /// and only the current time-bucket is updated with the actual total size/count.
    /// This ensures that deleting files whose creation dates lie in the past does NOT
    /// retroactively alter historical data points — the deletion shows up as a drop
    /// at the current point in time.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result.</returns>
    public virtual async Task<GrowthTimelineResult> ComputeTimelineAsync(CancellationToken cancellationToken)
    {
        PluginLogService.LogInfo("GrowthTimeline", "Starting growth timeline computation...", _logger);

        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var currentDirs = CollectDirectoryEntries(cancellationToken);

        if (currentDirs.Count == 0)
        {
            PluginLogService.LogInfo("GrowthTimeline", "No media directories found for growth timeline.", _logger);
            return new GrowthTimelineResult
            {
                ComputedAt = now,
                Granularity = "monthly",
            };
        }

        PluginLogService.LogInfo("GrowthTimeline", $"Collected {currentDirs.Count} media directories.", _logger);

        cancellationToken.ThrowIfCancellationRequested();

        var baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);

        // Discard legacy baselines that used grouped keys (contain '|' separator).
        // These are incompatible with the per-directory format and would produce incorrect diffs.
        if (baseline != null && baseline.Directories.Count > 0)
        {
            var firstKey = baseline.Directories.Keys.First();
            if (firstKey.Contains('|', StringComparison.Ordinal))
            {
                PluginLogService.LogInfo("GrowthTimeline", $"Discarding legacy grouped baseline ({baseline.Directories.Count} entries). A new per-directory baseline will be created.", _logger);
                baseline = null;
            }
        }

        List<GrowthTimelinePoint> dataPoints;

        if (baseline == null)
        {
            // === FIRST SCAN: Create baseline and build historical timeline ===
            PluginLogService.LogInfo("GrowthTimeline", $"First scan: creating baseline with {currentDirs.Count} directory entries.", _logger);

            baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now };
            foreach (var dir in currentDirs)
            {
                baseline.Directories[dir.Path] = new BaselineDirectoryEntry
                {
                    CreatedUtc = dir.CreatedUtc,
                    Size = dir.Size,
                    Count = dir.Count,
                };
            }

            await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);

            // For the first scan, use creation dates with current sizes (historical reconstruction)
            var timelineEntries = currentDirs.Select(d => new FileEntry
            {
                CreatedUtc = d.CreatedUtc,
                Size = d.Size,
                CountDelta = d.Count,
            }).ToList();

            timelineEntries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

            var earliest = timelineEntries.Count > 0 ? timelineEntries[0].CreatedUtc : now;
            var granularity = DetermineGranularity(earliest, now);

            PluginLogService.LogInfo("GrowthTimeline", $"Building initial timeline: {timelineEntries.Count} entries, earliest: {earliest:yyyy-MM-dd}, granularity: {granularity}", _logger);

            dataPoints = BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);
        }
        else
        {
            // === SUBSEQUENT SCAN: Append-only snapshot ===
            // Historical data points are immutable. We only update the current bucket
            // with the actual current total size/count. This prevents deletions of old
            // files from retroactively altering past data points.
            cancellationToken.ThrowIfCancellationRequested();

            var existingTimeline = await LoadTimelineAsync(cancellationToken).ConfigureAwait(false);

            // Calculate current absolute totals
            var currentTotalSize = currentDirs.Sum(d => d.Size);
            var currentTotalCount = currentDirs.Sum(d => d.Count);

            if (existingTimeline != null && existingTimeline.DataPoints.Count > 0)
            {
                // Append-only: preserve historical points, update current bucket
                PluginLogService.LogInfo("GrowthTimeline", $"Append-only scan: {existingTimeline.DataPoints.Count} existing points, current total: {currentTotalSize} bytes, {currentTotalCount} items.", _logger);

                var earliestExisting = existingTimeline.DataPoints[0].Date;
                var granularity = DetermineGranularity(earliestExisting, now);

                dataPoints = MergeSnapshotIntoTimeline(
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
                PluginLogService.LogInfo("GrowthTimeline", $"No existing timeline found. Performing historical reconstruction from baseline.", _logger);

                var timelineEntries = BuildIncrementalEntries(currentDirs, baseline, now);
                timelineEntries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

                var earliest = timelineEntries.Count > 0 ? timelineEntries[0].CreatedUtc : now;
                var granularity = DetermineGranularity(earliest, now);

                dataPoints = BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);
            }

            // Update baseline with current state for next scan
            UpdateBaseline(baseline, currentDirs);
            await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Trim leading zero-value data points but keep one zero just before the first non-zero
        // as a visual baseline start. This avoids long flat 0-lines for historical buckets
        // before any media existed, while still showing a library rebuild (drop to 0 then rise).
        dataPoints = TrimLeadingZeros(dataPoints);

        // Consolidate data points into the current granularity.
        // When the time span grows (e.g. from <90 days to >90 days), the granularity
        // upgrades (daily→weekly). Previously stored finer-grained points are merged
        // into the coarser buckets so the persisted file stays compact.
        var finalGranularity = dataPoints.Count > 0
            ? DetermineGranularity(dataPoints[0].Date, now)
            : "monthly";
        dataPoints = ConsolidateToGranularity(dataPoints, finalGranularity);

        // Remove consecutive data points with identical values to reduce storage size.
        // The UI will interpolate missing buckets back when rendering the chart.
        dataPoints = DeduplicateConsecutivePoints(dataPoints);

        if (dataPoints.Count == 0)
        {
            PluginLogService.LogInfo("GrowthTimeline", "No timeline data points after processing.", _logger);
            return new GrowthTimelineResult
            {
                ComputedAt = now,
                Granularity = "monthly",
                FirstScanTimestamp = baseline.FirstScanTimestamp,
            };
        }

        var result = new GrowthTimelineResult
        {
            Granularity = finalGranularity,
            EarliestFileDate = dataPoints[0].Date,
            ComputedAt = now,
            TotalFilesScanned = currentDirs.Count,
            FirstScanTimestamp = baseline.FirstScanTimestamp,
        };

        foreach (var point in dataPoints)
        {
            result.DataPoints.Add(point);
        }

        // Persist to disk
        cancellationToken.ThrowIfCancellationRequested();
        await SaveTimelineAsync(result, cancellationToken).ConfigureAwait(false);

        PluginLogService.LogInfo("GrowthTimeline", $"Growth timeline computed: {dataPoints.Count} data points ({finalGranularity})", _logger);
        return result;
    }

    /// <summary>
    /// Loads the last computed timeline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The cached timeline or null.</returns>
    public virtual async Task<GrowthTimelineResult?> LoadTimelineAsync(CancellationToken cancellationToken)
    {
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
            PluginLogService.LogWarning("GrowthTimeline", $"Could not load cached timeline from {_timelineFilePath}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    /// Determines the best granularity based on the time span between the oldest file and now.
    /// </summary>
    /// <param name="earliest">The earliest file date.</param>
    /// <param name="now">The current date.</param>
    /// <returns>The granularity string (daily, weekly, monthly, quarterly, yearly).</returns>
    internal static string DetermineGranularity(DateTime earliest, DateTime now)
    {
        var span = now - earliest;
        var totalDays = span.TotalDays;

        if (totalDays > 5 * 365)
        {
            return "yearly";
        }

        if (totalDays > 2 * 365)
        {
            return "quarterly";
        }

        if (totalDays > 365)
        {
            return "monthly";
        }

        if (totalDays > 90)
        {
            return "weekly";
        }

        return "daily";
    }

    /// <summary>
    /// Generates bucket start dates from earliest to now based on granularity.
    /// </summary>
    /// <param name="earliest">The earliest date to start from.</param>
    /// <param name="now">The current date (upper bound).</param>
    /// <param name="granularity">The granularity (daily, weekly, monthly, quarterly, yearly).</param>
    /// <returns>A list of bucket start dates.</returns>
    internal static List<DateTime> GenerateBucketStarts(DateTime earliest, DateTime now, string granularity)
    {
        var buckets = new List<DateTime>();
        var current = GetBucketStart(earliest, granularity);

        while (current <= now)
        {
            buckets.Add(current);
            current = AdvanceBucket(current, granularity);
        }

        return buckets;
    }

    /// <summary>
    /// Builds incremental timeline entries by comparing the current directory state
    /// against the baseline. Baseline directories contribute their full size at their
    /// creation date. New directories (created after firstScanTimestamp) also contribute
    /// their full size at their creation date. Size changes in baseline directories
    /// contribute only the positive diff at the scan timestamp.
    /// </summary>
    /// <param name="currentDirs">The currently scanned directories.</param>
    /// <param name="baseline">The baseline from the first scan.</param>
    /// <param name="now">The current scan timestamp.</param>
    /// <returns>A list of file entries for timeline construction.</returns>
    internal static List<FileEntry> BuildIncrementalEntries(
        List<DirectoryEntry> currentDirs,
        GrowthTimelineBaseline baseline,
        DateTime now)
    {
        var entries = new List<FileEntry>();

        // 1. Add all baseline entries at their original creation date with their original size
        foreach (var kvp in baseline.Directories)
        {
            // Use Count from the baseline entry; treat 0 as 1 for backwards compatibility
            var count = kvp.Value.Count > 0 ? kvp.Value.Count : 1;
            entries.Add(new FileEntry
            {
                CreatedUtc = kvp.Value.CreatedUtc,
                Size = kvp.Value.Size,
                CountDelta = count,
            });
        }

        // 2. Process current directories
        foreach (var dir in currentDirs)
        {
            if (baseline.Directories.TryGetValue(dir.Path, out var baselineEntry))
            {
                // Existing directory: check for size change
                var sizeDiff = dir.Size - baselineEntry.Size;
                if (sizeDiff > 0)
                {
                    // Directory grew: add the positive diff at the current scan time
                    // CountDelta = 0 because this is a size adjustment, not a new directory
                    entries.Add(new FileEntry
                    {
                        CreatedUtc = now,
                        Size = sizeDiff,
                        CountDelta = 0,
                    });
                }
                else if (sizeDiff < 0)
                {
                    // Directory shrank: add a negative entry at the current scan time
                    // CountDelta = 0 because this is a size adjustment, not a removal
                    entries.Add(new FileEntry
                    {
                        CreatedUtc = now,
                        Size = sizeDiff,
                        CountDelta = 0,
                    });
                }

                // No change: no entry needed (baseline already covers it)
            }
            else
            {
                // New group (not in baseline)
                var count = dir.Count > 0 ? dir.Count : 1;
                if (dir.CreatedUtc > baseline.FirstScanTimestamp)
                {
                    // Created after baseline: add full size at creation date
                    entries.Add(new FileEntry
                    {
                        CreatedUtc = dir.CreatedUtc,
                        Size = dir.Size,
                        CountDelta = count,
                    });
                }
                else
                {
                    // Created before baseline but wasn't in the baseline
                    // (e.g. a new library location was added). Treat as baseline-era entry.
                    entries.Add(new FileEntry
                    {
                        CreatedUtc = dir.CreatedUtc,
                        Size = dir.Size,
                        CountDelta = count,
                    });
                }
            }
        }

        // 3. Handle deleted directories (in baseline but not in current scan)
        var currentPaths = new HashSet<string>(currentDirs.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in baseline.Directories)
        {
            if (!currentPaths.Contains(kvp.Key))
            {
                // Group was removed: add a negative entry at scan time to reflect the deletion
                var removedCount = kvp.Value.Count > 0 ? kvp.Value.Count : 1;
                entries.Add(new FileEntry
                {
                    CreatedUtc = now,
                    Size = -kvp.Value.Size,
                    CountDelta = -removedCount,
                });
            }
        }

        return entries;
    }

    /// <summary>
    /// Updates the baseline with the current directory state for subsequent scans.
    /// New directories are added to the baseline. Existing entries are updated with
    /// the current size. Removed directories are kept in the baseline (their deletion
    /// is tracked via negative diff entries).
    /// </summary>
    /// <param name="baseline">The baseline to update.</param>
    /// <param name="currentDirs">The current directory entries.</param>
    internal static void UpdateBaseline(GrowthTimelineBaseline baseline, List<DirectoryEntry> currentDirs)
    {
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in currentDirs)
        {
            currentPaths.Add(dir.Path);

            if (baseline.Directories.TryGetValue(dir.Path, out var existing))
            {
                // Update size and count to current values (creation date stays the same)
                existing.Size = dir.Size;
                existing.Count = dir.Count;
            }
            else
            {
                // New group: add to baseline
                baseline.Directories[dir.Path] = new BaselineDirectoryEntry
                {
                    CreatedUtc = dir.CreatedUtc,
                    Size = dir.Size,
                    Count = dir.Count,
                };
            }
        }

        // Remove deleted directories from baseline so they don't generate
        // spurious negative diffs on future scans
        var toRemove = baseline.Directories.Keys.Where(k => !currentPaths.Contains(k)).ToList();
        foreach (var key in toRemove)
        {
            baseline.Directories.Remove(key);
        }
    }

    /// <summary>
    /// Collects top-level media directory entries (path, creation date, total size) from all libraries.
    /// Each top-level subdirectory in a library (e.g. a movie folder or TV show folder)
    /// becomes one entry using its directory creation date and the total size of all files within.
    /// Files directly in a library root are also collected as individual entries.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private List<DirectoryEntry> CollectDirectoryEntries(CancellationToken cancellationToken)
    {
        var entries = new List<DirectoryEntry>();
        var locations = LibraryPathResolver.GetDistinctLibraryLocations(_libraryManager);
        var config = CleanupConfigHelper.GetConfig();
        var trashFolderName = config.TrashFolderPath;

        foreach (var location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Collect top-level subdirectories as media items
                foreach (var subDir in _fileSystem.GetDirectories(location))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dirName = Path.GetFileName(subDir.FullName);

                    // Skip trickplay and trash directories
                    if (string.Equals(dirName, "trickplay", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(trashFolderName) &&
                        string.Equals(dirName, trashFolderName, StringComparison.OrdinalIgnoreCase))
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
                    var totalSize = GetDirectorySize(subDir.FullName, trashFolderName, cancellationToken);
                    if (totalSize > 0)
                    {
                        entries.Add(new DirectoryEntry
                        {
                            Path = subDir.FullName,
                            CreatedUtc = createdUtc,
                            Size = totalSize,
                            Count = 1,
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

                    entries.Add(new DirectoryEntry
                    {
                        Path = file.FullName,
                        CreatedUtc = createdUtc,
                        Size = file.Length,
                        Count = 1,
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("GrowthTimeline", $"Could not scan {location}", ex, _logger);
            }
        }

        return entries;
    }

    /// <summary>
    /// Calculates the total size of all files within a directory (recursively).
    /// </summary>
    private long GetDirectorySize(string directoryPath, string? trashFolderName, CancellationToken cancellationToken)
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

                // Skip trickplay and trash subdirectories
                if (string.Equals(dirName, "trickplay", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(trashFolderName) &&
                    string.Equals(dirName, trashFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total += GetDirectorySize(subDir.FullName, trashFolderName, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLogService.LogDebug("GrowthTimeline", $"Skipping inaccessible directory: {directoryPath}: {ex.Message}", _logger);
        }

        return total;
    }

    /// <summary>
    /// Builds cumulative data points from sorted file entries using the specified granularity.
    /// </summary>
    /// <param name="sortedEntries">The file entries sorted by creation date.</param>
    /// <param name="earliest">The earliest date to start from.</param>
    /// <param name="now">The current date (upper bound).</param>
    /// <param name="granularity">The granularity (daily, weekly, monthly, quarterly, yearly).</param>
    /// <returns>A list of cumulative growth timeline data points.</returns>
    internal static List<GrowthTimelinePoint> BuildCumulativeTimeline(
        List<FileEntry> sortedEntries,
        DateTime earliest,
        DateTime now,
        string granularity)
    {
        var points = new List<GrowthTimelinePoint>();

        // Generate all bucket start dates
        var bucketStarts = GenerateBucketStarts(earliest, now, granularity);
        if (bucketStarts.Count == 0)
        {
            return points;
        }

        int fileIndex = 0;
        long cumulativeSize = 0;
        int cumulativeCount = 0;

        for (int b = 0; b < bucketStarts.Count; b++)
        {
            var bucketStart = bucketStarts[b];
            var bucketEnd = b + 1 < bucketStarts.Count ? bucketStarts[b + 1] : now.AddDays(1);

            // Accumulate all files whose creation date falls before the bucket end
            while (fileIndex < sortedEntries.Count && sortedEntries[fileIndex].CreatedUtc < bucketEnd)
            {
                cumulativeSize += sortedEntries[fileIndex].Size;
                cumulativeCount += sortedEntries[fileIndex].CountDelta;
                fileIndex++;
            }

            points.Add(new GrowthTimelinePoint
            {
                Date = bucketStart,
                CumulativeSize = cumulativeSize,
                CumulativeFileCount = cumulativeCount,
            });
        }

        return points;
    }

    /// <summary>
    /// Merges a current snapshot into an existing timeline using append-only semantics.
    /// All existing data points whose bucket date is strictly before the current bucket
    /// are preserved as immutable history. The current bucket is replaced (or added)
    /// with the actual current total size and count. This ensures that deletions of
    /// files with past creation dates do not retroactively alter historical data points;
    /// instead, the deletion manifests as a size drop at the current point in time.
    /// </summary>
    /// <param name="existingPoints">The previously persisted data points (chronologically sorted).</param>
    /// <param name="now">The current scan timestamp.</param>
    /// <param name="currentTotalSize">The absolute total size of all current media directories.</param>
    /// <param name="currentTotalCount">The absolute total count of all current media items.</param>
    /// <param name="granularity">The granularity for bucket calculation.</param>
    /// <returns>A merged list of data points with the current snapshot appended/updated.</returns>
    internal static List<GrowthTimelinePoint> MergeSnapshotIntoTimeline(
        List<GrowthTimelinePoint> existingPoints,
        DateTime now,
        long currentTotalSize,
        int currentTotalCount,
        string granularity)
    {
        var currentBucketStart = GetBucketStart(now, granularity);

        // Keep all points strictly before the current bucket (immutable history)
        var result = existingPoints
            .Where(point => point.Date < currentBucketStart)
            .ToList();

        // Add the current snapshot as the latest data point
        result.Add(new GrowthTimelinePoint
        {
            Date = currentBucketStart,
            CumulativeSize = currentTotalSize,
            CumulativeFileCount = currentTotalCount,
        });

        return result;
    }

    /// <summary>
    /// Gets the start of the bucket containing the given date.
    /// </summary>
    /// <param name="date">The date to find the bucket start for.</param>
    /// <param name="granularity">The granularity (daily, weekly, monthly, quarterly, yearly).</param>
    /// <returns>The start date of the bucket containing the given date.</returns>
    internal static DateTime GetBucketStart(DateTime date, string granularity)
    {
        return granularity switch
        {
            "daily" => new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc),
            "weekly" => GetStartOfWeek(date),
            "monthly" => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            "quarterly" => new DateTime(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "yearly" => new DateTime(date.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    /// <summary>
    /// Advances a bucket start to the next bucket.
    /// </summary>
    private static DateTime AdvanceBucket(DateTime current, string granularity)
    {
        return granularity switch
        {
            "daily" => current.AddDays(1),
            "weekly" => current.AddDays(7),
            "monthly" => current.AddMonths(1),
            "quarterly" => current.AddMonths(3),
            "yearly" => current.AddYears(1),
            _ => current.AddMonths(1),
        };
    }

    /// <summary>
    /// Gets the start of the ISO week (Monday) for a given date.
    /// </summary>
    private static DateTime GetStartOfWeek(DateTime date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
    }

    /// <summary>
    /// Loads the baseline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The baseline or null if not found.</returns>
    internal async Task<GrowthTimelineBaseline?> LoadBaselineAsync(CancellationToken cancellationToken)
    {
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
            PluginLogService.LogWarning("GrowthTimeline", $"Could not load baseline from {_baselineFilePath}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    /// Persists the baseline to disk.
    /// </summary>
    /// <param name="baseline">The baseline to save.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task SaveBaselineAsync(GrowthTimelineBaseline baseline, CancellationToken cancellationToken)
    {
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
            PluginLogService.LogWarning("GrowthTimeline", $"Could not save baseline to {_baselineFilePath}", ex, _logger);
        }
    }

    /// <summary>
    /// Persists the timeline result to disk.
    /// </summary>
    /// <param name="result">The timeline result to save.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task SaveTimelineAsync(GrowthTimelineResult result, CancellationToken cancellationToken)
    {
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
            PluginLogService.LogWarning("GrowthTimeline", $"Could not save timeline to {_timelineFilePath}", ex, _logger);
        }
    }

    /// <summary>
    /// Removes leading zero-value data points from the timeline, keeping at most one
    /// zero point immediately before the first non-zero point as a visual baseline start.
    /// Only trims from the beginning — zero points in the middle or end of the timeline
    /// (e.g. after a library deletion) are preserved to show the full history.
    /// </summary>
    /// <param name="points">The data points to trim.</param>
    /// <returns>A trimmed list with leading zeros removed.</returns>
    internal static List<GrowthTimelinePoint> TrimLeadingZeros(List<GrowthTimelinePoint> points)
    {
        if (points.Count == 0)
        {
            return points;
        }

        // Find the index of the first non-zero data point
        int firstNonZero = -1;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].CumulativeSize > 0)
            {
                firstNonZero = i;
                break;
            }
        }

        // All points are zero — return empty (nothing meaningful to show)
        if (firstNonZero < 0)
        {
            return new List<GrowthTimelinePoint>();
        }

        // No leading zeros — return as-is
        if (firstNonZero == 0)
        {
            return points;
        }

        // Keep one zero point before the first non-zero as the visual "start from zero" baseline
        int startIndex = firstNonZero - 1;
        return points.GetRange(startIndex, points.Count - startIndex);
    }

    /// <summary>
    /// Consolidates data points from a finer granularity into a coarser one.
    /// When the time span grows and the granularity upgrades (e.g. daily→weekly),
    /// multiple finer-grained points that fall into the same coarser bucket are merged
    /// by keeping the last (most recent) point per bucket. This keeps the persisted
    /// timeline compact and ensures backup/restore works with consolidated data.
    /// </summary>
    /// <param name="points">The data points (sorted chronologically).</param>
    /// <param name="targetGranularity">The target granularity to consolidate into.</param>
    /// <returns>A consolidated list with at most one point per target bucket.</returns>
    internal static List<GrowthTimelinePoint> ConsolidateToGranularity(
        List<GrowthTimelinePoint> points,
        string targetGranularity)
    {
        if (points.Count <= 1)
        {
            return points;
        }

        // Group points by their target bucket start date.
        // For each bucket, keep the last point (highest cumulative values).
        var buckets = new Dictionary<DateTime, GrowthTimelinePoint>();

        foreach (var point in points)
        {
            var bucketStart = GetBucketStart(point.Date, targetGranularity);
            // Overwrite: the last point per bucket wins (points are sorted chronologically)
            buckets[bucketStart] = new GrowthTimelinePoint
            {
                Date = bucketStart,
                CumulativeSize = point.CumulativeSize,
                CumulativeFileCount = point.CumulativeFileCount,
            };
        }

        var result = buckets.Values.OrderBy(p => p.Date).ToList();
        return result;
    }

    /// <summary>
    /// Removes consecutive data points that have identical CumulativeSize and CumulativeFileCount.
    /// Only the first point of each "plateau" is kept, plus the last point is always preserved
    /// so the timeline's time span remains correct. The UI is responsible for interpolating
    /// the missing intermediate buckets when rendering the chart.
    /// </summary>
    /// <param name="points">The data points (already sorted chronologically).</param>
    /// <returns>A deduplicated list with redundant consecutive points removed.</returns>
    internal static List<GrowthTimelinePoint> DeduplicateConsecutivePoints(List<GrowthTimelinePoint> points)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        var result = new List<GrowthTimelinePoint> { points[0] };

        for (int i = 1; i < points.Count - 1; i++)
        {
            var prev = points[i - 1];
            var curr = points[i];

            // Keep the point if it differs from its predecessor
            if (curr.CumulativeSize != prev.CumulativeSize ||
                curr.CumulativeFileCount != prev.CumulativeFileCount)
            {
                result.Add(curr);
            }
        }

        // Always keep the last point to preserve the timeline's end date
        result.Add(points[points.Count - 1]);

        return result;
    }

    /// <summary>
    /// Internal struct for timeline construction — a size contribution at a point in time.
    /// </summary>
    internal struct FileEntry
    {
        public DateTime CreatedUtc;
        public long Size;
        public int CountDelta;
    }

    /// <summary>
    /// Internal struct for collecting directory metadata during scanning.
    /// Includes the path for baseline comparison.
    /// </summary>
    internal struct DirectoryEntry
    {
        public string Path;
        public DateTime CreatedUtc;
        public long Size;
        public int Count;
    }
}
