using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class StatisticsCacheServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StatisticsCacheService _service;

    public StatisticsCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jfh-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(ap => ap.DataPath).Returns(_tempDir);

        _service = new StatisticsCacheService(
            appPaths.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StatisticsCacheService>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* cleanup best-effort */ }
    }

    [Fact]
    public void LoadLatestResult_ReturnsNull_WhenNoFile()
    {
        var result = _service.LoadLatestResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var stats = new MediaStatisticsResult();
        stats.Libraries.Add(new LibraryStatistics { VideoSize = 42, VideoFileCount = 3 });
        stats.Movies.Add(new LibraryStatistics { VideoSize = 100 });

        _service.SaveLatestResult(stats);
        var loaded = _service.LoadLatestResult();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Libraries);
        Assert.Equal(42, loaded.Libraries[0].VideoSize);
        Assert.Equal(3, loaded.Libraries[0].VideoFileCount);
        Assert.Single(loaded.Movies);
        Assert.Equal(100, loaded.Movies[0].VideoSize);
    }

    [Fact]
    public void SaveLatestResult_OverwritesPrevious()
    {
        var stats1 = new MediaStatisticsResult();
        stats1.Libraries.Add(new LibraryStatistics { VideoSize = 1 });
        _service.SaveLatestResult(stats1);

        var stats2 = new MediaStatisticsResult();
        stats2.Libraries.Add(new LibraryStatistics { VideoSize = 2 });
        _service.SaveLatestResult(stats2);

        var loaded = _service.LoadLatestResult();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Libraries[0].VideoSize);
    }

    [Fact]
    public void LoadLatestResult_ReturnsNull_WhenFileCorrupt()
    {
        var filePath = Path.Join(_tempDir, "jellyfin-helper-statistics-latest.json");
        File.WriteAllText(filePath, "NOT VALID JSON {{{{");

        var result = _service.LoadLatestResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveLatestResult_CreatesDirectoryIfMissing()
    {
        var nestedDir = Path.Combine(_tempDir, "nested", "deep");
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(ap => ap.DataPath).Returns(nestedDir);

        var service = new StatisticsCacheService(
            appPaths.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<StatisticsCacheService>().Object);

        var stats = new MediaStatisticsResult();
        service.SaveLatestResult(stats);

        Assert.True(Directory.Exists(nestedDir));
    }
}