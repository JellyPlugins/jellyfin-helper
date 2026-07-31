using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

public sealed class TimelineAggregatorTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ===== DetermineGranularity =====

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(89)]
    [InlineData(90)]
    public void DetermineGranularity_UpTo90Days_ReturnsDaily(int daysAgo)
    {
        var earliest = Now.AddDays(-daysAgo);
        Assert.Equal("daily", TimelineAggregator.DetermineGranularity(earliest, Now));
    }

    [Theory]
    [InlineData(91)]
    [InlineData(180)]
    [InlineData(365)]
    public void DetermineGranularity_91To365Days_ReturnsWeekly(int daysAgo)
    {
        var earliest = Now.AddDays(-daysAgo);
        Assert.Equal("weekly", TimelineAggregator.DetermineGranularity(earliest, Now));
    }

    [Theory]
    [InlineData(366)]
    [InlineData(500)]
    [InlineData(2 * 365)]
    public void DetermineGranularity_366To730Days_ReturnsMonthly(int daysAgo)
    {
        var earliest = Now.AddDays(-daysAgo);
        Assert.Equal("monthly", TimelineAggregator.DetermineGranularity(earliest, Now));
    }

    [Theory]
    [InlineData(2 * 365 + 1)]
    [InlineData(1000)]
    [InlineData(5 * 365)]
    public void DetermineGranularity_731To1825Days_ReturnsQuarterly(int daysAgo)
    {
        var earliest = Now.AddDays(-daysAgo);
        Assert.Equal("quarterly", TimelineAggregator.DetermineGranularity(earliest, Now));
    }

    [Theory]
    [InlineData(5 * 365 + 1)]
    [InlineData(10 * 365)]
    public void DetermineGranularity_MoreThan5Years_ReturnsYearly(int daysAgo)
    {
        var earliest = Now.AddDays(-daysAgo);
        Assert.Equal("yearly", TimelineAggregator.DetermineGranularity(earliest, Now));
    }

    [Fact]
    public void DetermineGranularity_EarliestEqualsNow_ReturnsDaily()
    {
        Assert.Equal("daily", TimelineAggregator.DetermineGranularity(Now, Now));
    }

    // ===== GenerateBucketStarts =====

    [Fact]
    public void GenerateBucketStarts_Daily_ProducesOneBucketPerDay()
    {
        var start = Now.AddDays(-3);
        var buckets = TimelineAggregator.GenerateBucketStarts(start, Now, "daily");
        Assert.True(buckets.Count >= 3, $"Expected at least 3 daily buckets, got {buckets.Count}");
        for (var i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(1, (buckets[i] - buckets[i - 1]).Days);
        }
    }

    [Fact]
    public void GenerateBucketStarts_Weekly_ProducesOneBucketPerWeek()
    {
        var start = Now.AddDays(-14);
        var buckets = TimelineAggregator.GenerateBucketStarts(start, Now, "weekly");
        Assert.True(buckets.Count >= 2, $"Expected at least 2 weekly buckets, got {buckets.Count}");
        for (var i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(7, (buckets[i] - buckets[i - 1]).Days);
        }
    }

    [Fact]
    public void GenerateBucketStarts_Monthly_FirstBucketIsFirstOfMonth()
    {
        var start = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var buckets = TimelineAggregator.GenerateBucketStarts(start, Now, "monthly");
        Assert.True(buckets.Count > 0);
        Assert.Equal(1, buckets[0].Day);
    }

    [Fact]
    public void GenerateBucketStarts_UnknownGranularity_DoesNotThrow()
    {
        var start = Now.AddDays(-10);
        var ex = Record.Exception(() => TimelineAggregator.GenerateBucketStarts(start, Now, "unknown"));
        Assert.Null(ex);
    }

    // ===== BuildCumulativeTimeline =====

    [Fact]
    public void BuildCumulativeTimeline_LargeCountDeltas_NoOverflow()
    {
        // Arrange: two entries whose CountDelta values together exceed int.MaxValue.
        // int.MaxValue = 2_147_483_647; we use two entries each carrying that value,
        // so the expected cumulative total is 2L * int.MaxValue = 4_294_967_294.
        var earliest = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        long delta = (long)int.MaxValue;
        long expectedTotal = delta * 2; // 4_294_967_294 - exceeds int range

        var entries = new List<GrowthTimelineService.FileEntry>
        {
            new() { CreatedUtc = earliest,             Size = 0, CountDelta = delta },
            new() { CreatedUtc = earliest.AddDays(1),  Size = 0, CountDelta = delta },
        };

        // Act
        var points = TimelineAggregator.BuildCumulativeTimeline(entries, earliest, now, "daily");

        // Assert: the final point must carry the full long sum without overflow
        Assert.NotEmpty(points);
        var lastPoint = points[^1];
        Assert.Equal(expectedTotal, lastPoint.CumulativeFileCount);
    }

    // ===== DeduplicateConsecutivePoints =====

    [Fact]
    public void DeduplicateConsecutivePoints_LastPointEqualsSecondToLast_NotDuplicated()
    {
        // Arrange: a list whose last two points are identical (Size=100, Count=10)
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = Now.AddDays(-2), CumulativeSize = 50,  CumulativeFileCount = 5  },
            new() { Date = Now.AddDays(-1), CumulativeSize = 100, CumulativeFileCount = 10 },
            new() { Date = Now,             CumulativeSize = 100, CumulativeFileCount = 10 }
        };

        var result = TimelineAggregator.DeduplicateConsecutivePoints(points);

        // The last point is a duplicate of the second-to-last and must be dropped,
        // so only two points survive.
        Assert.Equal(2, result.Count);
        Assert.Equal(Now.AddDays(-2), result[0].Date);
        Assert.Equal(Now.AddDays(-1), result[1].Date);
        Assert.Equal(100, result[1].CumulativeSize);
        Assert.Equal(10,  result[1].CumulativeFileCount);
    }
}
