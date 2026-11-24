using System.Collections.ObjectModel;
using Jellyfin.Data;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
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

    [Fact]
    public void CachedResultsExcludeInactiveAccountsAndUserHiddenCopiesWithoutChangingTheSavedInput()
    {
        var enabled = new Jellyfin.Database.Implementations.Entities.User("enabled", "p", "r") { Id = Guid.NewGuid() };
        var disabled = new Jellyfin.Database.Implementations.Entities.User("disabled", "p", "r") { Id = Guid.NewGuid() };
        disabled.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        var deleted = Guid.NewGuid();
        var users = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        users.Setup(manager => manager.GetUserById(enabled.Id)).Returns(enabled);
        users.Setup(manager => manager.GetUserById(disabled.Id)).Returns(disabled);
        var visible = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/visible/movie.mkv" };
        var hidden = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/hidden/movie.mkv" };
        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(manager => manager.GetItemById(visible.Id)).Returns(visible);
        library.Setup(manager => manager.GetItemById(hidden.Id)).Returns(hidden);
        library.Setup(manager => manager.GetItemIds(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>())).Returns([visible.Id]);
        var inputs = new[] { enabled.Id, disabled.Id, deleted }.Select(user => new RecommendationResult
        {
            UserId = user, Recommendations = [new RecommendedItem { ItemId = hidden.Id, TmdbId = 900 }, new RecommendedItem { ItemId = visible.Id, TmdbId = 900 }],
            RankingDiagnostics = new CandidateRankingDiagnostics { IsComplete = true, ScoredCandidateCount = 2, Entries = [new CandidateRankingEntry { ItemId = hidden.Id, Name = "Hidden title" }, new CandidateRankingEntry { ItemId = visible.Id, InitialScore = .7 }] }
        }).ToArray();
        _cacheService.SaveResults(inputs);
        var path = Path.Join(_tempDir, "jellyfin-helper-recommendations-latest.json");
        var before = File.ReadAllBytes(path);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.DataPath).Returns(_tempDir);
        var reader = new RecommendationCacheService(paths.Object, new Mock<IPluginLogService>().Object, new Mock<ILogger<RecommendationCacheService>>().Object, library.Object, userManager: users.Object);
        var result = Assert.Single(reader.LoadResults()!);
        Assert.Equal(enabled.Id, result.UserId);
        Assert.Equal(visible.Id, Assert.Single(result.Recommendations).ItemId);
        Assert.Equal(visible.Id, Assert.Single(result.RankingDiagnostics!.Entries).ItemId);
        Assert.Equal(.7, result.RankingDiagnostics.Entries[0].InitialScore);
        Assert.False(result.RankingDiagnostics.IsComplete);
        library.Verify(manager => manager.ConfigureUserAccess(It.Is<MediaBrowser.Controller.Entities.InternalItemsQuery>(query => query.User == enabled), enabled), Times.Once);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CacheReadsRecheckCurrentNestedExclusionsVirtualStubsAndRemovedItemsWithoutRewritingSavedPredictions()
    {
        var config = new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration();
        var configService = new Mock<Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess.IPluginConfigurationService>();
        configService.Setup(service => service.GetConfiguration()).Returns(config);
        var library = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Allowed", Locations = ["/media"] },
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Excluded", Locations = ["/media/excluded"] }
        ]);
        var allowed = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/allowed.mkv" };
        var excluded = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/excluded/movie.mkv" };
        var stub = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/media/stub.mkv", IsVirtualItem = true };
        var removed = Guid.NewGuid();
        foreach (var item in new[] { allowed, excluded, stub }) library.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
        var predictedAt = DateTime.UtcNow.AddDays(-1);
        var original = new RecommendationResult
        {
            UserId = Guid.NewGuid(), GeneratedAt = predictedAt,
            Recommendations = new Collection<RecommendedItem>(new[] { allowed.Id, excluded.Id, stub.Id, removed }.Select(id => new RecommendedItem { ItemId = id, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .8 } }).ToList()),
            TrainingContrasts = [new RecommendedItem { ItemId = excluded.Id, ScoringFeatures = new CandidateFeatures { GenreSimilarity = .6 } }]
        };
        _cacheService.SaveResults([original]);
        var path = Path.Join(_tempDir, "jellyfin-helper-recommendations-latest.json");
        var bytes = File.ReadAllBytes(path);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.DataPath).Returns(_tempDir);
        var reader = new RecommendationCacheService(paths.Object, new Mock<IPluginLogService>().Object, new Mock<ILogger<RecommendationCacheService>>().Object, library.Object, configService.Object, Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.TestMockFactory.CreateVirtualItemDetector());
        Assert.True(new HashSet<Guid>([allowed.Id, excluded.Id]).SetEquals(Assert.Single(reader.LoadResults()!).Recommendations.Select(item => item.ItemId)));
        config.ExcludedLibraries = "Excluded";
        var scoped = Assert.Single(reader.LoadResults()!);
        Assert.Equal(allowed.Id, Assert.Single(scoped.Recommendations).ItemId);
        Assert.Empty(scoped.TrainingContrasts!);
        Assert.Equal(.8, scoped.Recommendations[0].ScoringFeatures!.GenreSimilarity);
        Assert.Equal(predictedAt, scoped.GeneratedAt);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(4, original.Recommendations.Count);
        library.Setup(manager => manager.GetItemById(allowed.Id)).Throws(new IOException("test current library unavailable"));
        Assert.Null(reader.LoadResults());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

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
            Directory.Delete(_tempDir, true);
        }
        catch (DirectoryNotFoundException)
        {
            // Best-effort cleanup - directory may already be gone
        }
        catch (IOException)
        {
            // Best-effort cleanup - don't fail the test run for transient IO issues
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup - don't fail the test run for access issues
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
                TrainingContrasts = [new RecommendedItem { ItemId = Guid.NewGuid(), ItemType = "Movie", ScoringFeatures = new CandidateFeatures { LanguageAffinity = 0.8, SubtitleLanguageAffinity = 0.3 } }],
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
                        CommunityRating = 7.5f,
                        ScoringFeatures = new CandidateFeatures { IsExternalCandidate = true, GenreSimilarity = 2.0, CollaborativeScore = double.NaN, LanguageAffinity = 0.7 }
                    }
                }
            }
        };

        _cacheService.SaveResults(results);
        var loaded = _cacheService.LoadResults();

        Assert.NotNull(loaded);
        var result = Assert.Single(loaded!);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("TestUser", result.UserName);
        Assert.Equal("Ensemble", result.ScoringStrategy);
        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal("Test Movie", recommendation.Name);
        Assert.Equal(0.85, recommendation.Score);
        Assert.Equal(new[] { "Action", "Thriller" }, recommendation.Genres);
        Assert.NotNull(recommendation.ScoringFeatures);
        Assert.True(recommendation.ScoringFeatures.IsExternalCandidate);
        Assert.Equal(1.0, recommendation.ScoringFeatures.GenreSimilarity);
        Assert.Equal(0.0, recommendation.ScoringFeatures.CollaborativeScore);
        Assert.Equal(0.7, recommendation.ScoringFeatures.LanguageAffinity);
        Assert.NotSame(results[0].Recommendations[0].ScoringFeatures, recommendation.ScoringFeatures);
        Assert.NotNull(result.TrainingContrasts);
        var contrast = Assert.Single(result.TrainingContrasts);
        Assert.Equal(0.8, contrast.ScoringFeatures!.LanguageAffinity);
        Assert.Equal(0.3, contrast.ScoringFeatures.SubtitleLanguageAffinity);
        Assert.NotSame(results[0].TrainingContrasts![0].ScoringFeatures, contrast.ScoringFeatures);
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
        var result = Assert.Single(loaded!);
        Assert.Equal("User2", result.UserName);
        Assert.Equal("Movie2", result.Recommendations[0].Name);
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
