using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class DiscoveryControllerTests
{
    private static DiscoveryController CreateController(Mock<ISeerrDiscoveryService>? discovery = null)
    {
        var pluginLog = new Mock<IPluginLogService>();
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        var cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object);
        var disc = discovery ?? new Mock<ISeerrDiscoveryService>();
        return new DiscoveryController(cache, disc.Object);
    }

    [Fact]
    public void Get_ReturnsOkWithResults()
    {
        var controller = CreateController();
        var result = controller.GetDiscoveryResults();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_InvalidTmdbId_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 0, MediaType = "movie" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PostRequest_InvalidMediaType_ReturnsBadRequest()
    {
        var controller = CreateController();
        var dto = new DiscoveryRequestDto { TmdbId = 123, MediaType = "invalid" };
        var result = await controller.SubmitRequest(dto, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
