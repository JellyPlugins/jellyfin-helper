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
        // DryRun should NOT persist to disk - the UI caches in the browser instead
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

    // === Trim / cache-mutation-guard tests ===

    [Fact]
    public void GetAllRecommendations_CachedListLargerThanRequestedMax_TrimsWithoutMutatingCache()
    {
        // BUG GUARD: TrimRecommendations must return a DEEP COPY when trimming so subsequent
        // requests still see the full cached list. A regression that mutates the cache in-place
        // would cause every following request to see the previously-trimmed size.
        var userId = Guid.NewGuid();
        var original = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 15)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        var cached = new Collection<RecommendationResult> { original };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        // Ask for a smaller max than the cached count.
        var result = _controller.GetAllRecommendations(maxPerUser: 5);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        Assert.Equal(5, data[0].Recommendations.Count);
        // Critical: the original cache entry must NOT have been shortened.
        Assert.Equal(15, original.Recommendations.Count);
    }

    [Fact]
    public void GetAllRecommendations_CachedListSmallerThanRequestedMax_ReturnsSameReference()
    {
        // Perf regression guard: when no trim is needed the controller must not
        // allocate a copy of the list. We can prove this by checking element count
        // and the exact reference of the underlying Recommendations collection.
        var userId = Guid.NewGuid();
        var original = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 3)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        _mockCache.Setup(c => c.LoadResults())
            .Returns(new Collection<RecommendationResult> { original });

        var result = _controller.GetAllRecommendations(maxPerUser: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        // Same reference passthrough — no unnecessary allocation.
        Assert.Same(original, data[0]);
    }

    [Fact]
    public void GetUserRecommendations_CachedUserLargerThanRequestedMax_ReturnsTrimmedCopy()
    {
        // BUG GUARD: the copy-on-trim path in GetUserRecommendations (Lines 145-157) must
        // preserve all metadata (UserName, ScoringStrategy, GeneratedAt, Profile) — not
        // just the Recommendations. If a future refactor forgets one field, this test
        // catches it because we assert against every metadata property.
        var userId = Guid.NewGuid();
        var original = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            ScoringStrategy = "TestStrategy",
            ScoringStrategyKey = "strategyTest",
            GeneratedAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            Profile = new UserWatchProfile { UserId = userId, UserName = "Alice" },
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 25)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        _mockCache.Setup(c => c.LoadResults())
            .Returns(new Collection<RecommendationResult> { original });

        var result = _controller.GetUserRecommendations(userId, maxResults: 3);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<RecommendationResult>(ok.Value);
        // Trimmed to 3.
        Assert.Equal(3, data.Recommendations.Count);
        // All metadata carried through unchanged.
        Assert.Equal("Alice", data.UserName);
        Assert.Equal("TestStrategy", data.ScoringStrategy);
        Assert.Equal("strategyTest", data.ScoringStrategyKey);
        Assert.Equal(original.GeneratedAt, data.GeneratedAt);
        Assert.Same(original.Profile, data.Profile);
        // Original cache entry not mutated.
        Assert.Equal(25, original.Recommendations.Count);
    }

    [Theory]
    [InlineData(0)]      // Zero → falls back to config default
    [InlineData(-5)]     // Negative → falls back to config default
    [InlineData(1000)]   // Above 100 → clamped to 100
    public void GetAllRecommendations_MaxPerUserOutOfRange_ClampsToValidRange(int maxPerUser)
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

        var result = _controller.GetAllRecommendations(maxPerUser);

        // Must return 200 in every case — no ArgumentOutOfRangeException, no crash.
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(500)]
    public void GetUserRecommendations_MaxResultsOutOfRange_ClampsToValidRange(int maxResults)
    {
        var userId = Guid.NewGuid();
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration
            {
                RecommendationsTaskMode = TaskMode.Activate,
                MaxRecommendationsPerUser = 20
            });
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);
        _mockEngine.Setup(e => e.GetRecommendations(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new RecommendationResult { UserId = userId });

        var result = _controller.GetUserRecommendations(userId, maxResults);

        // Must return 200 — no ArgumentOutOfRangeException regardless of parameter shape.
        Assert.IsType<OkObjectResult>(result.Result);
    }
}
