using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="UserDiscoveryController"/> with the access gate ENABLED.
///     Complements <see cref="UserDiscoveryControllerTests"/> by exercising validation,
///     permission and happy-path branches unreachable when the gate is disabled.
///     Belongs to the shared <c>ConfigOverride</c> collection.
/// </summary>
[Collection("ConfigOverride")]
public sealed class UserDiscoveryControllerAccessEnabledTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<ILogger<UserDiscoveryController>> _loggerMock;

    public UserDiscoveryControllerAccessEnabledTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        ControllerTestFactory.ResetPluginConfiguration();
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = true;

        var pluginLog = new Mock<IPluginLogService>();
        _cache = new DiscoveryCacheService(pluginLog.Object, new Mock<ILogger<DiscoveryCacheService>>().Object);
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
        _feedbackStoreMock.Setup(f => f.GetDismissedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());
        _feedbackStoreMock.Setup(f => f.GetRequestedItems(It.IsAny<Guid>()))
            .Returns(new HashSet<(int, string)>());
        _loggerMock = new Mock<ILogger<UserDiscoveryController>>();
    }

    public void Dispose()
    {
        ControllerTestFactory.ResetPluginConfiguration();
        _cache.Dispose();
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var controller = new UserDiscoveryController(
            _cache, _discoveryMock.Object, _feedbackStoreMock.Object, _loggerMock.Object);
        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    [Fact]
    public void GetMyDiscoveryResults_NoUserClaim_ReturnsUnauthorized()
    {
        var result = CreateController(null).GetMyDiscoveryResults();
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public void GetMyDiscoveryResults_UnknownUser_ReturnsOkWithNullBody()
    {
        var result = CreateController(Guid.NewGuid()).GetMyDiscoveryResults();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Null(ok.Value);
    }

    [Fact]
    public async Task GetMyRequestPermissions_NoUserClaim_ReturnsUnauthorized()
    {
        var result = await CreateController(null).GetMyRequestPermissions("radarr", "movie", CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("")]
    [InlineData("radarr2")]
    public async Task GetMyRequestPermissions_InvalidServiceType_ReturnsBadRequest(string svc)
    {
        var result = await CreateController(Guid.NewGuid()).GetMyRequestPermissions(svc, "movie", CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("")]
    [InlineData("movies")]
    public async Task GetMyRequestPermissions_InvalidMediaType_ReturnsBadRequest(string mt)
    {
        var result = await CreateController(Guid.NewGuid()).GetMyRequestPermissions("radarr", mt, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMyRequestPermissions_ValidRequest_NormalizesCasing_AndReturnsPermission()
    {
        // BUG GUARD: casing must be normalised before validation AND before forwarding.
        var userId = Guid.NewGuid();
        var permission = new UserRequestPermissionResult { CanRequest = true };
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(permission);

        var result = await CreateController(userId).GetMyRequestPermissions("Radarr", "MOVIE", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(permission, ok.Value);
        _discoveryMock.Verify(
            d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMyServiceInfo_NoUserClaim_ReturnsUnauthorized()
    {
        var result = await CreateController(null).GetMyServiceInfo("radarr", CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Theory]
    [InlineData("plex")]
    [InlineData("")]
    public async Task GetMyServiceInfo_InvalidServiceType_ReturnsBadRequest(string svc)
    {
        var result = await CreateController(Guid.NewGuid()).GetMyServiceInfo(svc, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMyServiceInfo_NoRequestPermission_NonTransient_ReturnsEmptyOk()
    {
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult { CanRequest = false, IsTransient = false });

        var result = await CreateController(userId).GetMyServiceInfo("radarr", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var services = Assert.IsAssignableFrom<IEnumerable<SeerrServiceInfo>>(ok.Value);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetMyServiceInfo_TransientFailure_Returns503()
    {
        // BUG GUARD: transient failure must NOT be swallowed as empty-list 200.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "tv", "sonarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = false,
                IsTransient = true,
                DeniedReason = "Seerr unreachable"
            });

        var result = await CreateController(userId).GetMyServiceInfo("sonarr", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task GetMyServiceInfo_CanRequestButNoProfiles_ReturnsEmptyOk()
    {
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = new List<AllowedQualityProfile>()
            });

        var result = await CreateController(userId).GetMyServiceInfo("radarr", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var services = Assert.IsAssignableFrom<IEnumerable<SeerrServiceInfo>>(ok.Value);
        Assert.Empty(services);
    }

    [Fact]
    public async Task GetMyServiceInfo_CanRequestWithProfiles_ReturnsBuiltServiceInfo()
    {
        // Reconstructs ServiceInfo from permissions.Profiles WITHOUT a second HTTP round-trip.
        var userId = Guid.NewGuid();
        _discoveryMock
            .Setup(d => d.GetUserRequestPermissionsAsync(userId, "movie", "radarr", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserRequestPermissionResult
            {
                CanRequest = true,
                Profiles = new List<AllowedQualityProfile>
                {
                    new() { ServerId = 1, ServerName = "Radarr-4K", ProfileId = 10, ProfileName = "2160p", IsDefault = true, RootFolder = "/movies/4k" },
                    new() { ServerId = 1, ServerName = "Radarr-4K", ProfileId = 11, ProfileName = "1080p", IsDefault = false, RootFolder = "/movies/hd" }
                }
            });

        var result = await CreateController(userId).GetMyServiceInfo("radarr", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var services = Assert.IsAssignableFrom<IEnumerable<SeerrServiceInfo>>(ok.Value);
        var svc = Assert.Single(services);
        Assert.Equal(1, svc.Id);
        Assert.Equal("Radarr-4K", svc.Name);
        Assert.Equal(2, svc.Profiles.Count); // Both profiles preserved
        Assert.Equal(2, svc.RootFolders.Count); // Both root folders preserved
        Assert.Equal(10, svc.ActiveProfileId); // Default was 2160p
    }
}
