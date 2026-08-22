using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="UserDiscoveryController"/>.
///     Note: These tests exercise the security access gate (DiscoveryUserAccessEnabled)
///     which cannot be bypassed in unit tests because <c>Plugin.Instance</c> is null.
///     Validation paths (400 responses) are covered by the equivalent admin
///     <see cref="DiscoveryControllerTests"/> which shares the same DTO validation logic.
///     Full integration tests covering the enabled-access path require a running
///     Jellyfin host with Plugin.Instance initialized - those are exercised by
///     <see cref="UserDiscoveryControllerAccessEnabledTests"/> and
///     <see cref="UserDiscoveryControllerSubmitTests"/> which flip the toggle on.
///     <para>
///         Belongs to the <c>ConfigOverride</c> collection because the sister suites
///         that DO flip <c>Plugin.Instance.Configuration.DiscoveryUserAccessEnabled</c> to
///         <c>true</c> run in parallel by default. Without joining the collection, THIS
///         suite would race with them and observe the toggle mid-flight, causing the
///         "access gate = false" tests here to see a mutated state and fail. All three
///         suites in the collection are serialised, so each suite's ctor/dispose resets
///         the toggle to a known state before its tests run.
///     </para>
/// </summary>
[Collection("ConfigOverride")]
public class UserDiscoveryControllerTests
{
    private readonly Mock<ISeerrDiscoveryService> _discoveryMock;
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock;
    private readonly DiscoveryCacheService _cache;
    private readonly Mock<ILogger<UserDiscoveryController>> _loggerMock;
    private readonly Mock<IPluginConfigurationService> _configServiceMock;
    private readonly MemoryCache _memoryCache;

    public UserDiscoveryControllerTests()
    {
        var pluginLog = new Mock<JellyfinHelper.Services.PluginLog.IPluginLogService>();
        var cacheLogger = new Mock<ILogger<DiscoveryCacheService>>();
        _cache = new DiscoveryCacheService(pluginLog.Object, cacheLogger.Object, filePath: Path.GetTempFileName());
        _discoveryMock = new Mock<ISeerrDiscoveryService>();
        _feedbackStoreMock = new Mock<IDiscoveryFeedbackStore>();
        _loggerMock = new Mock<ILogger<UserDiscoveryController>>();
        _configServiceMock = new Mock<IPluginConfigurationService>();
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(new PluginConfiguration());
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
    }

    private UserDiscoveryController CreateController(Guid? userId = null)
    {
        var controller = new UserDiscoveryController(
            _cache, _discoveryMock.Object, _feedbackStoreMock.Object, _configServiceMock.Object, _memoryCache, _loggerMock.Object);

        // Set up HttpContext with user claims
        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Fact]
    public void GetMyDiscoveryResults_WhenAccessDisabled_Returns403()
    {
        // DiscoveryUserAccessEnabled defaults to false when Plugin.Instance is null (test context)
        var controller = CreateController(Guid.NewGuid());
        var result = controller.GetMyDiscoveryResults();
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_NullDto_Returns403WhenAccessDisabled()
    {
        var controller = CreateController(Guid.NewGuid());

        // When DiscoveryUserAccessEnabled is false (Plugin.Instance is null in tests),
        // the access gate fires first and returns 403 before null-body validation.
        var result = await controller.SubmitMyRequest(null!, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_InvalidTmdbId_Returns403WhenAccessDisabled()
    {
        // Note: Validation branches (400) cannot be reached in unit tests because
        // Plugin.Instance is null → IsDiscoveryUserAccessEnabled() always returns false.
        // The access gate correctly fires first, which is the expected security behavior.
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryRequestDto { TmdbId = 0, MediaType = "movie" };
        var result = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_InvalidMediaType_Returns403WhenAccessDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryRequestDto { TmdbId = 100, MediaType = "invalid" };
        var result = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_RootFolderPathTraversal_Returns403WhenAccessDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "/media/../etc/passwd"
        };
        var result = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_RootFolderControlChars_Returns403WhenAccessDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "/media/\0movies"
        };
        var result = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitMyRequest_RootFolderTilde_Returns403WhenAccessDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 100,
            MediaType = "movie",
            RootFolder = "~/movies"
        };
        var result = await controller.SubmitMyRequest(dto, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public void DismissItem_NullDto_Returns403WhenDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var result = controller.DismissItem(null!);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public void DismissItem_InvalidTmdbId_Returns403WhenDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var dto = new DiscoveryDismissDto { TmdbId = 0, MediaType = "movie" };
        var result = controller.DismissItem(dto);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public void GetMyDiscoveryResults_NoUserClaim_Returns403WhenDisabled()
    {
        // No userId claim
        var controller = CreateController(null);
        var result = controller.GetMyDiscoveryResults();
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        // 403 because feature is disabled (checked before auth)
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetMyRequestPermissions_InvalidServiceType_Returns403WhenDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var result = await controller.GetMyRequestPermissions("invalid", "movie", CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetMyRequestPermissions_InvalidMediaType_Returns403WhenDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var result = await controller.GetMyRequestPermissions("radarr", "invalid", CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetMyServiceInfo_InvalidServiceType_Returns403WhenDisabled()
    {
        var controller = CreateController(Guid.NewGuid());
        var result = await controller.GetMyServiceInfo("invalid", CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public void GetScript_ReturnsEmbeddedJavaScriptFile()
    {
        var controller = CreateController(Guid.NewGuid());
        var result = controller.GetScript();
        // The embedded resource must always be present in a correctly built assembly.
        // Accepting NotFound here would mask broken resource embedding in CI.
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/javascript", fileResult.ContentType);
    }

    [Fact]
    public void GetExternalLinksConfig_WhenAccessDisabled_Returns403()
    {
        // The ExternalLinks endpoint must honour the same admin gate as every other user endpoint.
        var controller = CreateController(Guid.NewGuid());
        var result = controller.GetExternalLinksConfig();
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
    }
}