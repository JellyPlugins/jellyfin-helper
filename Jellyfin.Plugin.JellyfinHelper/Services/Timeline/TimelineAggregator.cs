using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     Provides pure, stateless aggregation logic for growth timeline data.
///     All methods are static and operate on data passed in — no I/O, no dependencies.
///     Used by <see cref="GrowthTimelineService" /> for timeline computation.
/// </summary>
public static class TimelineAggregator
{
    /// <summary>
    ///     Determines the best granularity based on the time span between the oldest file and now.
    /// </summary>
    /// <param name="earliest">The earliest file date.</param>
    /// <param name="now">The current date.</param>
    /// <returns>The granularity string (daily, weekly, monthly, quarterly, yearly).</returns>
    internal static string DetermineGranularity(DateTime earliest, DateTime now)
    {
        var span = now - earliest;
        var totalDays = span.TotalDays;

        return totalDays switch
        {
            > 5 * 365 => "yearly",
            > 2 * 365 => "quarterly",
            > 365 => "monthly",
            > 90 => "weekly",
            _ => "daily"
        };
    }

    /// <summary>
    ///     Generates bucket start dates from earliest to now based on granularity.
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
    ///     Builds incremental timeline entries by comparing the current directory state
    ///     against the baseline. Baseline directories contribute their full size at their
    ///     creation date. New directories (created after firstScanTimestamp) also contribute
    ///     their full size at their creation date. Size changes in baseline directories
    ///     contribute only the positive diff at the scan timestamp.
    /// </summary>
    /// <param name="currentDirs">The currently scanned directories.</param>
    /// <param name="baseline">The baseline from the first scan.</param>
    /// <param name="now">The current scan timestamp.</param>
    /// <returns>A list of file entries for timeline construction.</returns>
    internal static List<GrowthTimelineService.FileEntry> BuildIncrementalEntries(
        List<GrowthTimelineService.DirectoryEntry> currentDirs,
        GrowthTimelineBaseline baseline,
        DateTime now)
    {
        var entries = (from kvp in baseline.Directories
                let count = kvp.Value.Count > 0 ? kvp.Value.Count : 1
                select new GrowthTimelineService.FileEntry { CreatedUtc = kvp.Value.CreatedUtc, Size = kvp.Value.Size, CountDelta = count })
            .ToList();

        // 1. Add all baseline entries at their original creation date with their original size

        // 2. Process current directories
        foreach (var dir in currentDirs)
        {
            if (baseline.Directories.TryGetValue(dir.Path, out var baselineEntry))
            {
                // Existing directory: check for size or count change
                var sizeDiff = dir.Size - baselineEntry.Size;
                var baselineCount = baselineEntry.Count > 0 ? baselineEntry.Count : 1;
                var currentCount = dir.Count > 0 ? dir.Count : 1;
                var countDiff = currentCount - baselineCount;

                if (sizeDiff != 0 || countDiff != 0)
                {
                    // Directory changed: emit a delta entry at the current scan time
                    entries.Add(
                        new GrowthTimelineService.FileEntry
                        {
                            CreatedUtc = now,
                            Size = sizeDiff,
                            CountDelta = countDiff
                        });
                }

                // No change in size or count: no entry needed (baseline already covers it)
            }
            else
            {
                // New group (not in baseline)
                var count = dir.Count > 0 ? dir.Count : 1;
                if (dir.CreatedUtc > baseline.FirstScanTimestamp)
                {
                    // Created after baseline: add full size at creation date
                }

                // Created before baseline but wasn't in the baseline
                // (e.g. a new library location was added). Treat as baseline-era entry.
                entries.Add(
                    new GrowthTimelineService.FileEntry
                    {
                        CreatedUtc = dir.CreatedUtc,
                        Size = dir.Size,
                        CountDelta = count
                    });
            }
        }

        // 3. Handle deleted directories (in baseline but not in current scan)
        var currentPaths = new HashSet<string>(currentDirs.Select(d => d.Path), baseline.Directories.Comparer);
        entries.AddRange(
            from kvp in baseline.Directories
            where !currentPaths.Contains(kvp.Key)
            let removedCount = kvp.Value.Count > 0 ? kvp.Value.Count : 1
            select new GrowthTimelineService.FileEntry { CreatedUtc = now, Size = -kvp.Value.Size, CountDelta = -removedCount });

        return entries;
    }

