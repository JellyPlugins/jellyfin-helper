using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

public sealed class TimelineAggregatorTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void BuildCumulativeTimeline_LargeCountDeltas_NoOverflow()
    {
        // Arrange: two entries whose CountDelta values together exceed int.MaxValue. int.MaxValue = 2_147_483_647; we use two entries each carrying that value, so the expected cumulative total is 2L * int.MaxValue = 4_294_967_294.
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

    [Fact]
    public void BuildIncrementalEntries_MixedBaselineChangeNewAndDeleted_EmitsCorrectDeltas()
    {
        // One scan covering every branch at once: unchanged, grown, new, and deleted.
        var aCreated = Now.AddDays(-100);
        var bCreated = Now.AddDays(-80);
        var cCreated = Now.AddDays(-10);
        var dCreated = Now.AddDays(-60);

        var baseline = new GrowthTimelineBaseline();
        baseline.Directories["A"] = new BaselineDirectoryEntry { CreatedUtc = aCreated, Size = 100, Count = 2 };
        baseline.Directories["B"] = new BaselineDirectoryEntry { CreatedUtc = bCreated, Size = 200, Count = 3 };
        baseline.Directories["D"] = new BaselineDirectoryEntry { CreatedUtc = dCreated, Size = 300, Count = 4 };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "A", CreatedUtc = aCreated, Size = 100, Count = 2 }, // unchanged
            new() { Path = "B", CreatedUtc = bCreated, Size = 250, Count = 5 }, // grew: +50 size, +2 count
            new() { Path = "C", CreatedUtc = cCreated, Size = 400, Count = 6 }  // new
            // D dropped -> deleted
        };

        var entries = TimelineAggregator.BuildIncrementalEntries(currentDirs, baseline, Now);

        // 3 baseline seeds + 1 B delta + 1 C full + 1 D negative = 6 entries.
        Assert.Equal(6, entries.Count);

        // Baseline seeds sit at their original creation dates with original sizes/counts.
        Assert.Contains(entries, e => e.CreatedUtc == aCreated && e.Size == 100 && e.CountDelta == 2);
        Assert.Contains(entries, e => e.CreatedUtc == bCreated && e.Size == 200 && e.CountDelta == 3);
        Assert.Contains(entries, e => e.CreatedUtc == dCreated && e.Size == 300 && e.CountDelta == 4);

        // Unchanged A yields no delta beyond its single seed.
        Assert.Single(entries, e => e.CreatedUtc == aCreated);

        // B's growth is a single positive delta at now.
        Assert.Contains(entries, e => e.CreatedUtc == Now && e.Size == 50 && e.CountDelta == 2);

        // New C carries full size at its own creation date.
        Assert.Contains(entries, e => e.CreatedUtc == cCreated && e.Size == 400 && e.CountDelta == 6);

        // Deleted D is a negative entry at now.
        Assert.Contains(entries, e => e.CreatedUtc == Now && e.Size == -300 && e.CountDelta == -4);
    }

    [Fact]
    public void BuildIncrementalEntries_ZeroCountEntries_TreatedAsSingleItem()
    {
        // A reported Count of 0 is normalized to 1 everywhere (count ?? 1 semantics).
        var seedCreated = Now.AddDays(-50);
        var newCreated = Now.AddDays(-5);
        var deletedCreated = Now.AddDays(-40);

        var baseline = new GrowthTimelineBaseline();
        baseline.Directories["S"] = new BaselineDirectoryEntry { CreatedUtc = seedCreated, Size = 500, Count = 0 };
        baseline.Directories["Del"] = new BaselineDirectoryEntry { CreatedUtc = deletedCreated, Size = 600, Count = 0 };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "S", CreatedUtc = seedCreated, Size = 500, Count = 0 },  // unchanged size, count 0==0
            new() { Path = "New", CreatedUtc = newCreated, Size = 700, Count = 0 }  // new, zero count
        };

        var entries = TimelineAggregator.BuildIncrementalEntries(currentDirs, baseline, Now);

        // Baseline zero-count seed normalizes to CountDelta 1.
        Assert.Contains(entries, e => e.CreatedUtc == seedCreated && e.Size == 500 && e.CountDelta == 1);

        // New zero-count dir also normalizes to 1.
        Assert.Contains(entries, e => e.CreatedUtc == newCreated && e.Size == 700 && e.CountDelta == 1);

        // Deleted zero-count dir removes a single item.
        Assert.Contains(entries, e => e.CreatedUtc == Now && e.Size == -600 && e.CountDelta == -1);

        // Unchanged zero-count dir (1-1==0, size diff 0) contributes no delta beyond its seed.
        Assert.Single(entries, e => e.CreatedUtc == seedCreated);
    }

    [Fact]
    public void BuildIncrementalEntries_CountChangesButSizeConstant_EmitsCountOnlyDelta()
    {
        // Count-only changes must still be recorded (countDiff!=0 branch of the guard).
        var created = Now.AddDays(-30);

        var baseline = new GrowthTimelineBaseline();
        baseline.Directories["X"] = new BaselineDirectoryEntry { CreatedUtc = created, Size = 1000, Count = 3 };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "X", CreatedUtc = created, Size = 1000, Count = 5 } // size same, count 3->5
        };

        var entries = TimelineAggregator.BuildIncrementalEntries(currentDirs, baseline, Now);

        // 1 seed + 1 count-only delta.
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.CreatedUtc == Now && e.Size == 0 && e.CountDelta == 2);
    }

    [Fact]
    public void UpdateBaseline_AddsNewUpdatesExistingRemovesMissing_MutatesBaseline()
    {
        var xCreated = Now.AddDays(-100);
        var yCreated = Now.AddDays(-3);
        var zCreated = Now.AddDays(-70);

        var baseline = new GrowthTimelineBaseline();
        baseline.Directories["X"] = new BaselineDirectoryEntry { CreatedUtc = xCreated, Size = 100, Count = 2 };
        baseline.Directories["Z"] = new BaselineDirectoryEntry { CreatedUtc = zCreated, Size = 300, Count = 4 };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "X", CreatedUtc = xCreated, Size = 150, Count = 5 }, // update
            new() { Path = "Y", CreatedUtc = yCreated, Size = 400, Count = 6 }  // add
            // Z dropped -> removed
        };

        TimelineAggregator.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(2, baseline.Directories.Count);
        Assert.True(baseline.Directories.ContainsKey("X"));
        Assert.True(baseline.Directories.ContainsKey("Y"));
        Assert.False(baseline.Directories.ContainsKey("Z"));

        // X updated in place, original creation date preserved.
        var x = baseline.Directories["X"];
        Assert.Equal(150, x.Size);
        Assert.Equal(5, x.Count);
        Assert.Equal(xCreated, x.CreatedUtc);

        // Y added with the current scan's metadata.
        var y = baseline.Directories["Y"];
        Assert.Equal(yCreated, y.CreatedUtc);
        Assert.Equal(400, y.Size);
        Assert.Equal(6, y.Count);
    }

    [Fact]
    public void BuildCumulativeTimeline_EarliestAfterNow_ReturnsEmpty()
    {
        // earliest later than now -> no buckets -> no fabricated points.
        var earliest = Now.AddDays(5);
        var entries = new List<GrowthTimelineService.FileEntry>
        {
            new() { CreatedUtc = earliest, Size = 100, CountDelta = 1 }
        };

        var points = TimelineAggregator.BuildCumulativeTimeline(entries, earliest, Now, "daily");

        Assert.Empty(points);
    }

    [Theory]
    [InlineData(2, 1)]   // Feb -> Q1 starts Jan
    [InlineData(5, 4)]   // May -> Q2 starts Apr
    [InlineData(8, 7)]   // Aug -> Q3 starts Jul
    [InlineData(11, 10)] // Nov -> Q4 starts Oct
    public void GetBucketStart_Quarterly_SnapsToFirstDayOfQuarter(int inputMonth, int expectedMonth)
    {
        var date = new DateTime(2024, inputMonth, 15, 13, 45, 0, DateTimeKind.Utc);

        var start = TimelineAggregator.GetBucketStart(date, "quarterly");

        Assert.Equal(2024, start.Year);
        Assert.Equal(expectedMonth, start.Month);
        Assert.Equal(1, start.Day);
        Assert.Equal(0, start.Hour);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void GetBucketStart_Yearly_SnapsToJanuaryFirst()
    {
        var date = new DateTime(2023, 7, 20, 9, 30, 0, DateTimeKind.Utc);

        var start = TimelineAggregator.GetBucketStart(date, "yearly");

        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void GenerateBucketStarts_QuarterlyAndYearly_AdvanceByQuarterAndYear()
    {
        var start = new DateTime(2020, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var quarterly = TimelineAggregator.GenerateBucketStarts(start, end, "quarterly");
        Assert.True(quarterly.Count >= 2);
        for (var i = 1; i < quarterly.Count; i++)
        {
            Assert.Equal(quarterly[i - 1].AddMonths(3), quarterly[i]);
        }

        var yearly = TimelineAggregator.GenerateBucketStarts(start, end, "yearly");
        Assert.True(yearly.Count >= 2);
        for (var i = 1; i < yearly.Count; i++)
        {
            Assert.Equal(yearly[i - 1].Year + 1, yearly[i].Year);
            Assert.Equal(yearly[i - 1].Month, yearly[i].Month);
            Assert.Equal(yearly[i - 1].Day, yearly[i].Day);
        }
    }

    [Fact]
    public void TrimLeadingZeros_EmptyInput_ReturnsEmpty()
    {
        var result = TimelineAggregator.TrimLeadingZeros(new List<GrowthTimelinePoint>());
        Assert.Empty(result);
    }

    [Fact]
    public void TrimLeadingZeros_LeadingZeros_KeepsExactlyOneZeroBaseline()
    {
        // Three leading zeros then a rise; exactly one zero baseline should remain.
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = Now.AddDays(-4), CumulativeSize = 0,   CumulativeFileCount = 0 },
            new() { Date = Now.AddDays(-3), CumulativeSize = 0,   CumulativeFileCount = 0 },
            new() { Date = Now.AddDays(-2), CumulativeSize = 0,   CumulativeFileCount = 0 },
            new() { Date = Now.AddDays(-1), CumulativeSize = 50,  CumulativeFileCount = 1 },
            new() { Date = Now,             CumulativeSize = 100, CumulativeFileCount = 2 }
        };

        var result = TimelineAggregator.TrimLeadingZeros(points);

        // firstNonZero==3, so drop the first 2 zeros and keep 1 before the rise.
        Assert.Equal(points.Count - 2, result.Count);
        Assert.Equal(0, result[0].CumulativeSize);
        Assert.Equal(0, result[0].CumulativeFileCount);
        Assert.Equal(Now.AddDays(-2), result[0].Date);
        Assert.Equal(50, result[1].CumulativeSize);
        Assert.Equal(Now.AddDays(-1), result[1].Date);
    }

    [Fact]
    public void ConsolidateToGranularity_MultipleDailyPointsPerMonth_KeepsLastPerBucket()
    {
        // Daily points across two months collapse to one point per month, keeping the last.
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 1, 20, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 2, 5, 0, 0, 0, DateTimeKind.Utc),  CumulativeSize = 300, CumulativeFileCount = 3 },
            new() { Date = new DateTime(2024, 2, 25, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 400, CumulativeFileCount = 4 }
        };

        var result = TimelineAggregator.ConsolidateToGranularity(points, "monthly");

        Assert.Equal(2, result.Count);

        // Ascending by date.
        Assert.True(result[0].Date < result[1].Date);

        // January bucket keeps the last (Jan 20) values, snapped to the 1st.
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(200, result[0].CumulativeSize);
        Assert.Equal(2, result[0].CumulativeFileCount);

        // February bucket keeps the last (Feb 25) values.
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
        Assert.Equal(400, result[1].CumulativeSize);
        Assert.Equal(4, result[1].CumulativeFileCount);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_LastPointDiffersFromTail_IsAppended()
    {
        // Plateau in the middle then a genuinely new final value; the last point must survive.
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = Now.AddDays(-3), CumulativeSize = 50,  CumulativeFileCount = 5  },
            new() { Date = Now.AddDays(-2), CumulativeSize = 100, CumulativeFileCount = 10 },
            new() { Date = Now.AddDays(-1), CumulativeSize = 100, CumulativeFileCount = 10 },
            new() { Date = Now,             CumulativeSize = 150, CumulativeFileCount = 15 }
        };

        var result = TimelineAggregator.DeduplicateConsecutivePoints(points);

        var tail = result[^1];
        Assert.Equal(Now, tail.Date);
        Assert.Equal(150, tail.CumulativeSize);
        Assert.Equal(15, tail.CumulativeFileCount);
    }
}
