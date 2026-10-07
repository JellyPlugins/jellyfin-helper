using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests the Trakt endpoints on UserDiscoveryController: the TraktEnabled 403 gate, the linked/not-linked
///     envelope, device start/poll/disconnect flow, poll status to HTTP mapping (410 on expired), and the
///     per-user start/poll throttles that return 429.
/// </summary>
[Collection("ConfigOverride")]
public sealed class UserDiscoveryControllerTraktTests
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock = new();
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock = new();
    private readonly Mock<ITraktAuthService> _traktAuth = new();
    private readonly Mock<ITraktDiscoveryService> _traktDiscovery = new();
    private readonly Mock<ITraktUserStore> _traktStore = new();
    private readonly Mock<IPluginConfigurationService> _configServiceMock = new();
    private readonly DiscoveryCacheService _cache;
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly PluginConfiguration _config = new() { TraktEnabled = true, DiscoveryUserAccessEnabled = true };

    public UserDiscoveryControllerTraktTests()
    {
        var pluginLog = new Mock<JellyfinHelper.Services.PluginLog.IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: Path.GetTempFileName());
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(_config);

        // Fresh generation per test so throttle keys from a prior test cannot leak across the shared cache.
        UserDiscoveryController.ClearRateLimitState();
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var controller = new UserDiscoveryController(
            _cache, _discoveryMock.Object, _feedbackStoreMock.Object, _configServiceMock.Object,
            _memoryCache, _traktAuth.Object, _traktDiscovery.Object, _traktStore.Object, new Mock<ILogger<UserDiscoveryController>>().Object);

        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    private static int Status(ActionResult result) => Assert.IsType<ObjectResult>(result, exactMatch: false).StatusCode ?? 0;

    [Fact]
    public async Task GetMyTrakt_WhenTraktDisabled_Returns403()
    {
        _config.TraktEnabled = false;
        var result = await CreateController(Guid.NewGuid()).GetMyTrakt(CancellationToken.None);
        Assert.Equal(403, Status(result.Result!));
    }

    [Fact]
    public async Task GetMyTrakt_WhenDiscoveryAccessDisabled_Returns403()
    {
        // Trakt tabs are a feature of the Discovery sidebar, so the user-level discovery-access gate
        // applies even when Trakt itself is configured.
        _config.DiscoveryUserAccessEnabled = false;
        var result = await CreateController(Guid.NewGuid()).GetMyTrakt(CancellationToken.None);
        Assert.Equal(403, Status(result.Result!));
    }

    [Fact]
    public async Task GetMyTrakt_WhenNotLinked_ReturnsLinkedFalse()
    {
        var userId = Guid.NewGuid();
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.False(payload.Linked);
        Assert.Null(payload.Result);
    }

    [Fact]
    public async Task GetMyTrakt_WhenLinkedButEmpty_ReturnsLinkedTrueWithNullResult()
    {
        var userId = Guid.NewGuid();
        _traktStore.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "r" });
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        // Link state comes from the store, not the result: a linked user with an
        // empty pool sees the (empty) grid, not the connect panel.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.True(payload.Linked);
        Assert.Null(payload.Result);
    }

    [Fact]
    public async Task GetMyTrakt_WhenFetchUnlinks_ReturnsLinkedFalse()
    {
        var userId = Guid.NewGuid();
        _traktStore.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "r" });
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>()))
            .Callback(() => _traktStore.Setup(s => s.GetToken(userId)).Returns((TraktUserToken?)null))
            .ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        // The fetch unlinked a dead grant mid-flight: the endpoint reports the current
        // state so the UI offers a re-link instead of an empty grid.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.False(payload.Linked);
        Assert.Null(payload.Result);
    }

    [Fact]
    public async Task GetMyTrakt_WhenLinked_ReturnsResult()
    {
        var userId = Guid.NewGuid();
        var scored = new DiscoveryResult { UserId = userId };
        _traktStore.Setup(s => s.GetToken(userId)).Returns(new TraktUserToken { AccessToken = "a", RefreshToken = "r" });
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(scored);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.True(payload.Linked);
        Assert.Same(scored, payload.Result);
    }

    [Fact]
    public async Task GetMyTraktTrending_ReturnsResult()
    {
        var userId = Guid.NewGuid();
        var scored = new DiscoveryResult { UserId = userId };
        _traktDiscovery.Setup(d => d.GetTrendingAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(scored);

        var result = await CreateController(userId).GetMyTraktTrending(CancellationToken.None);

        Assert.Same(scored, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task StartTraktDevice_ReturnsDeviceCode()
    {
        var device = new TraktDeviceCodeResponse { DeviceCode = "dev", UserCode = "ABCD", VerificationUrl = "https://trakt.tv/activate", ExpiresIn = 600, Interval = 5 };
        _traktAuth.Setup(a => a.StartDeviceAuthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(device);

        var result = await CreateController(Guid.NewGuid()).StartTraktDevice(CancellationToken.None);

        Assert.Same(device, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task StartTraktDevice_SecondCallWithinWindow_Returns429()
    {
        var userId = Guid.NewGuid();
        _traktAuth.Setup(a => a.StartDeviceAuthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TraktDeviceCodeResponse { DeviceCode = "dev" });

        var controller = CreateController(userId);
        await controller.StartTraktDevice(CancellationToken.None);
        var second = await CreateController(userId).StartTraktDevice(CancellationToken.None);

        Assert.Equal(429, Status(second));
    }

    [Fact]
    public async Task PollTraktDevice_Pending_Returns200WithStatus()
    {
        var userId = Guid.NewGuid();
        _traktAuth.Setup(a => a.PollDeviceAuthAsync(userId, "dev", It.IsAny<CancellationToken>())).ReturnsAsync(TraktDevicePollStatus.Pending);

        var result = await CreateController(userId).PollTraktDevice(new TraktDevicePollRequest { DeviceCode = "dev" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Pending", Assert.IsType<TraktDevicePollResponse>(ok.Value).Status);
    }

    [Fact]
    public async Task PollTraktDevice_Expired_Returns410()
    {
        var userId = Guid.NewGuid();
        _traktAuth.Setup(a => a.PollDeviceAuthAsync(userId, "dev", It.IsAny<CancellationToken>())).ReturnsAsync(TraktDevicePollStatus.Expired);

        var result = await CreateController(userId).PollTraktDevice(new TraktDevicePollRequest { DeviceCode = "dev" }, CancellationToken.None);

        Assert.Equal(410, Status(result));
    }

    [Fact]
    public async Task PollTraktDevice_SecondCallWithinWindow_Returns429()
    {
        var userId = Guid.NewGuid();
        _traktAuth.Setup(a => a.PollDeviceAuthAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(TraktDevicePollStatus.Pending);

        await CreateController(userId).PollTraktDevice(new TraktDevicePollRequest { DeviceCode = "dev" }, CancellationToken.None);
        var second = await CreateController(userId).PollTraktDevice(new TraktDevicePollRequest { DeviceCode = "dev" }, CancellationToken.None);

        Assert.Equal(429, Status(second));
    }

    [Fact]
    public async Task Disconnect_RemovesLinkAndReturnsSuccess()
    {
        var userId = Guid.NewGuid();

        var result = await CreateController(userId).DisconnectTrakt(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(Assert.IsType<RequestResult>(ok.Value).Success);
        _traktAuth.Verify(a => a.DisconnectAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TraktEndpoints_RequireAuthentication()
    {
        // No user claim -> Unauthorized from the Caller-ID guard.
        var result = await CreateController(userId: null).GetMyTrakt(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
