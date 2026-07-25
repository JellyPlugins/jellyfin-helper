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
        // 0 is the "immediate cleanup" sentinel — must not be clamped to 1.
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

    // ===== Baseline directory trimming =====

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
}
