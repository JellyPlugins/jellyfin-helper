using System;
using System.Collections.ObjectModel;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

public class RecommendationCacheServiceTests : IDisposable
{
    private readonly RecommendationCacheService _cacheService;
    private readonly string _tempDir;

    public RecommendationCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jellyfin-helper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDir);

        var mockPluginLog = new Mock<IPluginLogService>();
        var mockLogger = new Mock<ILogger<RecommendationCacheService>>();

        _cacheService = new RecommendationCacheService(
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
        catch
        {
            // best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadResults_NoCacheFile_ReturnsNull()
    {
        var result = _cacheService.LoadResults();
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var userId = Guid.NewGuid();
        var results = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = userId,
                UserName = "TestUser",
                GeneratedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                Recommendations = new Collection<RecommendedItem>
                {
                    new()
                    {
                        ItemId = Guid.NewGuid(),
                        Name = "Test Movie",
                        ItemType = "Movie",
                        Score = 0.85,
                        Reason = "Genre match",
                        Genres = new[] { "Action", "Sci-Fi" },
                        Year = 2024,
                        CommunityRating = 7.5f
                    }
                }
            }
        };

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Single(loaded);
        Assert.Equal(userId, loaded[0].UserId);
        Assert.Equal("TestUser", loaded[0].UserName);
        Assert.Single(loaded[0].Recommendations);
        Assert.Equal("Test Movie", loaded[0].Recommendations[0].Name);
        Assert.Equal(0.85, loaded[0].Recommendations[0].Score);
    }

    [Fact]
    public void SaveResults_EmptyCollection_RoundTrips()
    {
        var results = new Collection<RecommendationResult>();
        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public void LoadResults_CorruptFile_ReturnsNull()
    {
        var cacheFile = Path.Combine(_tempDir, "jellyfin-helper-recommendations-latest.json");
        File.WriteAllText(cacheFile, "{{{NOT VALID JSON!!!");

        var result = _cacheService.LoadResults();
        Assert.Null(result);
    }

    [Fact]
    public void SaveResults_OverwritesPrevious()
    {
        var first = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "First" }
        };
        _cacheService.SaveResults(first);

        var second = new Collection<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), UserName = "Second" },
            new() { UserId = Guid.NewGuid(), UserName = "Third" }
        };
        _cacheService.SaveResults(second);

        var loaded = _cacheService.LoadResults();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Second", loaded[0].UserName);
        Assert.Equal("Third", loaded[1].UserName);
    }

    [Fact]
    public void SaveResults_MultipleUsers_PreservesAll()
    {
        var results = new Collection<RecommendationResult>
        {
            new()
            {
                UserId = Guid.NewGuid(),
                UserName = "Alice",
                Recommendations = new Collection<RecommendedItem>
                {
                    new() { Name = "Movie A", Score = 0.9 },
                    new() { Name = "Movie B", Score = 0.7 }
                }
            },
            new()
            {
                UserId = Guid.NewGuid(),
                UserName = "Bob",
                Recommendations = new Collection<RecommendedItem>
                {
                    new() { Name = "Movie C", Score = 0.8 }
                }
            }
        };

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, loaded[0].Recommendations.Count);
        Assert.Single(loaded[1].Recommendations);
    }
}