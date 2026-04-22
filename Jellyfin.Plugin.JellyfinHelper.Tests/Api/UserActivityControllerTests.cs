using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class UserActivityControllerTests
{
    private readonly Mock<IUserActivityCacheService> _mockCache;
    private readonly UserActivityController _controller;
    private readonly Mock<IUserActivityInsightsService> _mockInsights;
    private readonly Mock<IPluginConfigurationService> _mockConfig;

    public UserActivityControllerTests()
    {
        _mockCache = new Mock<IUserActivityCacheService>();
        _mockInsights = new Mock<IUserActivityInsightsService>();
        _mockConfig = new Mock<IPluginConfigurationService>();
        // Default: feature enabled (TaskMode = Activate)
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Activate
        });
        _controller = new UserActivityController(_mockCache.Object, _mockInsights.Object, _mockConfig.Object);
    }

    // === GetLatestActivity ===

    [Fact]
    public void GetLatestActivity_CacheHit_ReturnsCachedResult()
    {
        var cached = new UserActivityResult
        {
            TotalItemsWithActivity = 5,
            TotalUsersAnalyzed = 2,
            TotalPlayCount = 42
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        var result = _controller.GetLatestActivity();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(5, data.TotalItemsWithActivity);
        _mockInsights.Verify(i => i.BuildActivityReport(), Times.Never);
    }

    [Fact]
    public void GetLatestActivity_CacheMiss_GeneratesAndCaches()
    {
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        var generated = new UserActivityResult
        {
            TotalItemsWithActivity = 3,
            TotalUsersAnalyzed = 1,
            TotalPlayCount = 10
        };
        _mockInsights.Setup(i => i.BuildActivityReport()).Returns(generated);

        var result = _controller.GetLatestActivity();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(3, data.TotalItemsWithActivity);
        _mockCache.Verify(c => c.SaveResult(generated), Times.Once);
    }

    // === GetUserActivity ===

    [Fact]
    public void GetUserActivity_UserFound_ReturnsFilteredItems()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var cached = new UserActivityResult
        {
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Movie A",
                    ItemType = "Movie",
                    TotalPlayCount = 5,
                    UniqueViewers = 2,
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = userId,
                            UserName = "Alice",
                            PlayCount = 3,
                            Played = true,
                            LastPlayedDate = DateTime.UtcNow
                        },
                        new()
                        {
                            UserId = otherUserId,
                            UserName = "Bob",
                            PlayCount = 2,
                            Played = true
                        }
                    }
                },
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Movie B",
                    ItemType = "Movie",
                    TotalPlayCount = 1,
                    UniqueViewers = 1,
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = otherUserId,
                            UserName = "Bob",
                            PlayCount = 1,
                            Played = true
                        }
                    }
                }
            }
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        var result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Movie A", data[0].ItemName);
        // Should only contain the filtered user's activity
        Assert.Single(data[0].UserActivities);
        Assert.Equal(userId, data[0].UserActivities[0].UserId);
    }

    [Fact]
    public void GetUserActivity_UserNotFound_Returns404()
    {
        var cached = new UserActivityResult
        {
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Movie A",
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new() { UserId = Guid.NewGuid(), UserName = "Bob" }
                    }
                }
            }
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        var result = _controller.GetUserActivity(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void GetUserActivity_EmptyGuid_Returns400()
    {
        var result = _controller.GetUserActivity(Guid.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("userId", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetUserActivity_EpisodeFields_AreMappedCorrectly()
    {
        var userId = Guid.NewGuid();

        var cached = new UserActivityResult
        {
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Folge 3",
                    ItemType = "Episode",
                    SeriesName = "Frieren: Beyond Journey's End",
                    EpisodeLabel = "S01E03",
                    TotalPlayCount = 1,
                    UniqueViewers = 1,
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = userId,
                            UserName = "Alice",
                            PlayCount = 1,
                            Played = true,
                            LastPlayedDate = DateTime.UtcNow
                        }
                    }
                }
            }
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        var result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Frieren: Beyond Journey's End", data[0].SeriesName);
        Assert.Equal("S01E03", data[0].EpisodeLabel);
        Assert.Equal("Folge 3", data[0].ItemName);
    }

    [Fact]
    public void GetLatestActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        var result = _controller.GetLatestActivity();

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public void GetLatestActivity_DryRun_CacheMiss_DoesNotPersist()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.DryRun
        });
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        var generated = new UserActivityResult
        {
            TotalItemsWithActivity = 7,
            TotalUsersAnalyzed = 3,
            TotalPlayCount = 25
        };
        _mockInsights.Setup(i => i.BuildActivityReport()).Returns(generated);

        var result = _controller.GetLatestActivity();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(7, data.TotalItemsWithActivity);
        // DryRun should NOT persist to cache
        _mockCache.Verify(c => c.SaveResult(It.IsAny<UserActivityResult>()), Times.Never);
    }

    [Fact]
    public void GetUserActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        var result = _controller.GetUserActivity(Guid.NewGuid());

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public void GetUserActivity_DryRun_CacheMiss_DoesNotPersist()
    {
        var userId = Guid.NewGuid();
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.DryRun
        });
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        var generated = new UserActivityResult
        {
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Movie DryRun",
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = userId,
                            UserName = "TestUser",
                            PlayCount = 1,
                            Played = true,
                            LastPlayedDate = DateTime.UtcNow
                        }
                    }
                }
            }
        };
        _mockInsights.Setup(i => i.BuildActivityReport()).Returns(generated);

        var result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        // DryRun should NOT persist to cache
        _mockCache.Verify(c => c.SaveResult(It.IsAny<UserActivityResult>()), Times.Never);
    }

    [Fact]
    public void GetUserActivity_CacheMiss_GeneratesAndCaches()
    {
        var userId = Guid.NewGuid();
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        var generated = new UserActivityResult
        {
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Movie C",
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = userId,
                            UserName = "Alice",
                            PlayCount = 1,
                            Played = true,
                            LastPlayedDate = DateTime.UtcNow
                        }
                    }
                }
            }
        };
        _mockInsights.Setup(i => i.BuildActivityReport()).Returns(generated);

        var result = _controller.GetUserActivity(userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Movie C", data[0].ItemName);
        _mockCache.Verify(c => c.SaveResult(generated), Times.Once);
    }
}