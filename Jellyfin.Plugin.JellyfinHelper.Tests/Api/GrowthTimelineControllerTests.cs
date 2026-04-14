using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class GrowthTimelineControllerTests
{
    private readonly GrowthTimelineController _controller;
    private readonly Mock<GrowthTimelineService> _serviceMock;

    public GrowthTimelineControllerTests()
    {
        var appPaths = TestMockFactory.CreateAppPaths();
        _serviceMock = TestMockFactory.CreateGrowthTimelineService(appPaths.Object);
        _controller = new GrowthTimelineController(_serviceMock.Object);
    }

    [Fact]
    public async Task GetGrowthTimelineAsync_ReturnsTimeline()
    {
        var expected = new GrowthTimelineResult { Granularity = "Monthly" };
        _serviceMock.Setup(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<GrowthTimelineResult>(okResult.Value);
        Assert.Equal("Monthly", data.Granularity);
    }

    [Fact]
    public async Task GetGrowthTimelineAsync_ReturnsCachedTimeline()
    {
        var cached = new GrowthTimelineResult { Granularity = "Daily" };
        _serviceMock.Setup(s => s.LoadTimelineAsync(It.IsAny<CancellationToken>())).ReturnsAsync(cached);

        var result = await _controller.GetGrowthTimelineAsync(forceRefresh: false, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<GrowthTimelineResult>(okResult.Value);
        Assert.Equal("Daily", data.Granularity);
        _serviceMock.Verify(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
