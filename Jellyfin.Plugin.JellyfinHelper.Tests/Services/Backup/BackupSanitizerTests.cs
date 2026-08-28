using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Tests for <see cref="BackupSanitizer" /> targeting the timeline-trimming path.
/// </summary>
public class BackupSanitizerTests
{
    private static BackupData MakeTimelineBackup(int pointCount)
    {
        var data = new BackupData();
        var timeline = new GrowthTimelineResult();
        for (var i = 0; i < pointCount; i++)
        {
            timeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = DateTime.UtcNow.AddDays(-pointCount + i),
                CumulativeSize = (i + 1) * 1024L,
                CumulativeFileCount = i + 1
            });
        }

        data.GrowthTimeline = timeline;
        return data;
    }

    [Fact]
    public void Sanitize_TimelineUnderLimit_NoPointsRemoved()
    {
        var data = MakeTimelineBackup(BackupValidator.MaxTimelineDataPoints);
        BackupSanitizer.Sanitize(data);
        Assert.Equal(BackupValidator.MaxTimelineDataPoints, data.GrowthTimeline!.DataPoints.Count);
    }

    [Fact]
    public void Sanitize_TimelineOverLimit_TrimsToMax()
    {
        var excess = 50;
        var data = MakeTimelineBackup(BackupValidator.MaxTimelineDataPoints + excess);
        BackupSanitizer.Sanitize(data);
        Assert.Equal(BackupValidator.MaxTimelineDataPoints, data.GrowthTimeline!.DataPoints.Count);
    }

    [Fact]
    public void Sanitize_TimelineOverLimit_KeepsNewestPoints()
    {
        // Verify that after trimming, the retained points are the MaxTimelineDataPoints newest.
        var fullData = MakeTimelineBackup(BackupValidator.MaxTimelineDataPoints + 5);
        var before = fullData.GrowthTimeline!.DataPoints
            .OrderByDescending(p => p.Date)
            .Take(BackupValidator.MaxTimelineDataPoints)
            .Select(p => p.Date)
            .ToHashSet();

        BackupSanitizer.Sanitize(fullData);

        var after = fullData.GrowthTimeline!.DataPoints.Select(p => p.Date).ToHashSet();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Sanitize_TimelineOverLimit_ResultIsSortedAscending()
    {
        var data = MakeTimelineBackup(BackupValidator.MaxTimelineDataPoints + 10);
        BackupSanitizer.Sanitize(data);
        var points = data.GrowthTimeline!.DataPoints;
        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].Date >= points[i - 1].Date,
                $"Point {i} date {points[i].Date:O} is before {points[i - 1].Date:O}");
        }
    }

    [Fact]
    public void Sanitize_SeerrCleanupAgeDays_Zero_PreservesZero()
    {
        // 0 is the "immediate cleanup" sentinel - must not be clamped to 1.
        var data = new BackupData { SeerrCleanupAgeDays = 0 };
        BackupSanitizer.Sanitize(data);
        Assert.Equal(0, data.SeerrCleanupAgeDays);
    }

    [Fact]
    public void Sanitize_SeerrCleanupAgeDays_Negative_ClampsToZero()
    {
        var data = new BackupData { SeerrCleanupAgeDays = -5 };
        BackupSanitizer.Sanitize(data);
        Assert.Equal(0, data.SeerrCleanupAgeDays);
    }

    [Fact]
    public void Sanitize_SeerrCleanupAgeDays_AboveMax_ClampsToMax()
    {
        var data = new BackupData { SeerrCleanupAgeDays = BackupValidator.MaxRetentionDays + 1 };
        BackupSanitizer.Sanitize(data);
        Assert.Equal(BackupValidator.MaxRetentionDays, data.SeerrCleanupAgeDays);
    }

    [Fact]
    public void Sanitize_SeerrCleanupAgeDays_Null_LeftNull()
    {
        var data = new BackupData { SeerrCleanupAgeDays = null };
        BackupSanitizer.Sanitize(data);
        Assert.Null(data.SeerrCleanupAgeDays);
    }

    private static BackupData MakeBaselineBackup(int directoryCount)
    {
        var data = new BackupData();
        var baseline = new GrowthTimelineBaseline();
        for (var i = 0; i < directoryCount; i++)
        {
            baseline.Directories[$"/media/dir{i:D3}"] = new BaselineDirectoryEntry
            {
                CreatedUtc = DateTime.UtcNow.AddDays(-directoryCount + i),
                Size = (i + 1) * 1024L,
                Count = 1
            };
        }

        data.GrowthBaseline = baseline;
        return data;
    }

    [Fact]
    public void Sanitize_BaselineUnderLimit_NoDirsRemoved()
    {
        var data = MakeBaselineBackup(BackupValidator.MaxBaselineDirectories);
        BackupSanitizer.Sanitize(data);
        Assert.Equal(BackupValidator.MaxBaselineDirectories, data.GrowthBaseline!.Directories.Count);
    }

    [Fact]
    public void Sanitize_BaselineOverLimit_TrimsToMax()
    {
        var data = MakeBaselineBackup(BackupValidator.MaxBaselineDirectories + 10);
        BackupSanitizer.Sanitize(data);
        Assert.Equal(BackupValidator.MaxBaselineDirectories, data.GrowthBaseline!.Directories.Count);
    }

    [Fact]
    public void Sanitize_BaselineOverLimit_KeepsNewestEntries()
    {
        var data = MakeBaselineBackup(BackupValidator.MaxBaselineDirectories + 5);
        var allEntries = data.GrowthBaseline!.Directories
            .OrderByDescending(kvp => kvp.Value.CreatedUtc)
            .Take(BackupValidator.MaxBaselineDirectories)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        BackupSanitizer.Sanitize(data);

        var remaining = data.GrowthBaseline!.Directories.Keys.ToHashSet();
        Assert.Equal(allEntries, remaining);
    }

    [Fact]
    public void Sanitize_BaselineOverLimit_RemovesOldestEntries()
    {
        var data = MakeBaselineBackup(BackupValidator.MaxBaselineDirectories + 3);
        var oldestKey = data.GrowthBaseline!.Directories
            .OrderBy(kvp => kvp.Value.CreatedUtc)
            .First().Key;

        BackupSanitizer.Sanitize(data);

        Assert.DoesNotContain(oldestKey, data.GrowthBaseline!.Directories.Keys);
    }

    [Fact]
    public void Sanitize_TimelineNegativeCumulativeValues_ClampedToZero()
    {
        // A cumulative byte size / file count is physically non-negative. A hostile or corrupt backup must not be able to plant a negative that is written verbatim to the cache and surfaces on GET GrowthTimeline (and survives a recompute).
        var data = new BackupData
        {
            GrowthTimeline = new GrowthTimelineResult()
        };
        data.GrowthTimeline.DataPoints.Add(new GrowthTimelinePoint
        {
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CumulativeSize = -5000,
            CumulativeFileCount = -3
        });

        BackupSanitizer.Sanitize(data);

        var point = data.GrowthTimeline.DataPoints[0];
        Assert.Equal(0, point.CumulativeSize);
        Assert.Equal(0, point.CumulativeFileCount);
    }

    [Fact]
    public void Sanitize_TimelinePositiveCumulativeValues_LeftUnchanged()
    {
        var data = MakeTimelineBackup(3);
        var expected = data.GrowthTimeline!.DataPoints
            .Select(p => (p.CumulativeSize, p.CumulativeFileCount))
            .ToList();

        BackupSanitizer.Sanitize(data);

        var actual = data.GrowthTimeline!.DataPoints
            .Select(p => (p.CumulativeSize, p.CumulativeFileCount))
            .ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sanitize_BaselineNegativeSizeAndCount_ClampedToZero()
    {
        var data = new BackupData
        {
            GrowthBaseline = new GrowthTimelineBaseline()
        };
        data.GrowthBaseline.Directories["/media/corrupt"] = new BaselineDirectoryEntry
        {
            CreatedUtc = DateTime.UtcNow,
            Size = -1234,
            Count = -7
        };

        BackupSanitizer.Sanitize(data);

        var entry = data.GrowthBaseline.Directories["/media/corrupt"];
        Assert.Equal(0, entry.Size);
        Assert.Equal(0, entry.Count);
    }

    [Fact]
    public void TruncateString_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BackupSanitizer.TruncateString(null, 8));
        Assert.Equal(string.Empty, BackupSanitizer.TruncateString(string.Empty, 8));
    }

    [Fact]
    public void TruncateString_WithinLimit_ReturnsUnchanged()
    {
        Assert.Equal("hello", BackupSanitizer.TruncateString("hello", 8));
    }

    [Fact]
    public void TruncateString_SplitPointInsideSurrogatePair_DropsTheLoneHighSurrogate()
    {
        // "A" + U+1F600 (high+low surrogate). Truncating to length 2 would keep "A" plus the
        // lone HIGH surrogate - ill-formed UTF-16. The guard must drop it, yielding just "A".
        var value = "A😀"; // length 3 in UTF-16 code units
        var result = BackupSanitizer.TruncateString(value, 2);

        Assert.Equal("A", result);
        // No unpaired surrogate remains.
        Assert.DoesNotContain(result, c => char.IsHighSurrogate(c) || char.IsLowSurrogate(c));
    }

    [Fact]
    public void TruncateString_SplitPointAfterCompletePair_KeepsThePair()
    {
        // Truncating to length 3 keeps "A" + the full emoji (both surrogate halves).
        var value = "A😀B";
        var result = BackupSanitizer.TruncateString(value, 3);

        Assert.Equal("A😀", result);
    }
}
