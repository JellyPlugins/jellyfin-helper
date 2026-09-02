using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for UserDiscoveryController with the access gate ENABLED. Complements UserDiscoveryControllerTests by exercising validation, permission and happy-path branches unreachable when the gate is disabled.
/// </summary>
[Collection("ConfigOverride")]
public sealed class UserDiscoveryControllerAccessEnabledTests : IDisposable
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<ILogger<UserDiscoveryController>> _loggerMock;
    private readonly Mock<IPluginConfigurationService> _configServiceMock;
    private readonly MemoryCache _memoryCache;

    private static readonly int[] ExpectedSingleItem = [21];
    private static readonly int[] ExpectedBackfillItems = [32, 33];

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
        _configServiceMock = new Mock<IPluginConfigurationService>();
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration { DiscoveryUserAccessEnabled = true });
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        ControllerTestFactory.ResetPluginConfiguration();
        _cache.Dispose();
        _memoryCache.Dispose();
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var controller = new UserDiscoveryController(
            _cache, _discoveryMock.Object, _feedbackStoreMock.Object, _configServiceMock.Object, _memoryCache, _loggerMock.Object);
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
    public async Task GetMyDiscoveryResults_NoUserClaim_ReturnsUnauthorized()
    {
        var result = await CreateController(null).GetMyDiscoveryResults(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_UnknownUser_ReturnsOkWithNullBody()
    {
        var result = await CreateController(Guid.NewGuid()).GetMyDiscoveryResults(CancellationToken.None);
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

    [Fact]
    public async Task GetMyDiscoveryResults_KnownUser_ReturnsVisibleRecommendations_ExcludingDismissedAndRequestedAndAlreadyRequested()
    {
        var userId = Guid.NewGuid();
        var generatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        _cache.Save(new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "alice",
                GeneratedAt = generatedAt,
                Recommendations =
                {
                    new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie", AlreadyRequested = true },
                    new DiscoveryRecommendation { TmdbId = 2, MediaType = "movie", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 3, MediaType = "tv", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 4, MediaType = "movie", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 5, MediaType = "tv", AlreadyRequested = false }
                }
            }
        });

        // 2/movie is dismissed, 3/tv is requested; both must be filtered out alongside the AlreadyRequested item.
        _feedbackStoreMock.Setup(f => f.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)> { (2, "movie") });
        _feedbackStoreMock.Setup(f => f.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)> { (3, "tv") });

        var result = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        Assert.Equal(userId, body.UserId);
        Assert.Equal("alice", body.UserName);
        Assert.Equal(generatedAt, body.GeneratedAt);
        Assert.Equal(new[] { 4, 5 }, body.Recommendations.Select(r => r.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetMyDiscoveryResults_ExclusionMatchesRegardlessOfMediaTypeCasing()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        _cache.Save(new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "bob",
                GeneratedAt = DateTime.UtcNow,
                Recommendations =
                {
                    // MediaType stored with mixed case + surrounding whitespace must still match the canonical key.
                    new DiscoveryRecommendation { TmdbId = 7, MediaType = "  Movie  ", AlreadyRequested = false }
                }
            }
        });

        _feedbackStoreMock.Setup(f => f.GetDismissedItems(userId))
            .Returns(new HashSet<(int, string)> { (7, "movie") });

        var result = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        Assert.Empty(body.Recommendations);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_CapsVisibleAtMaxVisiblePerUser()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        var result = new DiscoveryResult { UserId = userId, UserName = "cara", GeneratedAt = DateTime.UtcNow };
        for (var i = 1; i <= 25; i++)
        {
            result.Recommendations.Add(new DiscoveryRecommendation { TmdbId = i, MediaType = "movie", AlreadyRequested = false });
        }

        _cache.Save(new List<DiscoveryResult> { result });

        var response = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        Assert.Equal(10, body.Recommendations.Count);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_WhenFeedbackStoreThrows_LogsAndServesUnfilteredVisiblePool()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        _cache.Save(new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "dave",
                GeneratedAt = DateTime.UtcNow,
                Recommendations =
                {
                    new DiscoveryRecommendation { TmdbId = 11, MediaType = "movie", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 12, MediaType = "tv", AlreadyRequested = false }
                }
            }
        });

        // A non-fatal store failure must not blow up the request: the exclusion set falls back to empty.
        _feedbackStoreMock.Setup(f => f.GetDismissedItems(userId))
            .Throws(new InvalidOperationException("store unavailable"));

        var response = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        Assert.Equal(new[] { 11, 12 }, body.Recommendations.Select(r => r.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetMyDiscoveryResults_InvokesReconcileBeforeReadingPool()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        _discoveryMock.Verify(
            d => d.ReconcileRequestedItemsAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_ReconcilesAtMostOncePerTtlWindow()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);

        // Two consecutive loads for the same user share one MemoryCache, so the second must be throttled.
        var controller = CreateController(userId);
        await controller.GetMyDiscoveryResults(CancellationToken.None);
        await controller.GetMyDiscoveryResults(CancellationToken.None);

        _discoveryMock.Verify(
            d => d.ReconcileRequestedItemsAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_WhenReconcileThrows_StillReturnsCachedView()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(10);
        _discoveryMock
            .Setup(d => d.ReconcileRequestedItemsAsync(userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("seerr exploded"));

        _cache.Save(new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "erin",
                GeneratedAt = DateTime.UtcNow,
                Recommendations =
                {
                    new DiscoveryRecommendation { TmdbId = 21, MediaType = "movie", AlreadyRequested = false }
                }
            }
        });

        var response = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        Assert.Equal(ExpectedSingleItem, body.Recommendations.Select(r => r.TmdbId).ToArray());
    }

    [Fact]
    public async Task GetMyDiscoveryResults_AfterReconcileMarksItem_BackfillsNextItem()
    {
        var userId = Guid.NewGuid();
        _discoveryMock.SetupGet(d => d.MaxVisiblePerUser).Returns(2);

        _cache.Save(new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                UserName = "frank",
                GeneratedAt = DateTime.UtcNow,
                Recommendations =
                {
                    new DiscoveryRecommendation { TmdbId = 31, MediaType = "movie", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 32, MediaType = "movie", AlreadyRequested = false },
                    new DiscoveryRecommendation { TmdbId = 33, MediaType = "movie", AlreadyRequested = false }
                }
            }
        });

        // Simulate reconciliation discovering that item 31 was requested out-of-band: it marks the
        // cache entry and records the signal, exactly as the real service does.
        _discoveryMock
            .Setup(d => d.ReconcileRequestedItemsAsync(userId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid uid, CancellationToken ct) =>
            {
                await _cache.MarkAsRequestedAsync(31, "movie", uid, ct);
                return 1;
            });
        _feedbackStoreMock.Setup(f => f.GetRequestedItems(userId))
            .Returns(new HashSet<(int, string)>());

        var response = await CreateController(userId).GetMyDiscoveryResults(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<DiscoveryResult>(ok.Value);
        // 31 marked requested and drops out; 32 and 33 become the visible top-2.
        Assert.Equal(ExpectedBackfillItems, body.Recommendations.Select(r => r.TmdbId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void BuildReconcileKey_HasExpectedFormat_AndSharesGenerationWithRateLimitKey()
    {
        var userId = Guid.NewGuid();
        var reconcileKey = UserDiscoveryController.BuildReconcileKey(userId);
        var rateLimitKey = UserDiscoveryController.BuildRateLimitKey(userId);

        Assert.Contains("discovery:reconcile:", reconcileKey, StringComparison.Ordinal);
        Assert.EndsWith(userId.ToString("N"), reconcileKey, StringComparison.Ordinal);

        // Both keys embed the same generation segment so a plugin reload invalidates both together.
        var reconcileGen = reconcileKey.Split(':')[3];
        var rateLimitGen = rateLimitKey.Split(':')[3];
        Assert.Equal(rateLimitGen, reconcileGen);
    }

    [Fact]
    public async Task GetMyDiscoveryResults_WhenAccessDisabled_DoesNotReconcile()
    {
        Plugin.Instance!.Configuration.DiscoveryUserAccessEnabled = false;
        _configServiceMock.Setup(s => s.GetConfiguration())
            .Returns(new PluginConfiguration { DiscoveryUserAccessEnabled = false });

        var response = await CreateController(Guid.NewGuid()).GetMyDiscoveryResults(CancellationToken.None);

        Assert.IsType<ObjectResult>(response.Result);
        _discoveryMock.Verify(
            d => d.ReconcileRequestedItemsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
