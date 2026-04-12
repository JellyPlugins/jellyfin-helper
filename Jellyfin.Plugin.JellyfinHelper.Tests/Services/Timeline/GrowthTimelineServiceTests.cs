using System;
using System.Collections.Generic;
using System.Linq;
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
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
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
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000 },
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
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 10_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 7_000_000_000 },
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
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var newDirCreated = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
            new() { Path = "/movies/MovieB", CreatedUtc = newDirCreated, Size = 5_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Baseline entry + new directory entry (no diff for MovieA since unchanged)
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
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };
        baseline.Directories["/movies/MovieB"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
        };

        // MovieB was deleted
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // 2 baseline entries + 1 negative entry for deleted MovieB
        Assert.Equal(3, entries.Count);

        var negativeEntry = entries.First(e => e.Size == -5_000_000_000);
        Assert.Equal(now, negativeEntry.CreatedUtc);
    }

    [Fact]
    public void BuildIncrementalEntries_CombinedScenario_CorrectTotalSize()
    {
        // Baseline: MovieA (8GB, created 2024-06), MovieB (5GB, created 2024-09)
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };
        baseline.Directories["/movies/MovieB"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
        };

        // Current: MovieA grew to 10GB, MovieB unchanged, MovieC is new (3GB, created 2025-03)
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000 },
            new() { Path = "/movies/MovieB", CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc), Size = 5_000_000_000 },
            new() { Path = "/movies/MovieC", CreatedUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 3_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Total should be: 8GB (baseline A) + 5GB (baseline B) + 2GB (diff A) + 3GB (new C) = 18GB
        var totalSize = entries.Sum(e => e.Size);
        Assert.Equal(18_000_000_000L, totalSize);
    }

    [Fact]
    public void BuildIncrementalEntries_UserScenario_Created2024_Changed2025()
    {
        // User's exact scenario: folder created 2024 at 8GB, now 10GB in 2025
        // Expected: 8GB at 2024, +2GB at 2025
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/library/Show1"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/library/Show1", CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        // Should have 2 entries: baseline (8GB at 2024-03) + diff (2GB at 2025-06)
        Assert.Equal(2, entries.Count);

        var baselineEntry = entries.First(e => e.CreatedUtc.Year == 2024);
        var diffEntry = entries.First(e => e.CreatedUtc.Year == 2025);

        Assert.Equal(8_000_000_000L, baselineEntry.Size);
        Assert.Equal(2_000_000_000L, diffEntry.Size);
    }

    // ── UpdateBaseline ────────────────────────────────────────────────────

    [Fact]
    public void UpdateBaseline_NewDirectory_AddedToBaseline()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
            new() { Path = "/movies/MovieB", CreatedUtc = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 5_000_000_000 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(2, baseline.Directories.Count);
        Assert.True(baseline.Directories.ContainsKey("/movies/MovieB"));
        Assert.Equal(5_000_000_000L, baseline.Directories["/movies/MovieB"].Size);
    }

    [Fact]
    public void UpdateBaseline_ExistingDirectory_SizeUpdated()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(10_000_000_000L, baseline.Directories["/movies/MovieA"].Size);
        // Creation date should NOT change
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), baseline.Directories["/movies/MovieA"].CreatedUtc);
    }

    [Fact]
    public void UpdateBaseline_DeletedDirectory_RemovedFromBaseline()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };
        baseline.Directories["/movies/MovieB"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
        };

        // MovieB was deleted
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Single(baseline.Directories);
        Assert.False(baseline.Directories.ContainsKey("/movies/MovieB"));
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
            new() { Path = "/movies/MovieA", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
        };

        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);

        Assert.Equal(originalTimestamp, baseline.FirstScanTimestamp);
    }

    // ── End-to-end: BuildIncrementalEntries → BuildCumulativeTimeline ────

    [Fact]
    public void EndToEnd_UserScenario_TimelineShowsCorrectGrowth()
    {
        // Scenario: Library with 1 show created 2024-03 (8GB at baseline)
        // By 2025-06 it grew to 10GB → graph should show 8GB from 2024 and +2GB in 2025
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/library/Show1"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/library/Show1", CreatedUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), Size = 10_000_000_000 },
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
        // Baseline: MovieA 8GB (created 2024-01), MovieB 5GB (created 2024-06)
        // Current: MovieA unchanged, MovieB grew to 7GB, MovieC new 3GB (created 2025-02)
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/movies/A"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };
        baseline.Directories["/movies/B"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            Size = 5_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/A", CreatedUtc = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
            new() { Path = "/movies/B", CreatedUtc = new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc), Size = 7_000_000_000 },
            new() { Path = "/movies/C", CreatedUtc = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc), Size = 3_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);
        entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        var earliest = entries[0].CreatedUtc;
        var points = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "monthly");

        // Total should be: 8 + 5 + 2 (diff B) + 3 (new C) = 18GB
        var lastPoint = points.Last();
        Assert.Equal(18_000_000_000L, lastPoint.CumulativeSize);

        // Jan 2024: MovieA = 8GB
        var jan2024 = points.First(p => p.Date == new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(8_000_000_000L, jan2024.CumulativeSize);

        // Jun 2024: MovieA + MovieB = 13GB
        var jun2024 = points.First(p => p.Date == new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(13_000_000_000L, jun2024.CumulativeSize);

        // Feb 2025: + MovieC 3GB = 16GB
        var feb2025 = points.First(p => p.Date == new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(16_000_000_000L, feb2025.CumulativeSize);
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

    // ── Path comparison ───────────────────────────────────────────────────

    [Fact]
    public void BuildIncrementalEntries_PathComparison_IsOsAware()
    {
        var baseline = new GrowthTimelineBaseline
        {
            FirstScanTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        baseline.Directories["/Movies/MovieA"] = new BaselineDirectoryEntry
        {
            CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Size = 8_000_000_000,
        };

        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>
        {
            new() { Path = "/movies/moviea", CreatedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), Size = 8_000_000_000 },
        };

        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entries = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);

        if (OperatingSystem.IsWindows())
        {
            // On Windows, paths are case-insensitive → should match (no duplicate, no diff)
            Assert.Single(entries);
        }
        else
        {
            // On Linux/macOS, paths are case-sensitive → treated as different directories
            Assert.Equal(2, entries.Count);
        }
    }
}
