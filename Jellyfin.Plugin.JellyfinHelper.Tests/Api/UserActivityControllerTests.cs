using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
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

public class UserActivityControllerTests
{
    private readonly Mock<IUserActivityCacheService> _mockCache;
    private readonly UserActivityController _controller;
    private readonly Mock<IUserActivityInsightsService> _mockInsights;
    private readonly Mock<IPluginConfigurationService> _mockConfig;
    private readonly Mock<IUserManager> _mockUserManager;

    public UserActivityControllerTests()
    {
        _mockCache = new Mock<IUserActivityCacheService>();
        _mockInsights = new Mock<IUserActivityInsightsService>();
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
        _controller = new UserActivityController(_mockCache.Object, _mockInsights.Object, _mockConfig.Object, _mockUserManager.Object);
    }

    // === GetLatestActivity ===

    [Fact]
    public async Task GetLatestActivity_CacheHit_ReturnsCachedResult()
    {
        var cached = new UserActivityResult
        {
            TotalItemsWithActivity = 5,
            TotalUsersAnalyzed = 2,
            TotalPlayCount = 42
        };
        _mockCache.Setup(c => c.LoadResult()).Returns(cached);

        var result = await _controller.GetLatestActivity(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(5, data.TotalItemsWithActivity);
        _mockInsights.Verify(i => i.BuildActivityReport(), Times.Never);
    }

    [Fact]
    public async Task GetLatestActivity_CacheMiss_GeneratesAndCaches()
    {
        _mockCache.Setup(c => c.LoadResult()).Returns((UserActivityResult?)null);

        var generated = new UserActivityResult
        {
            TotalItemsWithActivity = 3,
            TotalUsersAnalyzed = 1,
            TotalPlayCount = 10
        };
        _mockInsights.Setup(i => i.BuildActivityReport()).Returns(generated);

        var result = await _controller.GetLatestActivity(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(3, data.TotalItemsWithActivity);
        _mockCache.Verify(c => c.SaveResult(generated), Times.Once);
    }

    // === GetUserActivity ===

    [Fact]
    public async Task GetUserActivity_UserFound_ReturnsFilteredItems()
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

        var result = await _controller.GetUserActivity(userId, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Movie A", data[0].ItemName);
        // Should only contain the filtered user's activity
        Assert.Single(data[0].UserActivities);
        Assert.Equal(userId, data[0].UserActivities[0].UserId);
    }

    [Fact]
    public async Task GetUserActivity_UserNotFound_Returns200OkEmpty()
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

        var result = await _controller.GetUserActivity(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Empty(data);
    }

    [Fact]
    public async Task GetUserActivity_EmptyGuid_Returns400()
    {
        var result = await _controller.GetUserActivity(Guid.Empty, cancellationToken: CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("userId", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUserActivity_EpisodeFields_AreMappedCorrectly()
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

        var result = await _controller.GetUserActivity(userId, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Frieren: Beyond Journey's End", data[0].SeriesName);
        Assert.Equal("S01E03", data[0].EpisodeLabel);
        Assert.Equal("Folge 3", data[0].ItemName);
    }

    [Fact]
    public async Task GetLatestActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        var result = await _controller.GetLatestActivity(CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetLatestActivity_DryRun_CacheMiss_DoesPersistCache()
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

        var result = await _controller.GetLatestActivity(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<UserActivityResult>(ok.Value);
        Assert.Equal(7, data.TotalItemsWithActivity);
        // UserActivityController always caches on cache-miss to avoid expensive rebuilds,
        // independent of TaskMode (see UserActivityController.GetLatestActivity).
        _mockCache.Verify(c => c.SaveResult(It.IsAny<UserActivityResult>()), Times.Once);
    }

    [Fact]
    public async Task GetUserActivity_WhenDeactivated_Returns503()
    {
        _mockConfig.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration
        {
            RecommendationsTaskMode = TaskMode.Deactivate
        });

        var result = await _controller.GetUserActivity(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetUserActivity_DryRun_CacheMiss_DoesPersistCache()
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

        var result = await _controller.GetUserActivity(userId, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        // UserActivityController always caches on cache-miss to avoid expensive rebuilds,
        // independent of TaskMode (see UserActivityController.GetUserActivity).
        _mockCache.Verify(c => c.SaveResult(It.IsAny<UserActivityResult>()), Times.Once);
    }

    [Fact]
    public async Task GetUserActivity_CacheMiss_GeneratesAndCaches()
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

        var result = await _controller.GetUserActivity(userId, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<UserActivitySummary>>(ok.Value);
        Assert.Single(data);
        Assert.Equal("Movie C", data[0].ItemName);
        _mockCache.Verify(c => c.SaveResult(generated), Times.Once);
    }

    [Fact]
    public async Task GetUserActivity_UserManagerReturnsNull_Returns404()
    {
        // Arrange: override the default mock so this specific userId is unknown
        var unknownUserId = Guid.NewGuid();
        _mockUserManager.Setup(m => m.GetUserById(unknownUserId)).Returns((User?)null);

        var result = await _controller.GetUserActivity(unknownUserId, cancellationToken: CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _mockInsights.Verify(i => i.BuildActivityReport(), Times.Never);
    }
}
