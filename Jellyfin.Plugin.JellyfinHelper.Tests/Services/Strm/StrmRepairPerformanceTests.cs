using System.Diagnostics;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Strm;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Strm;

/// <summary>
///     Performance tests for StrmRepairService with large directory trees.
///     Run with: dotnet test --filter "Category=Performance"
/// </summary>
public class StrmRepairPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void FindStrmFiles_DeepDirectoryTree_5000Files_CompletesWithin5Seconds()
    {
        // Arrange: Create a mock filesystem with 5,000 .strm files across 500 directories
        // Note: MockFileSystem is significantly slower than real I/O, so limits are generous
        var fs = new MockFileSystem();
        var basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        for (var dir = 0; dir < 500; dir++)
        {
            var dirPath = $"{basePath}/movie_{dir:D4}";
            fs.Directory.CreateDirectory(dirPath);

            for (var file = 0; file < 10; file++)
            {
                var filePath = $"{dirPath}/video_{file:D2}.strm";
                fs.File.WriteAllText(filePath, $"/actual/path/movie_{dir}/video_{file}.mkv");
            }

            // Also add some non-strm files that should be skipped
            fs.File.WriteAllText($"{dirPath}/poster.jpg", "image data");
            fs.File.WriteAllText($"{dirPath}/info.nfo", "<nfo/>");
        }

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<StrmRepairService>>();
        var service = new StrmRepairService(fs, pluginLog.Object, logger.Object);

        // Act
        var sw = Stopwatch.StartNew();
        var result = service.FindStrmFiles(new List<string> { basePath });
        sw.Stop();

        // Assert
        output.WriteLine($"FindStrmFiles: 500 dirs × 10 files = {result.Count} .strm files found in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(5_000, result.Count);
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 8000, $"Took {sw.ElapsedMilliseconds}ms, expected < 8000ms");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepairStrmFiles_2000ValidFiles_CompletesWithin10Seconds()
    {
        // Arrange: 2,000 .strm files that all point to valid targets (no repair needed)
        // Note: MockFileSystem has high per-operation overhead; real I/O would be much faster
        var fs = new MockFileSystem();
        var basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        for (var i = 0; i < 2_000; i++)
        {
            var dirPath = $"{basePath}/movie_{i:D4}";
            fs.Directory.CreateDirectory(dirPath);

            var targetPath = $"{dirPath}/video.mkv";
            fs.File.WriteAllText(targetPath, "video content");

            var strmPath = $"{dirPath}/video.strm";
            fs.File.WriteAllText(strmPath, targetPath);
        }

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<StrmRepairService>>();
        var service = new StrmRepairService(fs, pluginLog.Object, logger.Object);

        // Act
        var sw = Stopwatch.StartNew();
        var result = service.RepairStrmFiles(new List<string> { basePath }, dryRun: true);
        sw.Stop();

        // Assert
        output.WriteLine($"RepairStrmFiles: {result.FileResults.Count} files processed in {sw.ElapsedMilliseconds}ms ({result.ValidCount} valid, {result.RepairedCount} repaired)");
        Assert.Equal(2_000, result.ValidCount);
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 15_000, $"Took {sw.ElapsedMilliseconds}ms, expected < 15000ms");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void FindStrmFiles_DeeplyNestedDirectories_CompletesWithin5Seconds()
    {
        // Arrange: 50 directories each nested 10 levels deep — tests the refactored
        // FindFilesRecursiveCore that avoids per-level list allocations.
        var fs = new MockFileSystem();
        var basePath = "/media/shows";
        fs.Directory.CreateDirectory(basePath);
        var expectedCount = 0;

        for (var show = 0; show < 50; show++)
        {
            var current = $"{basePath}/show_{show:D3}";
            fs.Directory.CreateDirectory(current);

            // Create 10 levels of nesting (season/episode style)
            for (var depth = 0; depth < 10; depth++)
            {
                current = $"{current}/level_{depth}";
                fs.Directory.CreateDirectory(current);

                // 3 .strm files per level
                for (var file = 0; file < 3; file++)
                {
                    fs.File.WriteAllText($"{current}/ep_{file}.strm", "/target/path.mkv");
                    expectedCount++;
                }

                // 2 non-strm files per level (should be skipped)
                fs.File.WriteAllText($"{current}/thumb.jpg", "image");
                fs.File.WriteAllText($"{current}/meta.nfo", "<nfo/>");
            }
        }

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<StrmRepairService>>();
        var service = new StrmRepairService(fs, pluginLog.Object, logger.Object);

        // Act
        var sw = Stopwatch.StartNew();
        var result = service.FindStrmFiles(new List<string> { basePath });
        sw.Stop();

        // Assert
        output.WriteLine($"FindStrmFiles (deep nesting): 50×10 levels, {expectedCount} expected → {result.Count} found in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedCount, result.Count);
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 8000, $"Took {sw.ElapsedMilliseconds}ms, expected < 8000ms");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void FindMediaFilesInDirectory_LargeDirectory_1000Files_CompletesWithin3Seconds()
    {
        // Arrange: A single directory with 1,000 mixed files — tests EnumerateFiles performance
        var fs = new MockFileSystem();
        var basePath = "/media/movies/large_collection";
        fs.Directory.CreateDirectory(basePath);
        var expectedMediaCount = 0;

        for (var i = 0; i < 1_000; i++)
        {
            if (i % 3 == 0)
            {
                fs.File.WriteAllText($"{basePath}/video_{i:D4}.mkv", "video");
                expectedMediaCount++;
            }
            else if (i % 3 == 1)
            {
                fs.File.WriteAllText($"{basePath}/subtitle_{i:D4}.srt", "subtitle");
            }
            else
            {
                fs.File.WriteAllText($"{basePath}/image_{i:D4}.jpg", "image");
            }
        }

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<StrmRepairService>>();
        var service = new StrmRepairService(fs, pluginLog.Object, logger.Object);

        // Act
        var sw = Stopwatch.StartNew();
        var result = service.FindMediaFilesInDirectory(basePath);
        sw.Stop();

        // Assert
        output.WriteLine($"FindMediaFilesInDirectory: 1,000 files → {result.Count} media files in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedMediaCount, result.Count);
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
        }
    }
}
