using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class SeerrControllerTests
{
    private readonly Mock<ISeerrIntegrationService> _seerrService;
    private readonly SeerrController _controller;

    public SeerrControllerTests()
    {
        _seerrService = new Mock<ISeerrIntegrationService>();

        _controller = new SeerrController(
            _seerrService.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<SeerrController>().Object);

        // Set up a default HttpContext so HttpContext.RequestAborted is available
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
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
        var payload = ParsePayload(okResult);
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Connected", payload.GetProperty("message").GetString());
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
        var payload = ParsePayload(objectResult);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Auth failed", payload.GetProperty("message").GetString());
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
        var payload = ParsePayload(objectResult);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("Connection failed", payload.GetProperty("message").GetString());
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
        var payload = ParsePayload(objectResult);
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("timed out", payload.GetProperty("message").GetString());
    }

    /// <summary>
    ///     Serializes the anonymous-type payload of an <see cref="OkObjectResult"/> into a <see cref="JsonElement"/>
    ///     so individual properties can be asserted without reflection or dynamic.
    /// </summary>
    private static JsonElement ParsePayload(OkObjectResult okResult)
    {
        var json = JsonSerializer.Serialize(okResult.Value);
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement ParsePayload(ObjectResult result)
    {
        var json = JsonSerializer.Serialize(result.Value);
        return JsonDocument.Parse(json).RootElement;
    }
}
