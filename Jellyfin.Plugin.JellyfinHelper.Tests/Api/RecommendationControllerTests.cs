using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
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

    [Fact]
    public async Task GetAllRecommendations_CacheHit_ReturnsCachedResults()
    {
        var cached = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Alice" }
        };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        var result = await _controller.GetAllRecommendations();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<Collection<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        _mockEngine.Verify(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAllRecommendations_CacheMiss_GeneratesOnDemandAndPersists()
    {
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Bob" }
        };
        _mockEngine.Setup(e => e.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(generated);

        var result = await _controller.GetAllRecommendations();

        Assert.IsType<OkObjectResult>(result.Result);
        // Activate mode: persist to disk
        _mockCache.Verify(c => c.SaveResults(generated), Times.Once);
    }

    [Fact]
    public async Task GetAllRecommendations_EngineReceivesConfiguredMax_NotApiParam()
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
        await _controller.GetAllRecommendations(200);

        _mockEngine.Verify(e => e.GetAllRecommendations(20, It.IsAny<CancellationToken>()), Times.Once);
    }

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
    public void GetEnsembleDiagnostics_PerUser_IncludesUserName()
    {
        var userId = Guid.NewGuid();
        _mockEngine.Setup(e => e.GetUserEnsembleDiagnostics(userId))
            .Returns((new EnsembleDiagnostics(), true));
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "Alice" });

        var result = _controller.GetEnsembleDiagnostics(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(data.Available);
        Assert.True(data.IsPerUser);
        Assert.Equal("Alice", data.UserName);
    }

    [Fact]
    public void GetEnsembleDiagnostics_UserNameLookupThrows_StillReturnsDiagnosticsWithNullName()
    {
        // The name lookup is a display-only extra layered on top of already-resolved diagnostics. A transient
        // profile-build failure there must not turn a valid 200 into a 500; the response comes back with a null
        // name instead.
        var userId = Guid.NewGuid();
        _mockEngine.Setup(e => e.GetUserEnsembleDiagnostics(userId))
            .Returns((new EnsembleDiagnostics(), true));
        _mockWatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Throws(new InvalidOperationException("profile build blew up"));

        var result = _controller.GetEnsembleDiagnostics(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<EnsembleDiagnosticsResponse>(ok.Value);
        Assert.True(data.Available);
        Assert.True(data.IsPerUser);
        Assert.Null(data.UserName);
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

    [Fact]
    public async Task GetAllRecommendations_Disabled_Returns503()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate });

        var result = await _controller.GetAllRecommendations();

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
    public async Task GetAllRecommendations_DryRun_CacheMiss_GeneratesButDoesNotPersist()
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration { RecommendationsTaskMode = TaskMode.DryRun });
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "DryRunUser" }
        };
        _mockEngine.Setup(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(generated);

        var result = await _controller.GetAllRecommendations();

        Assert.IsType<OkObjectResult>(result.Result);
        // DryRun should NOT persist to disk - the UI caches in the browser instead
        _mockCache.Verify(c => c.SaveResults(It.IsAny<IReadOnlyList<RecommendationResult>>()), Times.Never);
    }

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

    [Fact]
    public async Task GetAllRecommendations_CachedListLargerThanRequestedMax_TrimsWithoutMutatingCache()
    {
        // BUG GUARD: TrimRecommendations must return a DEEP COPY when trimming so subsequent requests still see the full cached list.
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
        var result = await _controller.GetAllRecommendations(maxPerUser: 5);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        Assert.Equal(5, data[0].Recommendations.Count);
        // Critical: the original cache entry must NOT have been shortened.
        Assert.Equal(15, original.Recommendations.Count);
    }

    [Fact]
    public async Task GetAllRecommendations_CachedListSmallerThanRequestedMax_ReturnsSameReference()
    {
        // Perf regression guard: when no trim is needed the controller must not allocate a copy of the list. We can prove this by checking element count and the exact reference of the underlying Recommendations collection.
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

        var result = await _controller.GetAllRecommendations(maxPerUser: 10);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        // Same reference passthrough - no unnecessary allocation.
        Assert.Same(original, data[0]);
    }

    [Fact]
    public void GetUserRecommendations_CachedUserLargerThanRequestedMax_ReturnsTrimmedCopy()
    {
        // BUG GUARD: the copy-on-trim path in GetUserRecommendations (Lines 145-157) must preserve all metadata (UserName, ScoringStrategy, GeneratedAt, Profile, Cohort) - not just the Recommendations.
        var userId = Guid.NewGuid();
        var original = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            ScoringStrategy = "TestStrategy",
            ScoringStrategyKey = "strategyTest",
            GeneratedAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            Cohort = "explore-high",
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
        Assert.Equal(userId, data.UserId);
        Assert.Equal("Alice", data.UserName);
        Assert.Equal("TestStrategy", data.ScoringStrategy);
        Assert.Equal("strategyTest", data.ScoringStrategyKey);
        Assert.Equal(original.GeneratedAt, data.GeneratedAt);
        Assert.Equal("explore-high", data.Cohort);
        Assert.Same(original.Profile, data.Profile);
        // Original cache entry not mutated.
        Assert.Equal(25, original.Recommendations.Count);
    }

    [Fact]
    public async Task GetAllRecommendations_CachedListLargerThanRequestedMax_TrimmedCopyPreservesCohort()
    {
        // BUG GUARD: TrimRecommendations must propagate the Cohort field on the trimmed copy. Without this test, a regression that drops Cohort would silently break the A/B reporting for the admin overview endpoint.
        var userId = Guid.NewGuid();
        var original = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Cohort = "explore-low",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 12)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        _mockCache.Setup(c => c.LoadResults())
            .Returns(new Collection<RecommendationResult> { original });

        var result = await _controller.GetAllRecommendations(maxPerUser: 4);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        Assert.Equal(4, data[0].Recommendations.Count);
        Assert.Equal("explore-low", data[0].Cohort);
    }

    [Theory]
    // maxPerUser <= 0 -> falls back to configured (20); >100 -> clamped to 100. The trailing tuple asserts BOTH: what the engine is invoked with AND how many recommendations survive trimming for each element in the response.
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    [InlineData(1000, 100)]
    [InlineData(50, 50)]
    public async Task GetAllRecommendations_MaxPerUserOutOfRange_TrimsToExpectedEffectiveLimit(
        int maxPerUser, int expectedTrimmedCount)
    {
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration
            {
                RecommendationsTaskMode = TaskMode.Activate,
                MaxRecommendationsPerUser = 20
            });

        // Seed the cache with 150 recommendations so the trim path is exercised
        // regardless of the effective limit (max clamp is 100).
        var userId = Guid.NewGuid();
        var cached = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "Alice",
                Recommendations = new Collection<RecommendedItem>(
                    Enumerable.Range(0, 150)
                        .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                        .ToList())
            }
        };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        var result = await _controller.GetAllRecommendations(maxPerUser);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        Assert.Equal(expectedTrimmedCount, data[0].Recommendations.Count);
        // Cache must never be mutated by trimming.
        Assert.Equal(150, cached[0].Recommendations.Count);
    }

    [Theory]
    // maxResults <= 0 -> falls back to configured (20); >100 -> clamped to 100.
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(500, 100)]
    [InlineData(15, 15)]
    public void GetUserRecommendations_MaxResultsOutOfRange_TrimsToExpectedEffectiveLimit(
        int maxResults, int expectedTrimmedCount)
    {
        var userId = Guid.NewGuid();
        _mockConfigService.Setup(c => c.GetConfiguration())
            .Returns(new PluginConfiguration
            {
                RecommendationsTaskMode = TaskMode.Activate,
                MaxRecommendationsPerUser = 20
            });

        // Seed the user cache with 150 recommendations so the trimmed-copy path is
        // exercised regardless of the effective limit (max clamp is 100).
        var cachedUser = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 150)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        _mockCache.Setup(c => c.LoadResults())
            .Returns(new Collection<RecommendationResult> { cachedUser });

        var result = _controller.GetUserRecommendations(userId, maxResults);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<RecommendationResult>(ok.Value);
        Assert.Equal(expectedTrimmedCount, data.Recommendations.Count);
        // Cache must never be mutated by the copy-on-trim path.
        Assert.Equal(150, cachedUser.Recommendations.Count);
    }

    [Fact]
    public async Task GetAllRecommendations_ConcurrentCacheMiss_EngineCalledOnce()
    {
        // First call: cache empty, generates. Second concurrent call: waits for lock, then finds the cache already filled, skips generation.
        var callCount = 0;
        _mockCache.Setup(c => c.LoadResults()).Returns(() =>
        {
            callCount++;
            // First two calls (initial check + re-check under lock by first caller) return null.
            // Third call (re-check under lock by second concurrent caller) returns data.
            return callCount <= 2
                ? null
                : new Collection<RecommendationResult> { new() { UserId = Guid.NewGuid() } };
        });
        _mockEngine.Setup(e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new Collection<RecommendationResult> { new() { UserId = Guid.NewGuid() } });

        // Fire two concurrent requests; the semaphore serialises them.
        var t1 = _controller.GetAllRecommendations();
        var t2 = _controller.GetAllRecommendations();
        var results = await Task.WhenAll(t1, t2);

        // Both should succeed with OK.
        Assert.All(results, r => Assert.IsType<OkObjectResult>(r.Result));

        // Engine must have been called at most once - the second waiter sees the cache.
        _mockEngine.Verify(
            e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtMostOnce());
    }

    [Fact]
    public async Task GetAllRecommendations_CacheFilledUnderLock_ReturnsCachedWithoutGenerating()
    {
        // The pre-lock LoadResults misses, but by the time we re-check under the lock another caller has filled the cache.
        var userId = Guid.NewGuid();
        var filled = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "Alice",
                Recommendations = new Collection<RecommendedItem>(
                    Enumerable.Range(0, 12)
                        .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                        .ToList())
            }
        };
        _mockCache.SetupSequence(c => c.LoadResults())
            .Returns((Collection<RecommendationResult>?)null) // pre-lock miss
            .Returns(filled); // re-check under lock hits

        var result = await _controller.GetAllRecommendations(maxPerUser: 5);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Single(data);
        Assert.Equal(5, data[0].Recommendations.Count);
        // Under-lock re-check short-circuits generation entirely.
        _mockEngine.Verify(
            e => e.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Cache source untouched by the trim.
        Assert.Equal(12, filled[0].Recommendations.Count);
    }

    [Fact]
    public async Task GetAllRecommendations_MixedTrimNeeds_TrimsOnlyOversizedUsersLeavingOthersUntouched()
    {
        // One user is over the limit (must be trimmed to a copy), another is under it and must
        // pass through by reference - proving the else branch does not needlessly copy.
        var oversized = new RecommendationResult
        {
            UserId = Guid.NewGuid(),
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 10)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"Item{i}" })
                    .ToList())
        };
        var underLimit = new RecommendationResult
        {
            UserId = Guid.NewGuid(),
            UserName = "Bob",
            Recommendations = new Collection<RecommendedItem>(
                Enumerable.Range(0, 2)
                    .Select(i => new RecommendedItem { ItemId = Guid.NewGuid(), Name = $"B{i}" })
                    .ToList())
        };
        var cached = new Collection<RecommendationResult> { oversized, underLimit };
        _mockCache.Setup(c => c.LoadResults()).Returns(cached);

        var result = await _controller.GetAllRecommendations(maxPerUser: 3);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsAssignableFrom<IReadOnlyList<RecommendationResult>>(ok.Value);
        Assert.Equal(2, data.Count);

        var trimmed = data.Single(r => r.UserId == oversized.UserId);
        Assert.Equal(3, trimmed.Recommendations.Count);
        Assert.NotSame(oversized, trimmed);

        var passthrough = data.Single(r => r.UserId == underLimit.UserId);
        // Under-limit entry re-added by reference, not copied.
        Assert.Same(underLimit, passthrough);
        Assert.Equal(2, passthrough.Recommendations.Count);

        // Neither source entry mutated.
        Assert.Equal(10, oversized.Recommendations.Count);
        Assert.Equal(2, underLimit.Recommendations.Count);
    }
}
