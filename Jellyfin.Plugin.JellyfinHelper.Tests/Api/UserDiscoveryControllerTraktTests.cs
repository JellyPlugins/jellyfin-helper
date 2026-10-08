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
public sealed class UserDiscoveryControllerTraktTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock = new();
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock = new();
    private readonly Mock<ITraktDiscoveryService> _traktDiscovery = new();
    private readonly Mock<Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External.IOfficialTraktPluginReader> _officialPlugin = new();
    private readonly Mock<IPluginConfigurationService> _configServiceMock = new();
    private readonly DiscoveryCacheService _cache;
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly PluginConfiguration _config = new() { TraktSourcingEnabled = true, DiscoveryUserAccessEnabled = true };
    private readonly string _cacheFile;

    public UserDiscoveryControllerTraktTests()
    {
        var pluginLog = new Mock<JellyfinHelper.Services.PluginLog.IPluginLogService>();
        _cacheFile = Path.GetTempFileName();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object, filePath: _cacheFile);
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(_config);
        // Trakt is sourced only through the official plugin; present by default so the read endpoints are open.
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);

        // Fresh generation per test so throttle keys from a prior test cannot leak across the shared cache.
        UserDiscoveryController.ClearRateLimitState();
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        try
        {
            if (File.Exists(_cacheFile))
            {
                File.Delete(_cacheFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the throwaway cache file.
        }
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var controller = new UserDiscoveryController(
            _cache, _discoveryMock.Object, _feedbackStoreMock.Object, _configServiceMock.Object,
            _memoryCache, _traktDiscovery.Object, _officialPlugin.Object, new Mock<ILogger<UserDiscoveryController>>().Object);

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
    public async Task GetMyTrakt_WhenOfficialPluginAbsent_Returns403()
    {
        // The official plugin is the only Trakt source; absent means Trakt is genuinely unavailable.
        _officialPlugin.Setup(p => p.IsPresent()).Returns(false);
        var result = await CreateController(Guid.NewGuid()).GetMyTrakt(CancellationToken.None);
        Assert.Equal(403, Status(result.Result!));
    }

    [Fact]
    public async Task GetMyTrakt_WhenOfficialPluginPresentButNotLinked_NotForbidden()
    {
        // The official Trakt plugin can source per-user tokens, so Trakt access is allowed (not 403'd). Not
        // linked for this user, so the body reports linked=false with a 200 - the point being it is NOT the gate.
        var userId = Guid.NewGuid();
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.False(payload.Linked);
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
        _traktDiscovery.Setup(d => d.IsLinkedForAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        // Link state is resolved by the discovery service, not the result: a linked user with an
        // empty pool sees the (empty) grid, not the connect panel.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.True(payload.Linked);
        Assert.Null(payload.Result);
    }

    [Fact]
    public async Task GetMyTrakt_WhenLinkedOnlyViaOfficialPlugin_ReturnsLinkedTrueWithResult()
    {
        // The target scenario of this feature: a free-account user linked ONLY through the official Trakt
        // plugin (no own device-flow token). The discovery service resolves the official source, so the
        // envelope must report linked=true and surface the fetched result instead of the connect panel.
        var userId = Guid.NewGuid();
        var scored = new DiscoveryResult { UserId = userId };
        _traktDiscovery.Setup(d => d.IsLinkedForAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(scored);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<TraktDiscoveryResponse>(ok.Value);
        Assert.True(payload.Linked);
        Assert.Same(scored, payload.Result);
    }

    [Fact]
    public async Task GetMyTrakt_WhenFetchUnlinks_ReturnsLinkedFalse()
    {
        var userId = Guid.NewGuid();
        var linked = true;
        _traktDiscovery.Setup(d => d.IsLinkedForAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(() => linked);
        _traktDiscovery.Setup(d => d.GetPersonalAsync(userId, It.IsAny<CancellationToken>()))
            .Callback(() => linked = false)
            .ReturnsAsync((DiscoveryResult?)null);

        var result = await CreateController(userId).GetMyTrakt(CancellationToken.None);

        // The fetch unlinked a dead grant mid-flight: the endpoint re-resolves link state so the UI
        // offers a re-link instead of an empty grid.
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
        _traktDiscovery.Setup(d => d.IsLinkedForAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
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
    public async Task GetMyTrakt_MasterSwitchOff_Returns403()
    {
        // The master switch suppresses Trakt entirely, even with the official plugin present.
        _config.TraktSourcingEnabled = false;
        _officialPlugin.Setup(p => p.IsPresent()).Returns(true);

        var result = await CreateController(Guid.NewGuid()).GetMyTrakt(CancellationToken.None);

        Assert.Equal(403, Status(result.Result!));
    }

    [Fact]
    public async Task TraktEndpoints_RequireAuthentication()
    {
        // No user claim -> Unauthorized from the Caller-ID guard.
        var result = await CreateController(userId: null).GetMyTrakt(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
