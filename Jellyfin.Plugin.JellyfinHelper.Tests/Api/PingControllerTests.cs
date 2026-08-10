using Jellyfin.Plugin.JellyfinHelper.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="PingController" />. The controller is intentionally
///     dependency-free so the tests focus on the response shape only.
/// </summary>
public class PingControllerTests
{
    [Fact]
    public void GetPing_ReturnsOkWithExpectedShape()
    {
        var controller = new PingController();

        var result = controller.GetPing();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<PingResponse>(ok.Value);
        Assert.True(payload.Ok);
        Assert.Equal("JellyfinHelper", payload.Plugin);
        Assert.False(string.IsNullOrWhiteSpace(payload.Version));
    }

    [Fact]
    public void GetPing_IsIdempotent()
    {
        var controller = new PingController();

        // Two consecutive calls must return equivalent payloads so the client
        // can use Ping as a stateless liveness probe without side effects.
        var first = Assert.IsType<OkObjectResult>(controller.GetPing());
        var second = Assert.IsType<OkObjectResult>(controller.GetPing());

        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.Equal(first.Value!.ToString(), second.Value!.ToString());
    }
}