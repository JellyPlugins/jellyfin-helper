using System;
using System.Collections.ObjectModel;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Activity;

public sealed class UserActivityCacheServiceTests : IDisposable
{
    private readonly UserActivityCacheService _cacheService;
    private readonly string _tempDir;

    public UserActivityCacheServiceTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jellyfin-helper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDir);

        var mockPluginLog = new Mock<IPluginLogService>();
        var mockLogger = new Mock<ILogger<UserActivityCacheService>>();

        _cacheService = new UserActivityCacheService(
            mockPaths.Object,
            mockPluginLog.Object,
            mockLogger.Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void LoadResult_NoCacheFile_ReturnsNull()
    {
        var result = _cacheService.LoadResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var original = new UserActivityResult
        {
            GeneratedAt = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
            TotalItemsWithActivity = 3,
            TotalUsersAnalyzed = 2,
            TotalPlayCount = 42,
            Items = new Collection<UserActivitySummary>
            {
                new()
                {
                    ItemId = Guid.NewGuid(),
                    ItemName = "Test Movie",
                    ItemType = "Movie",
                    Year = 2024,
                    Genres = ["Action", "Comedy"],
                    CommunityRating = 7.5f,
                    RuntimeTicks = 72000000000,
                    TotalPlayCount = 10,
                    UniqueViewers = 2,
                    MostRecentWatch = new DateTime(2025, 6, 14, 20, 0, 0, DateTimeKind.Utc),
                    AverageCompletionPercent = 85.5,
                    FavoriteCount = 1,
                    UserActivities = new Collection<UserItemActivity>
                    {
                        new()
                        {
                            UserId = Guid.NewGuid(),
                            UserName = "Alice",
                            PlayCount = 5,
                            LastPlayedDate = new DateTime(2025, 6, 14, 20, 0, 0, DateTimeKind.Utc),
                            PlaybackPositionTicks = 72000000000,
                            CompletionPercent = 100.0,
                            Played = true,
                            IsFavorite = true,
                            UserRating = 9.0
                        }
                    }
                }
            }
        };

        _cacheService.SaveResult(original);
        var loaded = _cacheService.LoadResult();

        Assert.NotNull(loaded);
        Assert.Equal(original.GeneratedAt, loaded!.GeneratedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.GeneratedAt.Kind);
        Assert.Equal(original.TotalItemsWithActivity, loaded.TotalItemsWithActivity);
        Assert.Equal(original.TotalUsersAnalyzed, loaded.TotalUsersAnalyzed);
        Assert.Equal(original.TotalPlayCount, loaded.TotalPlayCount);
        Assert.Single(loaded.Items);

        var item = loaded.Items[0];
        Assert.Equal("Test Movie", item.ItemName);
        Assert.Equal("Movie", item.ItemType);
        Assert.Equal(2024, item.Year);
        Assert.Equal(2, item.Genres.Length);
        Assert.Equal(7.5f, item.CommunityRating);
        Assert.Equal(10, item.TotalPlayCount);
        Assert.Equal(2, item.UniqueViewers);
        Assert.Equal(85.5, item.AverageCompletionPercent);
        Assert.Equal(1, item.FavoriteCount);
        Assert.Single(item.UserActivities);

        var activity = item.UserActivities[0];
        Assert.Equal("Alice", activity.UserName);
        Assert.Equal(5, activity.PlayCount);
        Assert.True(activity.Played);
        Assert.True(activity.IsFavorite);
        Assert.Equal(9.0, activity.UserRating);
    }

    [Fact]
    public void SaveResult_OverwritesPrevious()
    {
        var first = new UserActivityResult { TotalPlayCount = 10 };
        var second = new UserActivityResult { TotalPlayCount = 20 };

        _cacheService.SaveResult(first);
        _cacheService.SaveResult(second);

        var loaded = _cacheService.LoadResult();
        Assert.NotNull(loaded);
        Assert.Equal(20, loaded!.TotalPlayCount);
    }

    [Fact]
    public void LoadResult_CorruptedFile_ReturnsNull()
    {
        // Save first so the service produces its real cache file path, then corrupt it.
        _cacheService.SaveResult(new UserActivityResult());
        var cacheFile = Directory.GetFiles(_tempDir, "*.json").Single();
        File.WriteAllText(cacheFile, "NOT VALID JSON {{{");

        var result = _cacheService.LoadResult();
        Assert.Null(result);
    }

    [Fact]
    public void SaveResult_EmptyResult_SavesCorrectly()
    {
        var empty = new UserActivityResult
        {
            TotalItemsWithActivity = 0,
            TotalUsersAnalyzed = 0,
            TotalPlayCount = 0
        };

        _cacheService.SaveResult(empty);
        var loaded = _cacheService.LoadResult();

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.TotalItemsWithActivity);
        Assert.Empty(loaded.Items);
    }
}