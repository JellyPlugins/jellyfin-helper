using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class ConfigurationControllerTests : IDisposable
{
    private readonly ConfigurationController _controller;

    public ConfigurationControllerTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        var loggerMock = new Mock<ILogger<ConfigurationController>>();
        _controller = new ConfigurationController(loggerMock.Object);
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration();
    }

    public void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
    }

    [Fact]
    public void GetConfiguration_ReturnsCurrentConfig()
    {
        var result = _controller.GetConfiguration();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var config = Assert.IsType<PluginConfiguration>(okResult.Value);
        Assert.NotNull(config);
    }

    [Fact]
    public void UpdateConfiguration_ValidConfig_ReturnsOk()
    {
        var newConfig = new PluginConfiguration
        {
            OrphanMinAgeDays = 5,
            TrashRetentionDays = 10
        };

        var result = _controller.UpdateConfiguration(newConfig);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(5, Plugin.Instance!.Configuration.OrphanMinAgeDays);
        Assert.Equal(10, Plugin.Instance.Configuration.TrashRetentionDays);
    }

    [Fact]
    public void UpdateConfiguration_InvalidOrphanAge_ReturnsBadRequest()
    {
        var newConfig = new PluginConfiguration { OrphanMinAgeDays = -1 };

        var result = _controller.UpdateConfiguration(newConfig);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void UpdateConfiguration_InvalidTrashRetention_ReturnsBadRequest()
    {
        var newConfig = new PluginConfiguration { TrashRetentionDays = -1 };

        var result = _controller.UpdateConfiguration(newConfig);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
