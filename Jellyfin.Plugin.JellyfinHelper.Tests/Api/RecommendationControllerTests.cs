using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class RecommendationControllerTests
{
    private readonly Mock<IRecommendationCacheService> _mockCache;
    private readonly RecommendationController _controller;
    private readonly Mock<IRecommendationEngine> _mockEngine;
    private readonly Mock<IWatchHistoryService> _mockWatchHistory;

    public RecommendationControllerTests()
    {
        _mockEngine = new Mock<IRecommendationEngine>();
        _mockCache = new Mock<IRecommendationCacheService>();
        _mockWatchHistory = new Mock<IWatchHistoryService>();

        _controller = new RecommendationController(
            _mockEngine.Object,
            _mockCache.Object,
            _mockWatchHistory.Object);
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
        _mockEngine.Verify(e => e.GetAllRecommendations(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void GetAllRecommendations_CacheMiss_GeneratesOnDemand()
    {
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Bob" }
        };
        _mockEngine.Setup(e => e.GetAllRecommendations(20)).Returns(generated);

        var result = _controller.GetAllRecommendations();

        Assert.IsType<OkObjectResult>(result.Result);
        _mockCache.Verify(c => c.SaveResults(generated), Times.Once);
    }

    [Fact]
    public void GetAllRecommendations_ClampsMaxPerUser()
    {
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);
        _mockEngine.Setup(e => e.GetAllRecommendations(It.IsAny<int>()))
            .Returns(new Collection<RecommendationResult>());

        // maxPerUser=200 should be clamped to 100
        _controller.GetAllRecommendations(200);

        _mockEngine.Verify(e => e.GetAllRecommendations(100), Times.Once);
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
    }

    [Fact]
    public void GetUserRecommendations_CacheMiss_GeneratesOnDemand()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);

        var generated = new RecommendationResult { UserId = userId, UserName = "Alice" };
        _mockEngine.Setup(e => e.GetRecommendations(userId, 20)).Returns(generated);

        var result = _controller.GetUserRecommendations(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("Alice", ((RecommendationResult)ok.Value!).UserName);
    }

    [Fact]
    public void GetUserRecommendations_UserNotFound_Returns404()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResults()).Returns((Collection<RecommendationResult>?)null);
        _mockEngine.Setup(e => e.GetRecommendations(userId, 20)).Returns((RecommendationResult?)null);

        var result = _controller.GetUserRecommendations(userId);

        Assert.IsType<NotFoundResult>(result.Result);
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
        var data = Assert.IsAssignableFrom<List<UserWatchProfile>>(ok.Value);
        Assert.Single(data);
        Assert.Empty(data[0].WatchedItems); // stripped for lean response
    }
}