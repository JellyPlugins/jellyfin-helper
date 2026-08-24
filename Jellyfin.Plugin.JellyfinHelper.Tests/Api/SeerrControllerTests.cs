using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class SeerrControllerTests
{
    private const string ApiKeyMask = "********";

    private readonly Mock<ISeerrIntegrationService> _seerrService;
    private readonly SeerrController _controller;

    public SeerrControllerTests()
    {
        _seerrService = new Mock<ISeerrIntegrationService>();
        _controller = CreateController(new PluginConfiguration());
    }

    /// <summary>
    ///     Builds a controller wired to a config helper returning the supplied configuration.
    ///     The shared <see cref="_seerrService"/> mock is reused so setups/verifications apply.
    /// </summary>
    private SeerrController CreateController(PluginConfiguration config)
    {
        var controller = new SeerrController(
            _seerrService.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<SeerrController>().Object,
            TestMockFactory.CreateCleanupConfigHelper(config).Object);

        // Set up a default HttpContext so HttpContext.RequestAborted is available
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenRequestIsNull()
    {
        var result = await _controller.TestConnection(null!);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenUrlIsEmpty()
    {
        var request = new SeerrTestRequest { Url = "", ApiKey = "key" };
        var result = await _controller.TestConnection(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenApiKeyIsEmpty()
    {
        var request = new SeerrTestRequest { Url = "http://example.com", ApiKey = "" };
        var result = await _controller.TestConnection(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenUrlIsNotHttp()
    {
        var request = new SeerrTestRequest { Url = "ftp://example.com", ApiKey = "key" };
        var result = await _controller.TestConnection(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenUrlIsInvalid()
    {
        var request = new SeerrTestRequest { Url = "not-a-url", ApiKey = "key" };
        var result = await _controller.TestConnection(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("http://169.254.169.254")]
    [InlineData("http://metadata.google.internal")]
    [InlineData("http://100.100.100.200")]
    public async Task TestConnection_ReturnsBadRequest_ForCloudMetadataHost(string url)
    {
        // SSRF guard at the controller: metadata endpoints are rejected before any network call.
        var request = new SeerrTestRequest { Url = url, ApiKey = "key" };
        var result = await _controller.TestConnection(request);

        Assert.IsType<BadRequestObjectResult>(result);
        _seerrService.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestConnection_ReturnsOk_WhenConnectionSucceeds()
    {
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Connected"));

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = "abc123" };
        var result = await _controller.TestConnection(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        var payload = Assert.IsType<ConnectionTestResponse>(okResult.Value);
        Assert.True(payload.Success);
        Assert.Equal("Connected", payload.Message);
    }

    [Fact]
    public async Task TestConnection_ReturnsOk_WhenConnectionFails()
    {
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Auth failed"));

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = "bad" };
        var result = await _controller.TestConnection(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var payload = Assert.IsType<ConnectionTestResponse>(objectResult.Value);
        Assert.False(payload.Success);
        // The detailed upstream reason ("Auth failed") is logged server-side but MUST NOT be
        // reflected to the client: reflecting the raw upstream status/reason turns this endpoint
        // into an internal-reachability oracle. A generic failure message is returned instead.
        Assert.Equal("Connection failed. Please verify URL and API Key and try again.", payload.Message);
        Assert.DoesNotContain("Auth failed", payload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_ReturnsOk_WhenHttpRequestExceptionThrown()
    {
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = "abc" };
        var result = await _controller.TestConnection(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        var payload = Assert.IsType<ConnectionTestResponse>(objectResult.Value);
        Assert.False(payload.Success);
        Assert.Contains("Connection failed", payload.Message);
    }

    [Fact]
    public async Task TestConnection_ReturnsOk_WhenTimeoutOccurs()
    {
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = "abc" };
        var result = await _controller.TestConnection(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, objectResult.StatusCode);
        var payload = Assert.IsType<ConnectionTestResponse>(objectResult.Value);
        Assert.False(payload.Success);
        Assert.Contains("timed out", payload.Message);
    }

    // ---------- Masked-key sentinel resolution ----------

    [Fact]
    public async Task TestConnection_MaskWithMatchingStoredUrl_UsesRealStoredKey()
    {
        // Arrange: a stored Seerr instance whose real key the client must NOT know.
        const string realKey = "real-stored-seerr-key";
        var config = new PluginConfiguration { SeerrUrl = "http://seerr.local", SeerrApiKey = realKey };
        var controller = CreateController(config);

        string? sentKey = null;
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, key, _) => sentKey = key)
            .ReturnsAsync((true, "Connected"));

        // Client echoes back the mask (unchanged stored key).
        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = ApiKeyMask };
        var result = await controller.TestConnection(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<ConnectionTestResponse>(okResult.Value).Success);
        // The REAL stored key was probed upstream, never the mask.
        Assert.Equal(realKey, sentKey);
        Assert.NotEqual(ApiKeyMask, sentKey);
    }

    [Fact]
    public async Task TestConnection_MaskWithUnknownUrl_ReturnsFailureAndNeverSendsMask()
    {
        // Stored URL differs from the request URL, the mask cannot borrow the stored key.
        var config = new PluginConfiguration { SeerrUrl = "http://other.local", SeerrApiKey = "real-key" };
        var controller = CreateController(config);

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = ApiKeyMask };
        var result = await controller.TestConnection(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        Assert.False(Assert.IsType<ConnectionTestResponse>(objectResult.Value).Success);
        // The upstream must never be probed with the masked sentinel.
        _seerrService.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestConnection_MaskWithNoStoredKey_ReturnsFailureAndNeverSendsMask()
    {
        // URL matches but there is no stored key at all, cannot resolve, must not send the mask.
        var config = new PluginConfiguration { SeerrUrl = "http://seerr.local", SeerrApiKey = string.Empty };
        var controller = CreateController(config);

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = ApiKeyMask };
        var result = await controller.TestConnection(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, objectResult.StatusCode);
        _seerrService.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestConnection_RealKey_IsPassedThroughUnchanged()
    {
        // A non-mask value is a new key the admin is entering; it must be tested as-is even if a
        // different key is stored (this is how a key is changed).
        var config = new PluginConfiguration { SeerrUrl = "http://seerr.local", SeerrApiKey = "old-stored-key" };
        var controller = CreateController(config);

        string? sentKey = null;
        _seerrService
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, key, _) => sentKey = key)
            .ReturnsAsync((true, "Connected"));

        var request = new SeerrTestRequest { Url = "http://seerr.local", ApiKey = "brand-new-key" };
        var result = await controller.TestConnection(request);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("brand-new-key", sentKey);
    }
}
