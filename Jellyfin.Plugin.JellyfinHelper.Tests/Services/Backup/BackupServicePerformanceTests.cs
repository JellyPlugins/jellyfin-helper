using System.Diagnostics;
using Jellyfin.Plugin.JellyfinHelper.Services.Backup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Backup;

/// <summary>
///     Performance tests for BackupService static methods (Sanitize, Validate).
///     Run with: dotnet test --filter "Category=Performance"
/// </summary>
public class BackupServicePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Sanitize_LargeTimeline_6000DataPoints_TrimsToMaxAndCompletesWithin500ms()
    {
        // Arrange: BackupData with 6,000 timeline data points (exceeds MaxTimelineDataPoints = 5000)
        var backup = CreateLargeBackup(timelinePoints: 6_000, baselineDirs: 100, arrInstances: 5);

        // Act
        var sw = Stopwatch.StartNew();
        BackupSanitizer.Sanitize(backup);
        sw.Stop();

        // Assert
        output.WriteLine($"Sanitize: 6,000 timeline points \u2192 {backup.GrowthTimeline?.DataPoints.Count} in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
        var growthTimeline = backup.GrowthTimeline;
        Assert.NotNull(growthTimeline);
        Assert.True(growthTimeline.DataPoints.Count <= 5000,
            $"Expected \u2264 5000 data points after sanitize, got {growthTimeline.DataPoints.Count}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Sanitize_LargeBaseline_5000Directories_CompletesWithin500ms()
    {
        // Arrange: BackupData with 5,000 baseline directories (within MaxBaselineDirectories = 50,000)
        var backup = CreateLargeBackup(timelinePoints: 100, baselineDirs: 5_000, arrInstances: 5);

        // Act
        var sw = Stopwatch.StartNew();
        BackupSanitizer.Sanitize(backup);
        sw.Stop();

        // Assert: 5,000 < 50,000 limit, so no trimming expected - all directories preserved
        output.WriteLine($"Sanitize: 5,000 baseline dirs \u2192 {backup.GrowthBaseline?.Directories.Count} in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
        var growthBaseline = backup.GrowthBaseline;
        Assert.NotNull(growthBaseline);
        Assert.Equal(5_000, growthBaseline.Directories.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Validate_LargeBackup_CompletesWithin500ms()
    {
        // Arrange: Large but valid backup
        var backup = CreateLargeBackup(timelinePoints: 3_000, baselineDirs: 3_000, arrInstances: 10);

        // Act
        var sw = Stopwatch.StartNew();
        var result = BackupValidator.Validate(backup);
        sw.Stop();

        // Assert
        output.WriteLine($"Validate: {result.Errors.Count} errors, {result.Warnings.Count} warnings in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Sanitize_RepeatedCalls_StablePerformance()
    {
        // Arrange: Run Sanitize multiple times to check for performance degradation
        var times = new List<long>();

        // Act: Run 50 iterations
        for (var i = 0; i < 50; i++)
        {
            // Re-create backup each time since Sanitize modifies it
            var backup = CreateLargeBackup(timelinePoints: 3_000, baselineDirs: 2_000, arrInstances: 5);
            var sw = Stopwatch.StartNew();
            BackupSanitizer.Sanitize(backup);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        // Assert
        var avg = 0L;
        var max = 0L;
        foreach (var t in times)
        {
            avg += t;
            if (t > max)
            {
                max = t;
            }
        }

        avg /= times.Count;
        output.WriteLine($"Sanitize (50 iterations): avg={avg}ms, max={max}ms");
        Assert.True(max < 1000, $"Max iteration took {max}ms, expected < 1000ms");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Sanitize_OversizedBaseline_55000Directories_TrimsToMaxAndCompletesWithin2Seconds()
    {
        // Arrange: BackupData with 55,000 baseline directories (exceeds MaxBaselineDirectories = 50,000)
        var backup = CreateLargeBackup(timelinePoints: 100, baselineDirs: 55_000, arrInstances: 2);

        // Act
        var sw = Stopwatch.StartNew();
        BackupSanitizer.Sanitize(backup);
        sw.Stop();

        // Assert
        output.WriteLine($"Sanitize: 55,000 baseline dirs \u2192 {backup.GrowthBaseline?.Directories.Count} in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Took {sw.ElapsedMilliseconds}ms, expected < 2000ms");
        var growthBaseline = backup.GrowthBaseline;
        Assert.NotNull(growthBaseline);
        Assert.True(growthBaseline.Directories.Count <= 50_000,
            $"Expected \u2264 50,000 baseline dirs after sanitize, got {growthBaseline.Directories.Count}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Sanitize_MaxSizeTimeline_5000Points_NoTrimming_CompletesWithin500ms()
    {
        // Arrange: BackupData with exactly MaxTimelineDataPoints (5,000) - no trimming expected Threshold: 500ms accounts for validation overhead (path sanitization, Arr instance checks) and CI runner variance.
        var backup = CreateLargeBackup(timelinePoints: 5_000, baselineDirs: 100, arrInstances: 2);

        // Act
        var sw = Stopwatch.StartNew();
        BackupSanitizer.Sanitize(backup);
        sw.Stop();

        // Assert
        output.WriteLine($"Sanitize: 5,000 timeline points (at limit) \u2192 {backup.GrowthTimeline?.DataPoints.Count} in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Took {sw.ElapsedMilliseconds}ms, expected < 500ms");
        var growthTimeline = backup.GrowthTimeline;
        Assert.NotNull(growthTimeline);
        Assert.Equal(5_000, growthTimeline.DataPoints.Count);
    }

    private static BackupData CreateLargeBackup(int timelinePoints, int baselineDirs, int arrInstances)
    {
        var now = DateTime.UtcNow;

        var timeline = new GrowthTimelineResult
        {
            ComputedAt = now,
            Granularity = "daily",
            EarliestFileDate = now.AddDays(-timelinePoints)
        };
        for (var i = 0; i < timelinePoints; i++)
        {
            timeline.DataPoints.Add(new GrowthTimelinePoint
            {
                Date = now.AddDays(-timelinePoints + i),
                CumulativeSize = i * 1_000_000_000L,
                CumulativeFileCount = i * 10
            });
        }

        var baseline = new GrowthTimelineBaseline { FirstScanTimestamp = now.AddYears(-1) };
        for (var i = 0; i < baselineDirs; i++)
        {
            baseline.Directories[$"/media/library/item_{i:D5}"] = new BaselineDirectoryEntry
            {
                CreatedUtc = now.AddDays(-i),
                Size = 1_000_000_000L + i,
                Count = 1
            };
        }

        var radarrInstances = new List<BackupArrInstance>();
        var sonarrInstances = new List<BackupArrInstance>();
        for (var i = 0; i < arrInstances; i++)
        {
            radarrInstances.Add(new BackupArrInstance
            {
                Name = $"Radarr_{i}",
                Url = $"http://radarr-{i}:7878",
                ApiKey = $"api-key-radarr-{i}"
            });
            sonarrInstances.Add(new BackupArrInstance
            {
                Name = $"Sonarr_{i}",
                Url = $"http://sonarr-{i}:8989",
                ApiKey = $"api-key-sonarr-{i}"
            });
        }

        return new BackupData
        {
            BackupVersion = 1,
            CreatedAt = now,
            Language = "en",
            ExcludedLibraries = string.Join(",", GenerateStrings("Exclude", 20)),
            TrashFolderPath = ".jellyfin-trash",
            OrphanMinAgeDays = 14,
            TrickplayTaskMode = "DryRun",
            EmptyMediaFolderTaskMode = "DryRun",
            OrphanedSubtitleTaskMode = "DryRun",
            LinkRepairTaskMode = "DryRun",
            SeerrCleanupTaskMode = "Deactivate",
            TrashRetentionDays = 30,
            RadarrInstances = radarrInstances,
            SonarrInstances = sonarrInstances,
            GrowthTimeline = timeline,
            GrowthBaseline = baseline
        };
    }

    private static List<string> GenerateStrings(string prefix, int count)
    {
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add($"{prefix}_{i:D3}");
        }

        return result;
    }
}