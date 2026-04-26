using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class RecommendationControllerTests
{
    private readonly Mock<IRecommendationCacheService> _mockCache;
    private readonly Mock<IPluginConfigurationService> _mockConfigService;
    private readonly RecommendationController _controller;
    private readonly Mock<IRecommendationEngine> _mockEngine;
    private readonly Mock<IWatchHistoryService> _mockWatchHistory;

    public RecommendationControllerTests()
    {
        _mockEngine = new Mock<IRecommendationEngine>();
        _mockCache = new Mock<IRecommendationCacheService>();
        _mockWatchHistory = new Mock<IWatchHistoryService>();
        _mockConfigService = new Mock<IPluginConfigurationService>();

        // Default: recommendations enabled (Activate mode)
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate });

        _controller = new RecommendationController(
            _mockEngine.Object,
            _mockCache.Object,
            _mockWatchHistory.Object,
            _mockConfigService.Object);
    }

    // === GetAllRecommendations ===

    [Fact]
    public void GetAllRecommendations_CacheHit_ReturnsCachedResults()
    {
        var cached = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Alice" }
        };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        var result = _controller.GetAllRecommendations();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<Collection<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        _mockEngine.Verify(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetAllRecommendations_CacheMiss_GeneratesOnDemandAndPersists()
    {
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Bob" }
        };
        _mockEngine.Setup(e => e.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(generated);

        var result = _controller.GetAllRecommendations();

        Assert.IsType<OkObjectResult>(result.Result);
        // Activate mode: persist to disk
        _mockCache.Verify(c => c.SaveResults(generated), Times.Once);
    }

    [Fact]
    public void GetAllRecommendations_EngineReceivesConfiguredMax_NotApiParam()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration
            {
                RecommendationsTaskMode = TaskMode.Activate,
                MaxRecommendationsPerUser = 20
            });
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);
        _mockEngine.Setup(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new Collection<RecommendationResult>());

        // The API parameter maxPerUser=200 is only used for response trimming.
        // The engine always receives the configured MaxRecommendationsPerUser (20).
        _controller.GetAllRecommendations(200);

        _mockEngine.Verify(e => e.GetAllRecommendations(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // === GetUserRecommendations ===

    [Fact]
    public void GetUserRecommendations_CacheHit_ReturnsCachedUser()
    {
        var userId = Guid.NewGuid();
        var cached = new Collection<RecommendationResult>
        {
            new() { UserId = userId, UserName = "Alice" },
            new() { UserId = Guid.NewGuid(), UserName = "Bob" }
        };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        var result = _controller.GetUserRecommendations(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<RecommendationResult>(ok.Value);
        Assert.Equal("Alice", data.UserName);
        _mockEngine.Verify(e => e.GetRecommendations(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetUserRecommendations_CacheMiss_GeneratesOnDemand()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new RecommendationResult { UserId = userId, UserName = "Alice" };
        _mockEngine.Setup(e => e.GetRecommendations(userId, 20, It.IsAny<CancellationToken>())).Returns(generated);

        var result = _controller.GetUserRecommendations(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Alice", ((RecommendationResult)ok.Value!).UserName);
    }

    [Fact]
    public void GetUserRecommendations_UserNotFound_Returns404()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);
        _mockEngine.Setup(e => e.GetRecommendations(userId, 20, It.IsAny<CancellationToken>())).Returns((RecommendationResult?)null);

        var result = _controller.GetUserRecommendations(userId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetUserRecommendations_EmptyGuid_Returns400()
    {
        var result = _controller.GetUserRecommendations(Guid.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
        Assert.Contains("userId", badRequest.Value!.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // === GetUserWatchProfile ===

    [Fact]
    public void GetUserWatchProfile_Found_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = userId, UserName = "Alice" };
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);

        var result = _controller.GetUserWatchProfile(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Alice", ((UserWatchProfile)ok.Value!).UserName);
    }

    [Fact]
    public void GetUserWatchProfile_NotFound_Returns404()
    {
        var userId = Guid.NewGuid();
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns((UserWatchProfile?)null);

        var result = _controller.GetUserWatchProfile(userId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetUserWatchProfile_EmptyGuid_Returns400()
    {
        var result = _controller.GetUserWatchProfile(Guid.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
        Assert.Contains("userId", badRequest.Value!.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // === 503 Disabled ===

    [Fact]
    public void GetAllRecommendations_Disabled_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetAllRecommendations();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetUserRecommendations_Disabled_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetUserRecommendations(Guid.NewGuid());

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetAllWatchProfiles_Disabled_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetAllWatchProfiles();

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetUserWatchProfile_Disabled_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = _controller.GetUserWatchProfile(Guid.NewGuid());

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public void GetAllRecommendations_DryRun_CacheMiss_GeneratesButDoesNotPersist()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.DryRun });
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "DryRunUser" }
        };
        _mockEngine.Setup(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(generated);

        var result = _controller.GetAllRecommendations();

        Assert.IsType<OkObjectResult>(result.Result);
        // DryRun should NOT persist to disk — the UI caches in the browser instead
        _mockCache.Verify(c => c.SaveResults(It.IsAny<IReadOnlyList<RecommendationResult>>()), Times.Never);
    }

    // === GetAllWatchProfiles ===

    [Fact]
    public void GetAllWatchProfiles_ReturnsProfilesWithoutWatchedItems()
    {
        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = Guid.NewGuid(),
                UserName = "Alice",
                WatchedItems = new Collection<WatchedItemInfo>
                {
                    new() { Name = "Movie A" }
                }
            }
        };
        _mockWatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        var result = _controller.GetAllWatchProfiles();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IEnumerable<UserWatchProfile>>(ok.Value).ToList();
        Assert.Single(data);
        Assert.Empty(data[0].WatchedItems); // stripped for lean response

        // Verify the source profiles were not mutated (lean copy, not in-place strip)
        Assert.Single(profiles[0].WatchedItems);
        Assert.Equal("Movie A", profiles[0].WatchedItems[0].Name);
    }
}