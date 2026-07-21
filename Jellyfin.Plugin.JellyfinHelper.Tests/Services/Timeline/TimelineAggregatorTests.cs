using System;
using System.Collections.Generic;
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
}
