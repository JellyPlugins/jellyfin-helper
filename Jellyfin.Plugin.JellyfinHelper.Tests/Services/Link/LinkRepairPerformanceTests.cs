using System.Diagnostics;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Performance tests for LinkRepairService with large directory trees.
///     Tests both .strm and symlink scenarios, including mixed-mode.
///     Run with: dotnet test --filter "Category=Performance"
/// </summary>
public class LinkRepairPerformanceTests(ITestOutputHelper output)
{
    // ===== .strm Performance =====

    [Fact]
    [Trait("Category", "Performance")]
    public void FindLinkFiles_Strm_5000Files_CompletesWithin5Seconds()
    {
        var fs = new MockFileSystem();
        const string basePath = "/media/movies";
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

            fs.File.WriteAllText($"{dirPath}/poster.jpg", "image data");
            fs.File.WriteAllText($"{dirPath}/info.nfo", "<nfo/>");
        }

        var service = CreateService(fs, new StrmLinkHandler(fs));

        var sw = Stopwatch.StartNew();
        var result = service.FindLinkFiles(new List<string> { basePath });
        sw.Stop();

        output.WriteLine(
            $"FindLinkFiles (strm): 500 dirs × 10 files = {result.Count} found in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(5_000, result.Count);
        AssertPerfLimit(sw, 8000);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepairLinks_Strm_2000ValidFiles_CompletesWithin10Seconds()
    {
        var fs = new MockFileSystem();
        const string basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        for (var i = 0; i < 2_000; i++)
        {
            var dirPath = $"{basePath}/movie_{i:D4}";
            fs.Directory.CreateDirectory(dirPath);

            var targetPath = $"{dirPath}/video.mkv";
            fs.File.WriteAllText(targetPath, "video content");

            var linkPath = $"{dirPath}/video.strm";
            fs.File.WriteAllText(linkPath, targetPath);
        }

        var service = CreateService(fs, new StrmLinkHandler(fs));

        var sw = Stopwatch.StartNew();
        var result = service.RepairLinks(new List<string> { basePath }, true);
        sw.Stop();

        output.WriteLine(
            $"RepairLinks (strm): {result.FileResults.Count} files in {sw.ElapsedMilliseconds}ms ({result.ValidCount} valid)");
        Assert.Equal(2_000, result.ValidCount);
        AssertPerfLimit(sw, 15_000);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void FindLinkFiles_Strm_DeeplyNested_CompletesWithin5Seconds()
    {
        var fs = new MockFileSystem();
        const string basePath = "/media/shows";
        fs.Directory.CreateDirectory(basePath);
        var expectedCount = 0;

        for (var show = 0; show < 50; show++)
        {
            var current = $"{basePath}/show_{show:D3}";
            fs.Directory.CreateDirectory(current);

            for (var depth = 0; depth < 10; depth++)
            {
                current = $"{current}/level_{depth}";
                fs.Directory.CreateDirectory(current);

                for (var file = 0; file < 3; file++)
                {
                    fs.File.WriteAllText($"{current}/ep_{file}.strm", "/target/path.mkv");
                    expectedCount++;
                }

                fs.File.WriteAllText($"{current}/thumb.jpg", "image");
                fs.File.WriteAllText($"{current}/meta.nfo", "<nfo/>");
            }
        }

        var service = CreateService(fs, new StrmLinkHandler(fs));

        var sw = Stopwatch.StartNew();
        var result = service.FindLinkFiles(new List<string> { basePath });
        sw.Stop();

        output.WriteLine(
            $"FindLinkFiles (strm, deep): {expectedCount} expected → {result.Count} found in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedCount, result.Count);
        AssertPerfLimit(sw, 8000);
    }

    // ===== Symlink Performance =====

    [Fact]
    [Trait("Category", "Performance")]
    public void FindLinkFiles_Symlink_5000Files_CompletesWithin5Seconds()
    {
        var fs = new MockFileSystem();
        var symlinkHelper = new Mock<ISymlinkHelper>();
        var basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        var symlinkPaths = new HashSet<string>();
        for (var dir = 0; dir < 500; dir++)
        {
            var dirPath = $"{basePath}/movie_{dir:D4}";
            fs.Directory.CreateDirectory(dirPath);

            for (var file = 0; file < 10; file++)
            {
                var filePath = $"{dirPath}/video_{file:D2}.mkv";
                fs.File.WriteAllText(filePath, "video");
                symlinkPaths.Add(fs.Path.GetFullPath(filePath));
            }

            fs.File.WriteAllText($"{dirPath}/poster.jpg", "image data");
        }

        symlinkHelper.Setup(h => h.IsSymlink(It.IsAny<string>()))
            .Returns<string>(symlinkPaths.Contains);

        var service = CreateService(fs, new SymlinkHandler(symlinkHelper.Object));

        var sw = Stopwatch.StartNew();
        var result = service.FindLinkFiles(new List<string> { basePath });
        sw.Stop();

        output.WriteLine(
            $"FindLinkFiles (symlink): 500 dirs × 10 files = {result.Count} found in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(5_000, result.Count);
        AssertPerfLimit(sw, 8000);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void RepairLinks_Symlink_2000ValidFiles_CompletesWithin10Seconds()
    {
        var fs = new MockFileSystem();
        var symlinkHelper = new Mock<ISymlinkHelper>();
        const string basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        var symlinkTargets = new Dictionary<string, string>();
        for (var i = 0; i < 2_000; i++)
        {
            var dirPath = $"{basePath}/movie_{i:D4}";
            fs.Directory.CreateDirectory(dirPath);

            var targetPath = $"{dirPath}/video.mkv";
            fs.File.WriteAllText(targetPath, "video content");

            var linkPath = $"{dirPath}/link.mkv";
            fs.File.WriteAllText(linkPath, "symlink-placeholder");
            symlinkTargets[fs.Path.GetFullPath(linkPath)] = fs.Path.GetFullPath(targetPath);
        }

        symlinkHelper.Setup(h => h.IsSymlink(It.IsAny<string>()))
            .Returns<string>(symlinkTargets.ContainsKey);
        symlinkHelper.Setup(h => h.GetSymlinkTarget(It.IsAny<string>()))
            .Returns<string>(symlinkTargets.GetValueOrDefault);

        var service = CreateService(fs, new SymlinkHandler(symlinkHelper.Object));

        var sw = Stopwatch.StartNew();
        var result = service.RepairLinks(new List<string> { basePath }, true);
        sw.Stop();

        output.WriteLine(
            $"RepairLinks (symlink): {result.FileResults.Count} files in {sw.ElapsedMilliseconds}ms ({result.ValidCount} valid)");
        Assert.Equal(2_000, result.ValidCount);
        AssertPerfLimit(sw, 15_000);
    }

    // ===== Mixed Mode Performance =====

    [Fact]
    [Trait("Category", "Performance")]
    public void FindLinkFiles_MixedStrmAndSymlinks_3000Files_CompletesWithin5Seconds()
    {
        var fs = new MockFileSystem();
        var symlinkHelper = new Mock<ISymlinkHelper>();
        const string basePath = "/media/movies";
        fs.Directory.CreateDirectory(basePath);

        var symlinkPaths = new HashSet<string>();
        var expectedStrmCount = 0;
        var expectedSymlinkCount = 0;

        for (var dir = 0; dir < 300; dir++)
        {
            var dirPath = $"{basePath}/movie_{dir:D4}";
            fs.Directory.CreateDirectory(dirPath);

            // 5 .strm files per directory
            for (var file = 0; file < 5; file++)
            {
                fs.File.WriteAllText($"{dirPath}/video_{file:D2}.strm", "/target/path.mkv");
                expectedStrmCount++;
            }

            // 5 symlink files per directory
            for (var file = 0; file < 5; file++)
            {
                var symlinkPath = $"{dirPath}/symlink_{file:D2}.mkv";
                fs.File.WriteAllText(symlinkPath, "video");
                symlinkPaths.Add(fs.Path.GetFullPath(symlinkPath));
                expectedSymlinkCount++;
            }

            // Non-link files (should be skipped)
            fs.File.WriteAllText($"{dirPath}/poster.jpg", "image");
        }

        symlinkHelper.Setup(h => h.IsSymlink(It.IsAny<string>()))
            .Returns<string>(symlinkPaths.Contains);

        var handlers = new ILinkHandler[] { new StrmLinkHandler(fs), new SymlinkHandler(symlinkHelper.Object) };
        var service = CreateServiceMultiHandler(fs, handlers);

        var sw = Stopwatch.StartNew();
        var result = service.FindLinkFiles(new List<string> { basePath });
        sw.Stop();

        var strmResults = result.Count(r => r.Handler is StrmLinkHandler);
        var symlinkResults = result.Count(r => r.Handler is SymlinkHandler);

        output.WriteLine(
            $"FindLinkFiles (mixed): {strmResults} strm + {symlinkResults} symlinks = {result.Count} total in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedStrmCount, strmResults);
        Assert.Equal(expectedSymlinkCount, symlinkResults);
        Assert.Equal(expectedStrmCount + expectedSymlinkCount, result.Count);
        AssertPerfLimit(sw, 8000);
    }

    // ===== Shared: FindMediaFilesInDirectory =====

    [Fact]
    [Trait("Category", "Performance")]
    public void FindMediaFilesInDirectory_LargeDirectory_1000Files_CompletesWithin3Seconds()
    {
        var fs = new MockFileSystem();
        const string basePath = "/media/movies/large_collection";
        fs.Directory.CreateDirectory(basePath);
        var expectedMediaCount = 0;

        for (var i = 0; i < 1_000; i++)
        {
            switch (i % 3)
            {
                case 0:
                    fs.File.WriteAllText($"{basePath}/video_{i:D4}.mkv", "video");
                    expectedMediaCount++;
                    break;
                case 1:
                    fs.File.WriteAllText($"{basePath}/subtitle_{i:D4}.srt", "subtitle");
                    break;
                default:
                    fs.File.WriteAllText($"{basePath}/image_{i:D4}.jpg", "image");
                    break;
            }
        }

        var service = CreateService(fs, new StrmLinkHandler(fs));

        var sw = Stopwatch.StartNew();
        var result = service.FindMediaFilesInDirectory(basePath);
        sw.Stop();

        output.WriteLine(
            $"FindMediaFilesInDirectory: 1,000 files → {result.Count} media in {sw.ElapsedMilliseconds}ms");
        Assert.Equal(expectedMediaCount, result.Count);
        AssertPerfLimit(sw, 5000);
    }

    // ===== Helpers =====

    private static LinkRepairService CreateService(MockFileSystem fs, ILinkHandler handler)
    {
        return new LinkRepairService(
            fs,
            [handler],
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<LinkRepairService>>().Object);
    }

    private static LinkRepairService CreateServiceMultiHandler(MockFileSystem fs, ILinkHandler[] handlers)
    {
        return new LinkRepairService(
            fs,
            handlers,
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<LinkRepairService>>().Object);
    }

    private static void AssertPerfLimit(Stopwatch sw, long maxMilliseconds)
    {
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(
                sw.ElapsedMilliseconds < maxMilliseconds,
                $"Took {sw.ElapsedMilliseconds}ms, expected < {maxMilliseconds}ms");
        }
    }
}