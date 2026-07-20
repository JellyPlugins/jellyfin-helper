using System.Reflection;
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
        Assert.NotNull(ok.Value);

        // Anonymous type - reflect out the properties so we don't rely on internal shape.
        var payload = ok.Value!;
        var okValue = payload.GetType().GetProperty("ok", BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload);
        var plugin = payload.GetType().GetProperty("plugin", BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload);
        var version = payload.GetType().GetProperty("version", BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload);

        Assert.Equal(true, okValue);
        Assert.Equal("JellyfinHelper", plugin);
        Assert.False(string.IsNullOrWhiteSpace(version as string));
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