using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation;

/// <summary>
///     Tests for <see cref="RecommendationCacheService" />.
/// </summary>
public sealed class RecommendationCacheServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecommendationCacheService _cacheService;

    public RecommendationCacheServiceTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jf-helper-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
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
        catch (DirectoryNotFoundException)
        {
            // Best-effort cleanup — TOCTOU race between Exists check and Delete
        }
        catch (IOException)
        {
            // Best-effort cleanup — don't fail the test run for transient IO issues
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup — don't fail the test run for access issues
        }
    }

    [Fact]
    public void LoadResults_NoCacheFile_ReturnsNull()
    {
        var result = _cacheService.LoadResults();

        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesData()
    {
        var userId = Guid.NewGuid();
        var results = new Collection<RecommendationResult>
        {
            new RecommendationResult
            {
                UserId = userId,
                UserName = "TestUser",
                ScoringStrategy = "Ensemble",
                ScoringStrategyKey = "strategyEnsemble",
                GeneratedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                Recommendations = new Collection<RecommendedItem>
                {
                    new RecommendedItem
                    {
                        ItemId = Guid.NewGuid(),
                        Name = "Test Movie",
                        ItemType = "Movie",
                        Score = 0.85,
                        Reason = "Because you like Action",
                        ReasonKey = "reasonGenre",
                        Genres = new[] { "Action", "Thriller" },
                        Year = 2024,
                        CommunityRating = 7.5f
                    }
                }
            }
        };

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Single(loaded!);
        Assert.Equal(userId, loaded[0].UserId);
        Assert.Equal("TestUser", loaded[0].UserName);
        Assert.Equal("Ensemble", loaded[0].ScoringStrategy);
        Assert.Single(loaded[0].Recommendations);
        Assert.Equal("Test Movie", loaded[0].Recommendations[0].Name);
        Assert.Equal(0.85, loaded[0].Recommendations[0].Score);
        Assert.Equal(new[] { "Action", "Thriller" }, loaded[0].Recommendations[0].Genres);
    }

    [Fact]
    public void SaveResults_OverwritesPreviousCache()
    {
        var results1 = new Collection<RecommendationResult>
        {
            new RecommendationResult
            {
                UserId = Guid.NewGuid(),
                UserName = "User1",
                Recommendations = new Collection<RecommendedItem>
                {
                    new RecommendedItem { Name = "Movie1", Score = 0.5 }
                }
            }
        };

        var results2 = new Collection<RecommendationResult>
        {
            new RecommendationResult
            {
                UserId = Guid.NewGuid(),
                UserName = "User2",
                Recommendations = new Collection<RecommendedItem>
                {
                    new RecommendedItem { Name = "Movie2", Score = 0.9 }
                }
            }
        };

        _cacheService.SaveResults(results1);
        _cacheService.SaveResults(results2);

        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Single(loaded!);
        Assert.Equal("User2", loaded[0].UserName);
        Assert.Equal("Movie2", loaded[0].Recommendations[0].Name);
    }

    [Fact]
    public void SaveResults_EmptyCollection_SavesAndLoadsCorrectly()
    {
        var results = new Collection<RecommendationResult>();

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Empty(loaded!);
    }

    [Fact]
    public void SaveResults_MultipleUsers_AllPreserved()
    {
        var results = new Collection<RecommendationResult>
        {
            new RecommendationResult
            {
                UserId = Guid.NewGuid(),
                UserName = "Alice",
                Recommendations = new Collection<RecommendedItem>
                {
                    new RecommendedItem { Name = "Movie A", Score = 0.8 },
                    new RecommendedItem { Name = "Movie B", Score = 0.6 }
                }
            },
            new RecommendationResult
            {
                UserId = Guid.NewGuid(),
                UserName = "Bob",
                Recommendations = new Collection<RecommendedItem>
                {
                    new RecommendedItem { Name = "Movie C", Score = 0.7 }
                }
            }
        };

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Contains(loaded, r => r.UserName == "Alice");
        Assert.Contains(loaded, r => r.UserName == "Bob");

        var alice = loaded.First(r => r.UserName == "Alice");
        Assert.Equal(2, alice.Recommendations.Count);
    }

    [Fact]
    public void LoadResults_CorruptedFile_ReturnsNull()
    {
        // First, save valid results through the service so the correct cache file is created
        _cacheService.SaveResults(new Collection<RecommendationResult>());

        // Locate the generated cache file and corrupt it
        var cacheFilePath = Directory.GetFiles(_tempDir, "*.json").Single();
        File.WriteAllText(cacheFilePath, "{ this is not valid json !!!");

        var result = _cacheService.LoadResults();

        Assert.Null(result);
    }
}