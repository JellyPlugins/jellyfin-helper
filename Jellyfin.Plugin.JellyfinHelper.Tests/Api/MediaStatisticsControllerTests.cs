using System;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class MediaStatisticsControllerTests : IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly Mock<IMediaStatisticsService> _statsService;
    private readonly Mock<IStatisticsCacheService> _cacheService;
    private readonly MediaStatisticsController _controller;

    public MediaStatisticsControllerTests()
    {
        // Reset the static rate-limit timestamp so tests are not affected by previous runs
        var field = typeof(MediaStatisticsController).GetField("_lastScanTime", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, DateTime.MinValue);

        _cache = TestMockFactory.CreateMemoryCache();
        _statsService = TestMockFactory.CreateMediaStatisticsService();
        _cacheService = TestMockFactory.CreateStatisticsCacheService();

        _controller = new MediaStatisticsController(
            _cache,
            _statsService.Object,
            _cacheService.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<MediaStatisticsController>().Object);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    [Fact]
    public void ScanLibraries_ReturnsOk_WithStatistics()
    {
        var expected = new MediaStatisticsResult();
        expected.Libraries.Add(new LibraryStatistics { VideoFileCount = 5 });
        _statsService.Setup(s => s.CalculateStatistics()).Returns(expected);

        var result = _controller.ScanLibraries();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var stats = Assert.IsType<MediaStatisticsResult>(okResult.Value);
        Assert.Single(stats.Libraries);
    }

    [Fact]
    public void ScanLibraries_SavesResultToCache()
    {
        var expected = new MediaStatisticsResult();
        _statsService.Setup(s => s.CalculateStatistics()).Returns(expected);

        _controller.ScanLibraries();

        _cacheService.Verify(c => c.SaveLatestResult(It.IsAny<MediaStatisticsResult>()), Times.Once);
    }

    [Fact]
    public void GetLatestStatistics_ReturnsNoContent_WhenNothingCached()
    {
        _cacheService.Setup(c => c.LoadLatestResult()).Returns((MediaStatisticsResult?)null);

        var result = _controller.GetLatestStatistics();

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public void GetLatestStatistics_ReturnsPersistedResult_WhenNotInMemoryCache()
    {
        var persisted = new MediaStatisticsResult();
        persisted.Libraries.Add(new LibraryStatistics { VideoSize = 999 });
        _cacheService.Setup(c => c.LoadLatestResult()).Returns(persisted);

        var result = _controller.GetLatestStatistics();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var stats = Assert.IsType<MediaStatisticsResult>(okResult.Value);
        Assert.Equal(999, stats.Libraries[0].VideoSize);
    }

    [Fact]
    public void GetLatestStatistics_ReturnsCachedResult_WhenInMemoryCache()
    {
        var cached = new MediaStatisticsResult();
        cached.Libraries.Add(new LibraryStatistics { VideoSize = 42 });
        _cache.Set("JellyfinHelper_Statistics", cached, TimeSpan.FromMinutes(5));

        var result = _controller.GetLatestStatistics();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var stats = Assert.IsType<MediaStatisticsResult>(okResult.Value);
        Assert.Equal(42, stats.Libraries[0].VideoSize);
    }
}