using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for UserActivityController.
/// </summary>
public class UserActivityControllerTests
{
    private readonly Mock<IUserActivityCacheService> _mockCache;
    private readonly UserActivityController _controller;
    private readonly Mock<IPluginConfigurationService> _mockConfig;
    private readonly Mock<IUserManager> _mockUserManager;

    public UserActivityControllerTests()
    {
        _mockCache = new Mock<IUserActivityCacheService>();
        _mockConfig = new Mock<IPluginConfigurationService>();
        _mockUserManager = new Mock<IUserManager>();

        // Default: feature enabled (TaskMode = Activate)
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Activate
        });

        // Default: any userId resolves to a non-null user
        _mockUserManager.Setup(m => m.GetUserById(It.IsAny<Guid>()))
            .Returns(new User("testuser", "Default", "Default"));

        _controller = new UserActivityController(
            _mockCache.Object,
            _mockConfig.Object,
            _mockUserManager.Object);
    }

    /// <summary>
    ///     GetLatestActivity is synchronous and returns 200 OK
    ///     with the cached payload when the cache is populated.
    /// </summary>
    [Fact]
    public void GetLatestActivity_CachePopulated_Returns200WithData()
    {
        var cached = new UserActivityResult
        {
            TotalItemsWithActivity = 5,
            TotalUsersAnalyzed = 2,
            TotalPlayCount = 42
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        ActionResult<UserActivityResult> result = _controller.GetLatestActivity();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(5, data.TotalItemsWithActivity);
        Assert.Equal(2, data.TotalUsersAnalyzed);
        Assert.Equal(42, data.TotalPlayCount);
    }

    /// <summary>
    ///     GetLatestActivity is synchronous and returns 503
    ///     when the cache is empty (task has not run yet).
    /// </summary>
    [Fact]
    public void GetLatestActivity_CacheEmpty_Returns503()
    {
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        ActionResult<UserActivityResult> result = _controller.GetLatestActivity();

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
        // LoadResult must have been called exactly once - no retry or secondary build path
        _mockCache.Verify(c => c.LoadResult(), Times.Once);
    }

    /// <summary>
    ///     GetUserActivity is synchronous and returns 200 OK
    ///     with activity filtered to the requested user only.
    /// </summary>
    [Fact]
    public void GetUserActivity_KnownUser_Returns200WithFilteredData()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var cached = BuildCachedResult(
            BuildSummary("Movie A", userId, otherUserId),
            BuildSummary("Movie B", otherUserId)); // only other user

        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, ok.StatusCode);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);

        // Only Movie A has activity for userId
        Assert.Single(data);
        Assert.Equal("Movie A", data[0].ItemName);

        // UserActivities must be narrowed to the requested user
        Assert.Single(data[0].UserActivities);
        Assert.Equal(userId, data[0].UserActivities[0].UserId);
    }

    /// <summary>
    ///     GetUserActivity is synchronous and returns 404
    ///     when IUserManager cannot find the requested user.
    /// </summary>
    [Fact]
    public void GetUserActivity_UserNotFoundInUserManager_Returns404()
    {
        var unknownId = Guid.NewGuid();
        _mockUserManager.Setup(m => m.GetUserById(unknownId)).Returns((User?)null);

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(unknownId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    ///     GetUserActivity is synchronous and returns 503
    ///     when the cache is empty, even if the user is known.
    /// </summary>
    [Fact]
    public void GetUserActivity_CacheEmpty_Returns503()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(userId);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
        // LoadResult must have been called exactly once - no retry or secondary build path
        _mockCache.Verify(c => c.LoadResult(), Times.Once);
    }

    // Additional coverage preserved from prior test suite

    [Fact]
    public void GetUserActivity_EmptyGuid_Returns400()
    {
        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(Guid.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("userId", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetLatestActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        ActionResult<UserActivityResult> result = _controller.GetLatestActivity();

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public void GetLatestActivity_DryRun_CacheEmpty_Returns503()
    {
        // DryRun does NOT disable the feature - IsFeatureEnabled returns true for DryRun.
        // The 503 originates from the empty cache, not from the mode.
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.DryRun
        });
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        ActionResult<UserActivityResult> result = _controller.GetLatestActivity();

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
        _mockCache.Verify(c => c.LoadResult(), Times.Once);
    }

    [Fact]
    public void GetUserActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(Guid.NewGuid());

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public void GetUserActivity_DryRun_CacheEmpty_Returns503()
    {
        // DryRun does NOT disable the feature - IsFeatureEnabled returns true for DryRun.
        // The 503 originates from the empty cache, not from the mode.
        var userId = Guid.NewGuid();
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.DryRun
        });
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(userId);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
        _mockCache.Verify(c => c.LoadResult(), Times.Once);
    }

    [Fact]
    public void GetUserActivity_UserHasNoActivityInCache_Returns200WithEmptyList()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var cached = BuildCachedResult(BuildSummary("Movie B", otherUserId));
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Empty(data);
    }

    [Fact]
    public void GetUserActivity_EpisodeFields_AreMappedCorrectly()
    {
        var userId = Guid.NewGuid();

        var summary = new UserActivitySummary
        {
            ItemId = Guid.NewGuid(),
            ItemName = "Folge 3",
            ItemType = "Episode",
            SeriesName = "Frieren: Beyond Journey's End",
            EpisodeLabel = "S01E03",
            TotalPlayCount = 1,
            UniqueViewers = 1,
            UserActivities =
            {
                new UserItemActivity
                {
                    UserId = userId,
                    UserName = "Alice",
                    PlayCount = 1,
                    Played = true,
                    LastPlayedDate = DateTime.UtcNow
                }
            }
        };

        _mockCache.Setup(c => c.LoadResult()).Returns(BuildCachedResult(summary));

        ActionResult<List<UserActivitySummary>> result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Frieren: Beyond Journey's End", data[0].SeriesName);
        Assert.Equal("S01E03", data[0].EpisodeLabel);
        Assert.Equal("Folge 3", data[0].ItemName);
    }

    // Helpers

    private static UserActivityResult BuildCachedResult(params UserActivitySummary[] items)
    {
        var result = new UserActivityResult();
        foreach (var item in items)
        {
            result.Items.Add(item);
        }

        return result;
    }

    /// <summary>
    ///     Builds a summary where <paramref name="userIds" /> each get one played activity entry.
    /// </summary>
    private static UserActivitySummary BuildSummary(string name, params Guid[] userIds)
    {
        var summary = new UserActivitySummary
        {
            ItemId = Guid.NewGuid(),
            ItemName = name,
            ItemType = "Movie"
        };

        foreach (var uid in userIds)
        {
            summary.UserActivities.Add(new UserItemActivity
            {
                UserId = uid,
                UserName = uid.ToString("N")[..8],
                PlayCount = 1,
                Played = true,
                LastPlayedDate = DateTime.UtcNow
            });
        }

        return summary;
    }
}
