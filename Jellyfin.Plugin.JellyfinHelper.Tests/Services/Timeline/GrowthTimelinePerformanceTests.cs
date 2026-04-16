using System.Diagnostics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Performance tests for GrowthTimelineService pure methods.
///     These tests verify that timeline computation stays within acceptable time bounds
///     even with large datasets. Run with: dotnet test --filter "Category=Performance"
/// </summary>
public class GrowthTimelinePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void BuildCumulativeTimeline_10000Entries_CompletesWithin2Seconds()
    {
        // Arrange: 10,000 file entries spread over 5 years
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddYears(-5);
        var entries = new List<GrowthTimelineService.FileEntry>();
        var random = new Random(42); // deterministic seed

        for (var i = 0; i < 10_000; i++)
        {
            var daysOffset = random.Next(0, (int)(now - earliest).TotalDays);
            entries.Add(new GrowthTimelineService.FileEntry
            {
                CreatedUtc = earliest.AddDays(daysOffset),
                Size = random.Next(1_000_000, int.MaxValue),
                CountDelta = 1
            });
        }

        entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        var granularity = GrowthTimelineService.DetermineGranularity(earliest, now);
        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, granularity);
        sw.Stop();

        // Assert
        output.WriteLine($"BuildCumulativeTimeline: {entries.Count} entries → {result.Count} points in {sw.ElapsedMilliseconds}ms (granularity: {granularity})");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
        Assert.NotEmpty(result);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void BuildCumulativeTimeline_50000Entries_DailyGranularity_CompletesWithin3Seconds()
    {
        // Arrange: 50,000 entries over 60 days (daily granularity)
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var earliest = now.AddDays(-60);
        var entries = new List<GrowthTimelineService.FileEntry>();
        var random = new Random(123);

        for (var i = 0; i < 50_000; i++)
        {
            entries.Add(new GrowthTimelineService.FileEntry
            {
                CreatedUtc = earliest.AddDays(random.Next(0, 60)),
                Size = random.Next(100_000, 500_000_000),
                CountDelta = 1
            });
        }

        entries.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.BuildCumulativeTimeline(entries, earliest, now, "daily");
        sw.Stop();

        // Assert
        output.WriteLine($"BuildCumulativeTimeline (daily): {entries.Count} entries → {result.Count} points in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Took {sw.ElapsedMilliseconds}ms, expected < 3000ms");
        Assert.NotEmpty(result);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MergeSnapshotIntoTimeline_5000ExistingPoints_CompletesWithin500ms()
    {
        // Arrange: 5,000 existing data points (simulating years of history)
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var existingPoints = new List<GrowthTimelinePoint>();

        for (var i = 0; i < 5_000; i++)
        {
            existingPoints.Add(new GrowthTimelinePoint
            {
                Date = now.AddDays(-5000 + i),
                CumulativeSize = i * 1_000_000_000L,
                CumulativeFileCount = i * 10
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.MergeSnapshotIntoTimeline(
            existingPoints,
            now,
            5_000_000_000_000L,
            50_000,
            "monthly");
        sw.Stop();

        // Assert
        output.WriteLine($"MergeSnapshotIntoTimeline: {existingPoints.Count} existing → {result.Count} points in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
        Assert.NotEmpty(result);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void BuildIncrementalEntries_10000Directories_CompletesWithin1Second()
    {
        // Arrange: 10,000 directories with baseline
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now.AddYears(-1) };
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>();
        var random = new Random(42);

        for (var i = 0; i < 10_000; i++)
        {
            var path = $"/media/movies/movie_{i:D5}";
            var createdUtc = now.AddDays(-random.Next(0, 365));
            var size = (long)random.Next(500_000_000, 2_000_000_000);

            currentDirs.Add(new GrowthTimelineService.DirectoryEntry
            {
                Path = path,
                CreatedUtc = createdUtc,
                Size = size,
                Count = 1
            });

            // 80% of directories are in the baseline (20% are new)
            if (i % 5 != 0)
            {
                baseline.Directories[path] = new BaselineDirectoryEntry
                {
                    CreatedUtc = createdUtc,
                    Size = size - random.Next(0, 100_000_000), // slightly different size
                    Count = 1
                };
            }
        }

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.BuildIncrementalEntries(currentDirs, baseline, now);
        sw.Stop();

        // Assert
        output.WriteLine($"BuildIncrementalEntries: {currentDirs.Count} dirs, {baseline.Directories.Count} baseline → {result.Count} entries in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
        Assert.NotEmpty(result);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ConsolidateToGranularity_5000Points_CompletesWithin500ms()
    {
        // Arrange: 5,000 daily points to consolidate into monthly
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<GrowthTimelinePoint>();

        for (var i = 0; i < 5_000; i++)
        {
            points.Add(new GrowthTimelinePoint
            {
                Date = now.AddDays(-5000 + i),
                CumulativeSize = i * 500_000_000L,
                CumulativeFileCount = i * 5
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.ConsolidateToGranularity(points, "monthly");
        sw.Stop();

        // Assert
        output.WriteLine($"ConsolidateToGranularity: {points.Count} daily → {result.Count} monthly in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
        Assert.True(result.Count < points.Count, "Consolidation should reduce point count");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void DeduplicateConsecutivePoints_10000Points_CompletesWithin200ms()
    {
        // Arrange: 10,000 points with many consecutive duplicates (typical for stable libraries)
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<GrowthTimelinePoint>();

        for (var i = 0; i < 10_000; i++)
        {
            // Create plateaus of ~50 identical points, then a jump
            var plateau = i / 50;
            points.Add(new GrowthTimelinePoint
            {
                Date = now.AddDays(-10_000 + i),
                CumulativeSize = plateau * 10_000_000_000L,
                CumulativeFileCount = plateau * 100
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.DeduplicateConsecutivePoints(points);
        sw.Stop();

        // Assert
        output.WriteLine($"DeduplicateConsecutivePoints: {points.Count} → {result.Count} points in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 200, $"Took {sw.ElapsedMilliseconds}ms, expected < 200ms");
        Assert.True(result.Count < points.Count, "Deduplication should reduce point count");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void TrimLeadingZeros_10000Points_CompletesWithin100ms()
    {
        // Arrange: 10,000 points, first 5,000 are zeros
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var points = new List<GrowthTimelinePoint>();

        for (var i = 0; i < 10_000; i++)
        {
            points.Add(new GrowthTimelinePoint
            {
                Date = now.AddDays(-10_000 + i),
                CumulativeSize = i >= 5_000 ? (i - 5_000) * 1_000_000_000L : 0,
                CumulativeFileCount = i >= 5_000 ? (i - 5_000) * 10 : 0
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        var result = GrowthTimelineService.TrimLeadingZeros(points);
        sw.Stop();

        // Assert
        output.WriteLine($"TrimLeadingZeros: {points.Count} → {result.Count} points in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 100, $"Took {sw.ElapsedMilliseconds}ms, expected < 100ms");
        Assert.True(result.Count < points.Count, "Trimming should reduce point count");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void UpdateBaseline_10000Directories_MixedAddUpdateRemove_CompletesWithin1Second()
    {
        // Arrange: baseline with 8,000 dirs, current scan with 10,000 dirs (6,000 overlap, 4,000 new, 2,000 removed)
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now.AddYears(-1) };
        var random = new Random(42);

        // Baseline: dirs 0..7999
        for (var i = 0; i < 8_000; i++)
        {
            baseline.Directories[$"/media/movies/movie_{i:D5}"] = new BaselineDirectoryEntry
            {
                CreatedUtc = now.AddDays(-random.Next(0, 365)),
                Size = random.Next(500_000_000, 2_000_000_000),
                Count = 1
            };
        }

        // Current scan: dirs 2000..11999 (overlap with 2000..7999, new 8000..11999, removed 0..1999)
        var currentDirs = new List<GrowthTimelineService.DirectoryEntry>();
        for (var i = 2_000; i < 12_000; i++)
        {
            currentDirs.Add(new GrowthTimelineService.DirectoryEntry
            {
                Path = $"/media/movies/movie_{i:D5}",
                CreatedUtc = now.AddDays(-random.Next(0, 365)),
                Size = random.Next(500_000_000, 2_000_000_000),
                Count = 1
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        GrowthTimelineService.UpdateBaseline(baseline, currentDirs);
        sw.Stop();

        // Assert
        output.WriteLine($"UpdateBaseline: 8,000 baseline + 10,000 current (6,000 overlap, 4,000 new, 2,000 removed) → {baseline.Directories.Count} dirs in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
        Assert.Equal(10_000, baseline.Directories.Count);

        // Verify removed dirs are gone
        Assert.False(baseline.Directories.ContainsKey("/media/movies/movie_00000"));
        Assert.False(baseline.Directories.ContainsKey("/media/movies/movie_01999"));

        // Verify new dirs are present
        Assert.True(baseline.Directories.ContainsKey("/media/movies/movie_08000"));
        Assert.True(baseline.Directories.ContainsKey("/media/movies/movie_11999"));
    }
}
