using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for the dedicated Codecs-tab card order endpoint and the GET response mapping. Mirrors the
///     mocked-service setup of ConfigurationControllerTests so no Plugin.Instance singleton is required.
/// </summary>
public class ConfigurationControllerCodecCardOrderTests : IDisposable
{
    private readonly PluginConfiguration _config;
    private readonly Mock<IPluginConfigurationService> _configServiceMock;
    private readonly ConfigurationController _controller;
    private readonly EnsembleScoringStrategy _ensemble = new();

    public ConfigurationControllerCodecCardOrderTests()
    {
        _config = new PluginConfiguration();
        _configServiceMock = new Mock<IPluginConfigurationService>();
        _configServiceMock.Setup(s => s.IsInitialized).Returns(true);
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(_config);
        TestMockFactory.SetupReadAndMutate(_configServiceMock, _config);

        var arrServiceMock = new Mock<IArrIntegrationService>();
        var seerrServiceMock = new Mock<ISeerrIntegrationService>();
        var configHelperMock = new Mock<ICleanupConfigHelper>();
        configHelperMock.Setup(h => h.GetConfig()).Returns(_config);

        _controller = new ConfigurationController(
            arrServiceMock.Object,
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<ConfigurationController>>().Object,
            configHelperMock.Object,
            _configServiceMock.Object,
            seerrServiceMock.Object,
            new Mock<MediaBrowser.Controller.Library.ILibraryManager>().Object,
            _ensemble,
            TestMockFactory.CreateSecretProtector());
    }

    public void Dispose()
    {
        _ensemble.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetConfiguration_IncludesNormalizedCodecCardOrder()
    {
        // An empty stored order must surface as the full canonical set (11 entries) so the UI always
        // has a complete layout to render.
        var result = _controller.GetConfiguration();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ConfigurationResponse>(ok.Value);

        Assert.Equal(CodecCardCatalog.Canonical, response.CodecCardOrder);
        Assert.Equal(11, response.CodecCardOrder.Count);
    }

    [Fact]
    public void UpdateCodecCardOrder_NullBody_ReturnsBadRequest()
    {
        var result = _controller.UpdateCodecCardOrder(null!);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<CodecCardOrderResponse>(bad.Value);
        Assert.Contains("required", response.Message, StringComparison.OrdinalIgnoreCase);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void UpdateCodecCardOrder_NotInitialized_ReturnsBadRequest()
    {
        _configServiceMock.Setup(s => s.IsInitialized).Returns(false);
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = ["containers"] };

        var result = _controller.UpdateCodecCardOrder(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void UpdateCodecCardOrder_ValidPermutation_StoresNormalizedOrderAndReturnsOk()
    {
        var permutation = CodecCardCatalog.Canonical.Reverse().ToList();
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = permutation };

        var result = _controller.UpdateCodecCardOrder(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CodecCardOrderResponse>(ok.Value);
        Assert.Equal(permutation, response.CodecCardOrder);
        Assert.Equal(permutation, _config.CodecCardOrder);
    }

    [Fact]
    public void UpdateCodecCardOrder_UnknownId_ReturnsBadRequest()
    {
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = ["containers", "notACard"] };

        var result = _controller.UpdateCodecCardOrder(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void UpdateCodecCardOrder_DuplicateId_ReturnsBadRequest()
    {
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = ["containers", "containers"] };

        var result = _controller.UpdateCodecCardOrder(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void UpdateCodecCardOrder_TooManyEntries_ReturnsBadRequest()
    {
        var order = Enumerable.Range(0, 33).Select(i => $"card{i}").ToList();
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = order };

        var result = _controller.UpdateCodecCardOrder(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Never);
    }

    [Fact]
    public void UpdateCodecCardOrder_EmptyList_ReturnsOkWithCanonicalOrder()
    {
        // An empty list is valid and means "use the canonical layout"; the stored and returned order
        // is the full canonical set.
        var request = new CodecCardOrderUpdateRequest { CodecCardOrder = [] };

        var result = _controller.UpdateCodecCardOrder(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CodecCardOrderResponse>(ok.Value);
        Assert.Equal(CodecCardCatalog.Canonical, response.CodecCardOrder);
        Assert.Equal(CodecCardCatalog.Canonical, _config.CodecCardOrder);
    }
}
