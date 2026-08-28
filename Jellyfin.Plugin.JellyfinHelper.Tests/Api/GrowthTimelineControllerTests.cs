using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class GrowthTimelineControllerTests
{
    private readonly GrowthTimelineController _controller;
    private readonly Mock<IGrowthTimelineService> _serviceMock;

    public GrowthTimelineControllerTests()
    {
        _serviceMock = TestMockFactory.CreateGrowthTimelineService();
        _controller = new GrowthTimelineController(_serviceMock.Object);
    }

    /// <summary>
    ///     Resets the private static _lastRefreshTime field to DateTime.MinValue so that the rate-limit window is clear before the test begins.
    /// </summary>
    private static void ResetRateLimitState()
    {
        var field = typeof(GrowthTimelineController)
            .GetField("_lastRefreshTime", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find _lastRefreshTime field via reflection.");
        field.SetValue(null, DateTime.MinValue);
    }

    [Fact]
    public async Task GetGrowthTimelineAsync_ReturnsTimeline()
    {
        ResetRateLimitState();
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

    [Fact]
    public async Task GetGrowthTimeline_ForceRefresh_CalledTwiceWithinWindow_Returns429()
    {
        // Arrange: clear any leftover rate-limit state from other tests.
        ResetRateLimitState();

        var timeline = new GrowthTimelineResult { Granularity = "Weekly" };
        _serviceMock
            .Setup(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(timeline);

        // Act: first call - should succeed because the window is clear.
        var firstResult = await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);

        // Act: second call immediately - still within the 30-second rate-limit window.
        var secondResult = await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);

        // Assert first call returns 200 OK.
        Assert.IsType<OkObjectResult>(firstResult.Result);

        // Assert second call returns 429 Too Many Requests.
        var tooManyRequests = Assert.IsType<ObjectResult>(secondResult.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooManyRequests.StatusCode);
    }

    [Fact]
    public async Task GetGrowthTimeline_ForceRefresh_WithinWindow_SetsRetryAfterHeader()
    {
        ResetRateLimitState();

        // Response.Headers is written on the throttled path, so the controller needs an HttpContext.
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        _serviceMock
            .Setup(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GrowthTimelineResult { Granularity = "Weekly" });

        // Prime _lastRefreshTime, then hit the rate-limit branch immediately.
        await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);
        var throttled = await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(throttled.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);

        // Retry-After must communicate the remaining window: a positive integer no larger than 30s.
        Assert.True(_controller.Response.Headers.ContainsKey("Retry-After"));
        var retryAfter = int.Parse(_controller.Response.Headers["Retry-After"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 30);
    }

    [Fact]
    public async Task GetGrowthTimeline_ForceRefresh_ComputeThrows_RethrowsAndRestoresRefreshTime()
    {
        ResetRateLimitState();

        _serviceMock
            .Setup(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compute failed"));

        // The failing compute must surface to the caller.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None));

        // A failed attempt must not consume the rate-limit window: the catch block rolls _lastRefreshTime
        // back to its previous value, so an immediate retry succeeds instead of returning 429.
        var success = new GrowthTimelineResult { Granularity = "Monthly" };
        _serviceMock
            .Setup(s => s.ComputeTimelineAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(success);

        var result = await _controller.GetGrowthTimelineAsync(forceRefresh: true, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(success, okResult.Value);
    }
}
