using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class TranslationsControllerTests : IDisposable
{
    private readonly TranslationsController _controller;

    public TranslationsControllerTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        _controller = new TranslationsController();
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration();
    }

    public void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
    }

    [Fact]
    public void GetTranslations_ReturnsTranslations()
    {
        var result = _controller.GetTranslations("en");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var translations = Assert.IsType<Dictionary<string, string>>(okResult.Value);
        Assert.NotEmpty(translations);
    }

    [Fact]
    public void GetTranslations_DefaultsToConfigLanguage()
    {
        CleanupConfigHelper.ConfigOverride = new PluginConfiguration { Language = "de" };

        var result = _controller.GetTranslations(null);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var translations = Assert.IsType<Dictionary<string, string>>(okResult.Value);
        Assert.NotEmpty(translations);
        Assert.Equal("Einstellungen", translations["tabSettings"]);
    }
}
