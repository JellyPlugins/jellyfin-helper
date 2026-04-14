using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

public class GrowthTimelineServiceTests
{
    // ── DetermineGranularity ──────────────────────────────────────────────

    [Fact]
    public void DetermineGranularity_LessThan90Days_ReturnsDaily()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-30);
        Assert.Equal("daily", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Exactly90Days_ReturnsDaily()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-90);
        Assert.Equal("daily", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_91Days_ReturnsWeekly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-91);
        Assert.Equal("weekly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Between91And365Days_ReturnsWeekly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-200);
        Assert.Equal("weekly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Exactly365Days_ReturnsWeekly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-365);
        Assert.Equal("weekly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_366Days_ReturnsMonthly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-366);
        Assert.Equal("monthly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Between1And2Years_ReturnsMonthly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-500);
        Assert.Equal("monthly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Exactly2Years_ReturnsMonthly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-2 * 365);
        Assert.Equal("monthly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_MoreThan2Years_ReturnsQuarterly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-2 * 365 - 1);
        Assert.Equal("quarterly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Between2And5Years_ReturnsQuarterly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-3 * 365);
        Assert.Equal("quarterly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_Exactly5Years_ReturnsQuarterly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-5 * 365);
        Assert.Equal("quarterly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_MoreThan5Years_ReturnsYearly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-5 * 365 - 1);
        Assert.Equal("yearly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_10Years_ReturnsYearly()
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-10 * 365);
        Assert.Equal("yearly", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    [Fact]
    public void DetermineGranularity_SameDay_ReturnsDaily()
    {
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var earliest = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("daily", GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    // ── GenerateBucketStarts ──────────────────────────────────────────────

    [Fact]
    public void GenerateBucketStarts_Daily_GeneratesCorrectBuckets()
    {
        var earliest = new DateTime(2025, 1, 1, 10, 30, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "daily");

        Assert.Equal(5, buckets.Count);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[0]);
        Assert.Equal(new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc), buckets[1]);
        Assert.Equal(new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc), buckets[4]);
    }

    [Fact]
    public void GenerateBucketStarts_Monthly_GeneratesCorrectBuckets()
    {
        var earliest = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 4, 10, 0, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "monthly");

        Assert.Equal(4, buckets.Count);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[0]);
        Assert.Equal(new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), buckets[1]);
        Assert.Equal(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), buckets[2]);
        Assert.Equal(new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc), buckets[3]);
    }

    [Fact]
    public void GenerateBucketStarts_Quarterly_GeneratesCorrectBuckets()
    {
        var earliest = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 2, 10, 0, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "quarterly");

        // Q1 2024 (Jan 1), Q2 2024 (Apr 1), Q3 2024 (Jul 1), Q4 2024 (Oct 1), Q1 2025 (Jan 1)
        Assert.Equal(5, buckets.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[0]);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), buckets[1]);
        Assert.Equal(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), buckets[2]);
        Assert.Equal(new DateTime(2024, 10, 1, 0, 0, 0, DateTimeKind.Utc), buckets[3]);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[4]);
    }

    [Fact]
    public void GenerateBucketStarts_Yearly_GeneratesCorrectBuckets()
    {
        var earliest = new DateTime(2022, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "yearly");

        Assert.Equal(4, buckets.Count);
        Assert.Equal(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[0]);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[1]);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[2]);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), buckets[3]);
    }

    [Fact]
    public void GenerateBucketStarts_Weekly_StartsOnMonday()
    {
        // Wednesday Jan 15 2025
        var earliest = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "weekly");

        // First bucket should be Monday Jan 13
        Assert.Equal(DayOfWeek.Monday, buckets[0].DayOfWeek);
        Assert.Equal(new DateTime(2025, 1, 13, 0, 0, 0, DateTimeKind.Utc), buckets[0]);

        // All buckets should be 7 days apart
        for (int i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(7, (buckets[i] - buckets[i - 1]).TotalDays);
        }
    }

    [Fact]
    public void GenerateBucketStarts_SingleDay_ReturnsSingleBucket()
    {
        var earliest = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 6, 1, 18, 0, 0, DateTimeKind.Utc);

        var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, "daily");

        Assert.Single(buckets);
        Assert.Equal(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), buckets[0]);
    }

    [Fact]
    public void GenerateBucketStarts_BucketsAreAlwaysChronological()
    {
        var earliest = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var granularity in new[] { "daily", "weekly", "monthly", "quarterly", "yearly" })
        {
            var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, granularity);
            for (int i = 1; i < buckets.Count; i++)
            {
                Assert.True(buckets[i] > buckets[i - 1], $"Buckets not chronological for {granularity} at index {i}");
            }
        }
    }

    [Fact]
    public void GenerateBucketStarts_AllBucketsAreUtc()
    {
        var earliest = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var granularity in new[] { "daily", "weekly", "monthly", "quarterly", "yearly" })
        {
            var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, granularity);
            foreach (var bucket in buckets)
            {
                Assert.Equal(DateTimeKind.Utc, bucket.Kind);
            }
        }
    }

    [Fact]
    public void GenerateBucketStarts_LastBucketDoesNotExceedNow()
    {
        var earliest = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        foreach (var granularity in new[] { "daily", "weekly", "monthly", "quarterly", "yearly" })
        {
            var buckets = GrowthTimelineService.GenerateBucketStarts(earliest, now, granularity);
            Assert.NotEmpty(buckets);
            Assert.True(buckets.Last() <= now, $"Last bucket exceeds now for {granularity}");
        }
    }

    // ── DetermineGranularity boundary consistency ─────────────────────────

    [Theory]
    [InlineData(1, "daily")]
    [InlineData(30, "daily")]
    [InlineData(89, "daily")]
    [InlineData(91, "weekly")]
    [InlineData(180, "weekly")]
    [InlineData(366, "monthly")]
    [InlineData(731, "quarterly")]
    [InlineData(1826, "yearly")]
    public void DetermineGranularity_VariousSpans_ReturnsExpected(int daysAgo, string expected)
    {
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-daysAgo);
        Assert.Equal(expected, GrowthTimelineService.DetermineGranularity(earliest, now));
    }

    // ── BuildCumulativeTimeline ────────────────────────────────────────────

    [Fact]
    public void BuildCumulativeTimeline_EmptyEntries_ReturnsEmptyPoints()
    {
        var entries = new List<GrowthTimelineService.FileEntry>();
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddMonths(-3);

        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        // Buckets are generated but all have 0 size
        Assert.All(points, p => Assert.Equal(0L, p.CumulativeSize));
    }

    [Fact]
    public void BuildCumulativeTimeline_SingleEntry_AssignedToCorrectBucket()
    {
        var entries = new List<GrowthTimelineService.FileEntry>
        {
            new() { CreatedUtc = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc), Size = 1000 },
        };
        var earliest = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        // Jan and Feb should be 0, Mar onwards should be 1000
        var jan = points.First(p => p.Date.Month == 1);
        var feb = points.First(p => p.Date.Month == 2);
        var mar = points.First(p => p.Date.Month == 3);
        var apr = points.First(p => p.Date.Month == 4);

        Assert.Equal(0L, jan.CumulativeSize);
        Assert.Equal(0L, feb.CumulativeSize);
        Assert.Equal(1000L, mar.CumulativeSize);
        Assert.Equal(1000L, apr.CumulativeSize);
    }

    [Fact]
    public void BuildCumulativeTimeline_MultipleEntries_CumulativeSizeGrows()
    {
        var entries = new List<GrowthTimelineService.FileEntry>
        {
            new() { CreatedUtc = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc), Size = 500 },
            new() { CreatedUtc = new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc), Size = 300 },
            new() { CreatedUtc = new DateTime(2025, 3, 5, 0, 0, 0, DateTimeKind.Utc), Size = 200 },
        };
        var earliest = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        Assert.Equal(500L, points[0].CumulativeSize);  // Jan
        Assert.Equal(800L, points[1].CumulativeSize);  // Feb
        Assert.Equal(1000L, points[2].CumulativeSize); // Mar
        Assert.Equal(1000L, points[3].CumulativeSize); // Apr
    }

    [Fact]
    public void BuildCumulativeTimeline_NegativeEntries_DecreaseCumulativeSize()
    {
        var entries = new List<GrowthTimelineService.FileEntry>
        {
            new() { CreatedUtc = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc), Size = 1000 },
            new() { CreatedUtc = new DateTime(2025, 3, 5, 0, 0, 0, DateTimeKind.Utc), Size = -300 },
        };
        var earliest = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        Assert.Equal(1000L, points[0].CumulativeSize); // Jan
        Assert.Equal(1000L, points[1].CumulativeSize); // Feb
        Assert.Equal(700L, points[2].CumulativeSize);  // Mar (1000 - 300)
    }

    // ── BuildIncrementalEntries ────────────────────────────────────────────

    [Fact]
    public void BuildIncrementalEntries_NoChanges_ReturnsBaselineEntriesOnly()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Matrix"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 3,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Matrix", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 3 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Only the baseline entry (no diff because size unchanged)
        Assert.Single(entries);
        Assert.Equal(8_000_000_000L, entries[0].Size);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), entries[0].CreatedUtc);
    }

    [Fact]
    public void BuildIncrementalEntries_DirectoryGrew_AddsDiffAtScanTime()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Matrix"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Matrix", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000, Count = 3 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Baseline entry + diff entry
        Assert.Equal(2, entries.Count);

        // First: baseline entry at original date with original size
        Assert.Equal(8_000_000_000L, entries[0].Size);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), entries[0].CreatedUtc);

        // Second: diff entry at scan time
        Assert.Equal(2_000_000_000L, entries[1].Size);
        Assert.Equal(now, entries[1].CreatedUtc);
    }

    [Fact]
    public void BuildIncrementalEntries_DirectoryShrunk_AddsNegativeDiffAtScanTime()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Matrix"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 10_000_000_000,
            Count = 3,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Matrix", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 7_000_000_000, Count = 2 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        Assert.Equal(2, entries.Count);
        Assert.Equal(-3_000_000_000L, entries[1].Size);
        Assert.Equal(now, entries[1].CreatedUtc);
    }

    [Fact]
    public void BuildIncrementalEntries_NewDirectoryAfterBaseline_AddsAtCreationDate()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };

        var newDirCreated = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
            new() { Path = "/movies/Batman", CreatedUtc = newDirCreated, Size = 5_000_000_000, Count = 1 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Baseline entry + new directory entry (no diff for Avatar since unchanged)
        Assert.Equal(2, entries.Count);

        // The new directory should be at its creation date
        var newEntry = entries.First(e => e.Size == 5_000_000_000);
        Assert.Equal(newDirCreated, newEntry.CreatedUtc);
    }

    [Fact]
    public void BuildIncrementalEntries_DeletedDirectory_AddsNegativeAtScanTime()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };
        baseline.Directories["/movies/Batman"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
            Count = 1,
        };

        // Batman was removed
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // 2 baseline entries + 1 negative entry for deleted Batman
        Assert.Equal(3, entries.Count);

        var negativeEntry = entries.First(e => e.Size == -5_000_000_000);
        Assert.Equal(now, negativeEntry.CreatedUtc);
        Assert.Equal(-1, negativeEntry.CountDelta);
    }

    [Fact]
    public void BuildIncrementalEntries_CombinedScenario_CorrectTotalSize()
    {
        // Baseline: Avatar (8GB, 2 items), Batman (5GB, 1 item)
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };
        baseline.Directories["/movies/Batman"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
            Count = 1,
        };

        // Current: Avatar grew to 10GB, Batman unchanged, Casablanca is new (3GB)
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000, Count = 3 },
            new() { Path = "/movies/Batman", CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc), Size = 5_000_000_000, Count = 1 },
            new() { Path = "/movies/Casablanca", CreatedUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 3_000_000_000, Count = 1 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Total should be: 8GB (baseline Avatar) + 5GB (baseline Batman) + 2GB (diff Avatar) + 3GB (new Casablanca) = 18GB
        var totalSize = entries.Sum(e => e.Size);
        Assert.Equal(18_000_000_000L, totalSize);
    }

    [Fact]
    public void BuildIncrementalEntries_CaseInsensitivePaths_MatchesOnWindows()
    {
        // On Windows the baseline dictionary uses OrdinalIgnoreCase, so paths
        // with different casing should match. On Linux they won't match and
        // the entry will be treated as new. This test verifies the platform-
        // specific behaviour is consistent.
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories[@"C:\Movies\Matrix"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 3,
        };

        // Current scan reports the same directory but with different casing
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = @"C:\movies\matrix", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 3 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        if (OperatingSystem.IsWindows())
        {
            // Case-insensitive match: baseline entry matches, no diff → 1 entry
            Assert.Single(entries);
            Assert.Equal(8_000_000_000L, entries[0].Size);
        }
        else
        {
            // Case-sensitive: treated as different paths → baseline entry + new entry + deletion of old
            Assert.True(entries.Count > 1, "On Linux, case-different paths should be treated as distinct.");
        }
    }

    [Fact]
    public void BuildIncrementalEntries_DeletedDirectory_CaseInsensitiveDetection()
    {
        // Verify that the deleted-directory detection (which uses its own HashSet
        // with OrdinalIgnoreCase) correctly matches paths with different casing.
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories[@"/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };

        // Current scan has same path but different casing
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = @"/movies/avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // The currentPaths HashSet uses OrdinalIgnoreCase, so "/movies/Avatar" should
        // be found as present (not deleted) regardless of the "/movies/avatar" casing.
        var negativeEntries = entries.Where(e => e.Size < 0).ToList();
        Assert.Empty(negativeEntries);
    }

    [Fact]
    public void BuildIncrementalEntries_BackwardsCompatibility_CountZeroTreatedAsOne()
    {
        // Simulate a legacy baseline entry with Count = 0
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 0, // Legacy: no count stored
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Baseline entry should have CountDelta = 1 (not 0)
        Assert.Single(entries);
        Assert.Equal(1, entries[0].CountDelta);
    }

    // ── UpdateBaseline ────────────────────────────────────────────────────

    [Fact]
    public void UpdateBaseline_NewDirectory_AddedToBaseline()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
            new() { Path = "/movies/Batman", CreatedUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 5_000_000_000, Count = 1 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(2, baseline.Directories.Count);
        Assert.True(baseline.Directories.ContainsKey("/movies/Batman"));
        Assert.Equal(5_000_000_000L, baseline.Directories["/movies/Batman"].Size);
        Assert.Equal(1, baseline.Directories["/movies/Batman"].Count);
    }

    [Fact]
    public void UpdateBaseline_ExistingDirectory_SizeAndCountUpdated()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000, Count = 3 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(10_000_000_000L, baseline.Directories["/movies/Avatar"].Size);
        Assert.Equal(3, baseline.Directories["/movies/Avatar"].Count);
        // Creation date should NOT change
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), baseline.Directories["/movies/Avatar"].CreatedUtc);
    }

    [Fact]
    public void UpdateBaseline_DeletedDirectory_RemovedFromBaseline()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };
        baseline.Directories["/movies/Batman"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
            Count = 1,
        };

        // Batman removed
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Single(baseline.Directories);
        Assert.False(baseline.Directories.ContainsKey("/movies/Batman"));
    }

    [Fact]
    public void UpdateBaseline_PreservesFirstScanTimestamp()
    {
        var originalTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = originalTimestamp,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(originalTimestamp, baseline.FirstScanTimestamp);
    }

    // ── End-to-end: BuildIncrementalEntries → BuildCumulativeTimeline ────

    [Fact]
    public void EndToEnd_DirectoryScenario_TimelineShowsCorrectGrowth()
    {
        // Scenario: Directory created 2024-03 (8GB at baseline)
        // By 2025-06 it grew to 10GB → graph should show 8GB from 2024 and +2GB in 2025
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/library/Stranger Things"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 5,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/library/Stranger Things", CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000, Count = 6 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);
        entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        var earliest = entries[0].CreatedUtc;
        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        // After March 2024: should show 8GB
        var march2024 = points.First(p => p.Date == new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(8_000_000_000L, march2024.CumulativeSize);

        // Between April 2024 and May 2025: should still be 8GB (no change)
        var dec2024 = points.First(p => p.Date == new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(8_000_000_000L, dec2024.CumulativeSize);

        // June 2025: should show 10GB (8GB + 2GB diff)
        var lastPoint = points.Last();
        Assert.Equal(10_000_000_000L, lastPoint.CumulativeSize);
    }

    [Fact]
    public void EndToEnd_MultipleDirectories_TimelineAccurate()
    {
        // Baseline: Avatar 8GB (created 2024-01), Batman 5GB (created 2024-06)
        // Current: Avatar unchanged, Batman grew to 7GB, Casablanca new 3GB (created 2025-02)
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/Avatar"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
            Count = 2,
        };
        baseline.Directories["/movies/Batman"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
            Count = 1,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/Avatar", CreatedUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000, Count = 2 },
            new() { Path = "/movies/Batman", CreatedUtc = new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc), Size = 7_000_000_000, Count = 2 },
            new() { Path = "/movies/Casablanca", CreatedUtc = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), Size = 3_000_000_000, Count = 1 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);
        entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        var earliest = entries[0].CreatedUtc;
        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        // Total should be: 8 + 5 + 2 (diff Batman) + 3 (new Casablanca) = 18GB
        var lastPoint = points.Last();
        Assert.Equal(18_000_000_000L, lastPoint.CumulativeSize);

        // Jan 2024: Avatar = 8GB
        var jan2024 = points.First(p => p.Date == new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(8_000_000_000L, jan2024.CumulativeSize);

        // Jun 2024: Avatar + Batman = 13GB
        var jun2024 = points.First(p => p.Date == new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(13_000_000_000L, jun2024.CumulativeSize);

        // Feb 2025: + Casablanca 3GB = 16GB
        var feb2025 = points.First(p => p.Date == new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(16_000_000_000L, feb2025.CumulativeSize);
    }

    // ── MergeSnapshotIntoTimeline ─────────────────────────────────────────

    [Fact]
    public void MergeSnapshotIntoTimeline_PreservesHistoricalPoints()
    {
        var existingPoints = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5_000_000_000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8_000_000_000, CumulativeFileCount = 15 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 10_000_000_000, CumulativeFileCount = 20 },
        };

        var now = new DateTime(2024, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 12_000_000_000, 25, "monthly");

        // 3 historical points + 1 current = 4
        Assert.Equal(4, result.Count);
        Assert.Equal(5_000_000_000L, result[0].CumulativeSize);
        Assert.Equal(8_000_000_000L, result[1].CumulativeSize);
        Assert.Equal(10_000_000_000L, result[2].CumulativeSize);
        Assert.Equal(12_000_000_000L, result[3].CumulativeSize);
        Assert.Equal(25, result[3].CumulativeFileCount);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), result[3].Date);
    }

    [Fact]
    public void MergeSnapshotIntoTimeline_ReplacesCurrentBucket()
    {
        // Existing timeline already has a point for the current bucket
        var existingPoints = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5_000_000_000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8_000_000_000, CumulativeFileCount = 15 },
        };

        // "now" is still in February → should replace the Feb point
        var now = new DateTime(2024, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 9_000_000_000, 18, "monthly");

        Assert.Equal(2, result.Count);
        Assert.Equal(5_000_000_000L, result[0].CumulativeSize); // Jan unchanged
        Assert.Equal(9_000_000_000L, result[1].CumulativeSize); // Feb replaced
        Assert.Equal(18, result[1].CumulativeFileCount);
    }

    [Fact]
    public void MergeSnapshotIntoTimeline_DeletionShowsAsDropAtCurrentTime()
    {
        // Scenario: Library had 100GB, then files were deleted
        var existingPoints = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 50_000_000_000, CumulativeFileCount = 100 },
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100_000_000_000, CumulativeFileCount = 200 },
        };

        // Files with old creation dates were deleted → current total is smaller
        var now = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 80_000_000_000, 160, "monthly");

        // Historical points preserved, current shows the drop
        Assert.Equal(3, result.Count);
        Assert.Equal(50_000_000_000L, result[0].CumulativeSize);  // 2023-01 unchanged
        Assert.Equal(100_000_000_000L, result[1].CumulativeSize); // 2024-01 unchanged (history!)
        Assert.Equal(80_000_000_000L, result[2].CumulativeSize);  // 2024-06 shows deletion
        Assert.Equal(160, result[2].CumulativeFileCount);
    }

    [Fact]
    public void MergeSnapshotIntoTimeline_EmptyExistingPoints_AddsCurrentOnly()
    {
        var existingPoints = new List<GrowthTimelinePoint>();

        var now = new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 5_000_000_000, 10, "monthly");

        Assert.Single(result);
        Assert.Equal(5_000_000_000L, result[0].CumulativeSize);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
    }

    [Fact]
    public void MergeSnapshotIntoTimeline_GranularityChange_KeepsOlderPointsBeforeCurrentBucket()
    {
        // Points were stored with monthly granularity, now we use quarterly
        var existingPoints = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5_000_000_000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 6_000_000_000, CumulativeFileCount = 12 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 7_000_000_000, CumulativeFileCount = 14 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8_000_000_000, CumulativeFileCount = 16 },
        };

        // Now is in Q3 2024, using quarterly granularity
        var now = new DateTime(2024, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 10_000_000_000, 20, "quarterly");

        // Q3 2024 bucket starts at Jul 1 → all existing monthly points are before that
        Assert.Equal(5, result.Count);
        Assert.Equal(10_000_000_000L, result[4].CumulativeSize);
        Assert.Equal(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), result[4].Date);
    }

    [Fact]
    public void MergeSnapshotIntoTimeline_MultipleScansInSameBucket_UpdatesLatestValue()
    {
        // First scan in March added a point
        var existingPoints = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5_000_000_000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8_000_000_000, CumulativeFileCount = 15 },
        };

        // Second scan still in March → should replace March value
        var now = new DateTime(2024, 3, 25, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints, now, 9_000_000_000, 17, "monthly");

        Assert.Equal(2, result.Count);
        Assert.Equal(5_000_000_000L, result[0].CumulativeSize);
        Assert.Equal(9_000_000_000L, result[1].CumulativeSize);
        Assert.Equal(17, result[1].CumulativeFileCount);
    }

    // ── GetBucketStart ────────────────────────────────────────────────────

    [Fact]
    public void GetBucketStart_Monthly_ReturnsFirstOfMonth()
    {
        var date = new DateTime(2024, 3, 15, 14, 30, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.GetBucketStart(date, "monthly");
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void GetBucketStart_Quarterly_ReturnsFirstOfQuarter()
    {
        var date = new DateTime(2024, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.GetBucketStart(date, "quarterly");
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void GetBucketStart_Yearly_ReturnsFirstOfYear()
    {
        var date = new DateTime(2024, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.GetBucketStart(date, "yearly");
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void GetBucketStart_Daily_ReturnsSameDay()
    {
        var date = new DateTime(2024, 3, 15, 14, 30, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.GetBucketStart(date, "daily");
        Assert.Equal(new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc), result);
    }

    // ── TrimLeadingZeros ──────────────────────────────────────────────────

    [Fact]
    public void TrimLeadingZeros_EmptyList_ReturnsEmpty()
    {
        var points = new List<GrowthTimelinePoint>();
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        Assert.Empty(result);
    }

    [Fact]
    public void TrimLeadingZeros_AllZeros_ReturnsEmpty()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        Assert.Empty(result);
    }

    [Fact]
    public void TrimLeadingZeros_NoLeadingZeros_ReturnsAllPoints()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 3000 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        Assert.Equal(3, result.Count);
        Assert.Equal(1000L, result[0].CumulativeSize);
    }

    [Fact]
    public void TrimLeadingZeros_MultipleLeadingZeros_KeepsOneBeforeFirstNonZero()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000 },
            new() { Date = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8000 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);

        // Should keep: Mar (0 as baseline), Apr (5000), May (8000)
        Assert.Equal(3, result.Count);
        Assert.Equal(0L, result[0].CumulativeSize);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(5000L, result[1].CumulativeSize);
        Assert.Equal(8000L, result[2].CumulativeSize);
    }

    [Fact]
    public void TrimLeadingZeros_SingleLeadingZero_KeepsIt()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        Assert.Equal(2, result.Count);
        Assert.Equal(0L, result[0].CumulativeSize);
        Assert.Equal(5000L, result[1].CumulativeSize);
    }

    [Fact]
    public void TrimLeadingZeros_ZeroInMiddle_IsPreserved()
    {
        // Library rebuilt scenario: grew, deleted (0), rebuilt
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 8000 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 10000 },
            new() { Date = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 0 },   // library deleted
            new() { Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 3000 }, // rebuilt
            new() { Date = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 7000 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);

        // Should trim leading zeros but keep the mid-timeline zero
        Assert.Equal(6, result.Count);
        Assert.Equal(0L, result[0].CumulativeSize);      // Feb (one zero baseline before Mar)
        Assert.Equal(8000L, result[1].CumulativeSize);    // Mar
        Assert.Equal(10000L, result[2].CumulativeSize);   // Apr
        Assert.Equal(0L, result[3].CumulativeSize);       // May (mid-timeline zero preserved!)
        Assert.Equal(3000L, result[4].CumulativeSize);    // Jun
        Assert.Equal(7000L, result[5].CumulativeSize);    // Jul
    }

    [Fact]
    public void TrimLeadingZeros_SingleNonZeroPoint_ReturnsIt()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000 },
        };
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        Assert.Single(result);
        Assert.Equal(5000L, result[0].CumulativeSize);
    }

    // ── DeduplicateConsecutivePoints ───────────────────────────────────────

    [Fact]
    public void DeduplicateConsecutivePoints_EmptyList_ReturnsEmpty()
    {
        var points = new List<GrowthTimelinePoint>();
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);
        Assert.Empty(result);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_SinglePoint_ReturnsSame()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 5 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);
        Assert.Single(result);
        Assert.Equal(1000L, result[0].CumulativeSize);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_TwoPoints_ReturnsBoth()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 5 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_AllIdentical_KeepsFirstAndLast()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);

        // Only first and last should remain
        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_AllDifferent_KeepsAll()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 3000, CumulativeFileCount = 3 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 4000, CumulativeFileCount = 4 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_PlateauInMiddle_RemovesDuplicates()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 2000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 3000, CumulativeFileCount = 15 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);

        Assert.Equal(3, result.Count);
        Assert.Equal(1000L, result[0].CumulativeSize);
        Assert.Equal(2000L, result[1].CumulativeSize);
        Assert.Equal(3000L, result[2].CumulativeSize);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_FileCountChangesOnly_KeepsPoint()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 12 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 12 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);

        // Jan (10 files), Feb (12 files - change), Mar (last point always kept)
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void DeduplicateConsecutivePoints_LastPointAlwaysPreserved()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 5000, CumulativeFileCount = 10 },
        };
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);

        // First and last preserved
        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
    }

    // --- ConsolidateToGranularity Tests ---

    [Fact]
    public void ConsolidateToGranularity_DailyToWeekly_MergesSameWeekPoints()
    {
        // Mon 2024-01-01 to Sun 2024-01-07 are one ISO week
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 300, CumulativeFileCount = 3 },
            new() { Date = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 400, CumulativeFileCount = 4 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "weekly");

        // Jan 1-3 all in same week (Mon Jan 1), Jan 8 is next week (Mon Jan 8)
        Assert.Equal(2, result.Count);
        Assert.Equal(300L, result[0].CumulativeSize); // last point of first week
        Assert.Equal(400L, result[1].CumulativeSize); // second week
    }

    [Fact]
    public void ConsolidateToGranularity_DailyToMonthly_MergesSameMonthPoints()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 500, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2024, 1, 31, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1000, CumulativeFileCount = 10 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1100, CumulativeFileCount = 11 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "monthly");

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(1000L, result[0].CumulativeSize); // last Jan point
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
        Assert.Equal(1100L, result[1].CumulativeSize);
    }

    [Fact]
    public void ConsolidateToGranularity_SinglePoint_ReturnsUnchanged()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 999, CumulativeFileCount = 5 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "yearly");

        Assert.Single(result);
        Assert.Equal(999L, result[0].CumulativeSize);
    }

    [Fact]
    public void ConsolidateToGranularity_EmptyList_ReturnsEmpty()
    {
        var points = new List<GrowthTimelinePoint>();

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "monthly");

        Assert.Empty(result);
    }

    [Fact]
    public void ConsolidateToGranularity_MonthlyToQuarterly_MergesCorrectly()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 300, CumulativeFileCount = 3 },
            new() { Date = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 400, CumulativeFileCount = 4 },
            new() { Date = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 500, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 600, CumulativeFileCount = 6 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "quarterly");

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date); // Q1
        Assert.Equal(300L, result[0].CumulativeSize); // last Q1 point (March)
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date); // Q2
        Assert.Equal(600L, result[1].CumulativeSize); // last Q2 point (June)
    }

    [Fact]
    public void ConsolidateToGranularity_SameGranularity_NoChange()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 300, CumulativeFileCount = 3 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "monthly");

        // Points are already monthly-aligned, so no merging
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ConsolidateToGranularity_InvalidGranularity_FallsBackToMonthly()
    {
        // Unknown granularity strings should fall back to the default (monthly) bucket logic,
        // the same way GetBucketStart and AdvanceBucket handle unrecognised values.
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2024, 1, 28, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 200, CumulativeFileCount = 2 },
            new() { Date = new DateTime(2024, 2, 10, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 300, CumulativeFileCount = 3 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "biweekly");

        // "biweekly" is unknown → GetBucketStart falls back to monthly
        // Jan 15 and Jan 28 both map to Jan 1 bucket, Feb 10 maps to Feb 1 bucket
        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(200L, result[0].CumulativeSize); // last Jan point wins
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
        Assert.Equal(300L, result[1].CumulativeSize);
    }

    [Fact]
    public void ConsolidateToGranularity_EmptyString_FallsBackToMonthly()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 500, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 700, CumulativeFileCount = 7 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "");

        // Empty string → default branch → monthly bucket
        Assert.Single(result);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(700L, result[0].CumulativeSize);
    }

    [Fact]
    public void GetBucketStart_InvalidGranularity_FallsBackToMonthly()
    {
        var date = new DateTime(2024, 5, 20, 14, 30, 0, DateTimeKind.Utc);
        var result = GrowthTimelineService.GetBucketStart(date, "biweekly");
        // Unknown granularity falls back to monthly
        Assert.Equal(new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ConsolidateToGranularity_DailyToYearly_MergesSameYearPoints()
    {
        var points = new List<GrowthTimelinePoint>
        {
            new() { Date = new DateTime(2022, 6, 15, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 100, CumulativeFileCount = 1 },
            new() { Date = new DateTime(2022, 12, 31, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 500, CumulativeFileCount = 5 },
            new() { Date = new DateTime(2023, 3, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 800, CumulativeFileCount = 8 },
            new() { Date = new DateTime(2023, 9, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1200, CumulativeFileCount = 12 },
            new() { Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), CumulativeSize = 1500, CumulativeFileCount = 15 },
        };

        var result = GrowthTimelineService.ConsolidateToGranularity(points, "yearly");

        Assert.Equal(3, result.Count);
        Assert.Equal(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[0].Date);
        Assert.Equal(500L, result[0].CumulativeSize); // last 2022 point
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[1].Date);
        Assert.Equal(1200L, result[1].CumulativeSize); // last 2023 point
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result[2].Date);
        Assert.Equal(1500L, result[2].CumulativeSize);
    }
}