    /// <summary>
    ///     Updates the baseline with the current directory state for subsequent scans.
    ///     New directories are added to the baseline. Existing entries are updated with
    ///     the current size. Removed directories are deleted from the baseline so they
    ///     do not generate spurious negative diffs on future scans.
    /// </summary>
    /// <param name="baseline">The baseline to update.</param>
    /// <param name="currentDirs">The current directory entries.</param>
    internal static void UpdateBaseline(GrowthTimelineBaseline baseline, List<GrowthTimelineService.DirectoryEntry> currentDirs)
    {
        var currentPaths = new HashSet<string>(baseline.Directories.Comparer);

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
                    Count = dir.Count
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
    ///     Builds cumulative data points from sorted file entries using the specified granularity.
    /// </summary>
    /// <param name="sortedEntries">The file entries sorted by creation date.</param>
    /// <param name="earliest">The earliest date to start from.</param>
    /// <param name="now">The current date (upper bound).</param>
    /// <param name="granularity">The granularity (daily, weekly, monthly, quarterly, yearly).</param>
    /// <returns>A list of cumulative growth timeline data points.</returns>
    internal static List<GrowthTimelinePoint> BuildCumulativeTimeline(
        List<GrowthTimelineService.FileEntry> sortedEntries,
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

        var fileIndex = 0;
        long cumulativeSize = 0;
        var cumulativeCount = 0;

        for (var b = 0; b < bucketStarts.Count; b++)
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

            points.Add(
                new GrowthTimelinePoint
                {
                    Date = bucketStart,
                    CumulativeSize = cumulativeSize,
                    CumulativeFileCount = cumulativeCount
                });
        }

        return points;
    }

    /// <summary>
    ///     Merges a current snapshot into an existing timeline using append-only semantics.
    ///     All existing data points whose bucket date is strictly before the current bucket
    ///     are preserved as immutable history. The current bucket is replaced (or added)
    ///     with the actual current total size and count.
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
        result.Add(
            new GrowthTimelinePoint
            {
                Date = currentBucketStart,
                CumulativeSize = currentTotalSize,
                CumulativeFileCount = currentTotalCount
            });

        return result;
    }

    /// <summary>
    ///     Gets the start of the bucket containing the given date.
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
            _ => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    /// <summary>
    ///     Advances a bucket start to the next bucket.
    /// </summary>
    /// <param name="current">The current bucket start date.</param>
    /// <param name="granularity">The granularity (daily, weekly, monthly, quarterly, yearly).</param>
    /// <returns>The start date of the next bucket.</returns>
    private static DateTime AdvanceBucket(DateTime current, string granularity)
    {
        return granularity switch
        {
            "daily" => current.AddDays(1),
            "weekly" => current.AddDays(7),
            "monthly" => current.AddMonths(1),
            "quarterly" => current.AddMonths(3),
            "yearly" => current.AddYears(1),
            _ => current.AddMonths(1)
        };
    }

    /// <summary>
    ///     Gets the start of the ISO week (Monday) for a given date.
    /// </summary>
    /// <param name="date">The date to find the start of the week for.</param>
    /// <returns>The Monday of the week containing the given date.</returns>
    private static DateTime GetStartOfWeek(DateTime date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
    }

    /// <summary>
    ///     Removes leading zero-value data points from the timeline, keeping at most one
    ///     zero point immediately before the first non-zero point as a visual baseline start.
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
        var firstNonZero = -1;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].CumulativeSize <= 0 && points[i].CumulativeFileCount <= 0)
            {
                continue;
            }

            firstNonZero = i;
            break;
        }

        switch (firstNonZero)
        {
            // All points are zero — return empty (nothing meaningful to show)
            case < 0:
                return [];
            // No leading zeros — return as-is
            case 0:
                return points;
            default:
            {
                // Keep one zero point before the first non-zero as the visual "start from zero" baseline
                var startIndex = firstNonZero - 1;
                return points.GetRange(startIndex, points.Count - startIndex);
            }
        }
    }

    /// <summary>
    ///     Consolidates data points from a finer granularity into a coarser one.
    ///     When the time span grows and the granularity upgrades (e.g. daily→weekly),
    ///     multiple finer-grained points that fall into the same coarser bucket are merged
    ///     by keeping the last (most recent) point per bucket.
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
                CumulativeFileCount = point.CumulativeFileCount
            };
        }

        var result = buckets.Values.OrderBy(p => p.Date).ToList();
        return result;
    }

    /// <summary>
    ///     Removes consecutive data points that have identical CumulativeSize and CumulativeFileCount.
    ///     Only the first point of each "plateau" is kept, plus the last point is always preserved
    ///     so the timeline's time span remains correct.
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

        for (var i = 1; i < points.Count - 1; i++)
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
        result.Add(points[^1]);

        return result;
    }
}