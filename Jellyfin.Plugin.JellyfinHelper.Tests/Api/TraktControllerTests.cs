using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Verifies the admin-only Trakt connection test: empty input is rejected, a valid client id returns 200,
///     and a rejected client id surfaces a 502 with the service's message. The service is mocked, so no network
///     is touched and the client-id-only semantics live entirely in <see cref="TraktAuthService"/>.
/// </summary>
public class TraktControllerTests
{
    private readonly Mock<ITraktAuthService> _authService = new();
    private readonly Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader> _officialPlugin = new();
    private readonly TraktController _controller;

    public TraktControllerTests()
    {
        _controller = new TraktController(
            _authService.Object,
            _officialPlugin.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<TraktController>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task TestConnection_ReturnsBadRequest_WhenRequestIsNull()
    {
        var result = await _controller.TestConnection(null!);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestConnection_ReturnsBadRequest_WhenClientIdBlank(string clientId)
    {
        var result = await _controller.TestConnection(new TraktTestRequest { ClientId = clientId });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TestConnection_ReturnsOk_WhenClientIdValid()
    {
        _authService
            .Setup(a => a.TestClientIdAsync("client-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Trakt Client ID is valid."));

        var result = await _controller.TestConnection(new TraktTestRequest { ClientId = "client-id" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ConnectionTestResponse>(ok.Value);
        Assert.True(body.Success);
    }

    [Fact]
    public async Task TestConnection_TrimsClientId_BeforeTesting()
    {
        _authService
            .Setup(a => a.TestClientIdAsync("client-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "ok"));

        await _controller.TestConnection(new TraktTestRequest { ClientId = "  client-id  " });

        _authService.Verify(a => a.TestClientIdAsync("client-id", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_Returns502_WhenClientIdRejected()
    {
        _authService
            .Setup(a => a.TestClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Trakt rejected this Client ID. Verify it against your Trakt application."));

        var result = await _controller.TestConnection(new TraktTestRequest { ClientId = "bad-id" });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        var body = Assert.IsType<ConnectionTestResponse>(obj.Value);
        Assert.False(body.Success);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetOfficialPluginStatus_ReflectsReaderPresence(bool present)
    {
        _officialPlugin.Setup(p => p.IsPresent()).Returns(present);

        var result = _controller.GetOfficialPluginStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<OfficialTraktPluginStatusResponse>(ok.Value);
        Assert.Equal(present, body.Present);
    }
}
