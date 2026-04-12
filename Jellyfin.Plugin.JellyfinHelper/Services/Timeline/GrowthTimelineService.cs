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
    /// On the first scan, creates a baseline snapshot. On subsequent scans,
    /// uses diff-based tracking: new directories (created after the first scan)
    /// contribute their full size at their creation date, while existing directories
    /// that changed size contribute only the diff at the current scan date.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result.</returns>
    public async Task<GrowthTimelineResult> ComputeTimelineAsync(CancellationToken cancellationToken)
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

        cancellationToken.ThrowIfCancellationRequested();

        var baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
        List<FileEntry> timelineEntries;

        if (baseline == null)
        {
            // === FIRST SCAN: Create baseline and build historical timeline ===
            PluginLogService.LogInfo("GrowthTimeline", $"First scan: creating baseline with {currentDirs.Count} directories.", _logger);

            baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now };
            foreach (var dir in currentDirs)
            {
                baseline.Directories[dir.Path] = new BaselineDirectoryEntry
                {
                    CreatedUtc = dir.CreatedUtc,
                    Size = dir.Size,
                };
            }

            await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);

            // For the first scan, use creation dates with current sizes (historical reconstruction)
            timelineEntries = currentDirs.Select(d => new FileEntry
            {
                CreatedUtc = d.CreatedUtc,
                Size = d.Size,
                CountDelta = 1,
            }).ToList();
        }
        else
        {
            // === SUBSEQUENT SCAN: Diff-based tracking ===
            PluginLogService.LogInfo("GrowthTimeline", $"Incremental scan: baseline from {baseline.FirstScanTimestamp:yyyy-MM-dd}, {currentDirs.Count} current dirs, {baseline.Directories.Count} baseline dirs.", _logger);

            cancellationToken.ThrowIfCancellationRequested();

            timelineEntries = BuildIncrementalEntries(currentDirs, baseline, now);

            // Update baseline with current state for next scan
            UpdateBaseline(baseline, currentDirs);
            await SaveBaselineAsync(baseline, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (timelineEntries.Count == 0)
        {
            PluginLogService.LogInfo("GrowthTimeline", "No timeline entries after processing.", _logger);
            return new GrowthTimelineResult
            {
                ComputedAt = now,
                Granularity = "monthly",
                FirstScanTimestamp = baseline.FirstScanTimestamp,
            };
        }

        timelineEntries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        var earliest = timelineEntries[0].CreatedUtc;
        var granularity = DetermineGranularity(earliest, now);

        PluginLogService.LogInfo("GrowthTimeline", $"Building timeline: {timelineEntries.Count} entries, earliest: {earliest:yyyy-MM-dd}, granularity: {granularity}", _logger);

        var dataPoints = BuildCumulativeTimeline(timelineEntries, earliest, now, granularity);

        // Trim leading zero-value data points but keep one zero just before the first non-zero
        // as a visual baseline start. This avoids long flat 0-lines for historical buckets
        // before any media existed, while still showing a library rebuild (drop to 0 then rise).
        dataPoints = TrimLeadingZeros(dataPoints);

        var result = new GrowthTimelineResult
        {
            Granularity = granularity,
            EarliestFileDate = earliest,
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

        PluginLogService.LogInfo("GrowthTimeline", $"Growth timeline computed: {dataPoints.Count} data points ({granularity})", _logger);
        return result;
    }

    /// <summary>
    /// Loads the last computed timeline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The cached timeline or null.</returns>
    public async Task<GrowthTimelineResult?> LoadTimelineAsync(CancellationToken cancellationToken)
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
            entries.Add(new FileEntry
            {
                CreatedUtc = kvp.Value.CreatedUtc,
                Size = kvp.Value.Size,
                CountDelta = 1,
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
                // New directory (not in baseline)
                if (dir.CreatedUtc > baseline.FirstScanTimestamp)
                {
                    // Created after baseline: add full size at creation date
                    entries.Add(new FileEntry
                    {
                        CreatedUtc = dir.CreatedUtc,
                        Size = dir.Size,
                        CountDelta = 1,
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
                        CountDelta = 1,
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
                // Directory was removed: add a negative entry at scan time to reflect the deletion
                entries.Add(new FileEntry
                {
                    CreatedUtc = now,
                    Size = -kvp.Value.Size,
                    CountDelta = -1,
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
                // Update size to current value (creation date stays the same)
                existing.Size = dir.Size;
            }
            else
            {
                // New directory: add to baseline
                baseline.Directories[dir.Path] = new BaselineDirectoryEntry
                {
                    CreatedUtc = dir.CreatedUtc,
                    Size = dir.Size,
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
    /// Gets the start of the bucket containing the given date.
    /// </summary>
    private static DateTime GetBucketStart(DateTime date, string granularity)
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
    }
}
