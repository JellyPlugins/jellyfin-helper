using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class CleanupStatisticsControllerTests
{
    private readonly CleanupStatisticsController _controller;

    public CleanupStatisticsControllerTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        _controller = new CleanupStatisticsController();
    }

    [Fact]
    public void GetCleanupStatistics_ReturnsOk()
    {
        var result = _controller.GetCleanupStatistics();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var payloadJson = JsonSerializer.Serialize(okResult.Value);
        Assert.Contains("TotalBytesFreed", payloadJson);
        Assert.Contains("TotalItemsDeleted", payloadJson);
        Assert.Contains("LastCleanupTimestamp", payloadJson);
    }
}
