using System.Collections.ObjectModel;
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
        // UserName is [JsonIgnore] - not persisted to disk; resolves from IUserManager at API layer
        Assert.Equal(string.Empty, activity.UserName);
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

    // -----------------------------------------------------------------------
    // Guard-branch coverage (was untested before this batch):
    //   * SaveResult(null) - must throw before the lock is taken so the caller
    //     surfaces the NRE via its normal test/observability tooling, not as
    //     a corrupted "empty JSON" cache file.
    //   * LoadResult on a JSON file that deserializes to literal null - the
    //     helper is expected to log a warning AND return null so the caller
    //     falls through to the "no cache" recovery path instead of crashing
    //     on a null-dereference.
    //   * SaveResult when the parent directory is missing - the helper must
    //     auto-create the directory chain so a first-boot deployment doesn't
    //     lose its very first result.
    // -----------------------------------------------------------------------

    [Fact]
    public void SaveResult_NullResult_ThrowsArgumentNullException()
    {
        // BUG GUARD: the guard clause is a "throw before lock" - if a maintainer
        // moved it inside the lock, a concurrent LoadResult would still spin
        // waiting for the lock while the NRE propagated. The pre-lock throw
        // means we fail fast and do not hold the semaphore on the failure path.
        Assert.Throws<ArgumentNullException>(() => _cacheService.SaveResult(null!));
    }

    [Fact]
    public void LoadResult_FileContainsLiteralNull_LogsWarningAndReturnsNull()
    {
        // BUG GUARD: a corrupt-but-parseable JSON payload containing the
        // literal token `null` deserializes to a null UserActivityResult.
        // Historically this collapsed with the "no cache file" branch and
        // silently returned null - no diagnostics for the operator. The
        // implementation now logs a warning specifically for this case,
        // pinned by this test with a mock captured on the IPluginLogService.
        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDir);
        var mockPluginLog = new Mock<IPluginLogService>();
        var warningCount = 0;
        mockPluginLog.Setup(l => l.LogWarning(
                "UserActivityCache",
                It.Is<string>(m => m.Contains("deserialized to null", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()))
            .Callback(() => warningCount++);
        var mockLogger = new Mock<ILogger<UserActivityCacheService>>();

        var service = new UserActivityCacheService(mockPaths.Object, mockPluginLog.Object, mockLogger.Object);

        // Write a valid-JSON-but-null-typed cache file directly at the path the service uses.
        var cacheFile = Path.Join(_tempDir, "jellyfin-helper-useractivity-latest.json");
        File.WriteAllText(cacheFile, "null");

        var loaded = service.LoadResult();

        Assert.Null(loaded);
        Assert.Equal(1, warningCount);
    }

    [Fact]
    public void SaveResult_ParentDirectoryMissing_CreatesItAndPersists()
    {
        // BUG GUARD: on a fresh install DataPath may exist but a nested cache
        // directory may not. The helper must create the directory chain in
        // one shot, otherwise the very first scheduled-task result silently
        // vanishes (the AtomicFile.WriteAllText would throw DirectoryNotFoundException
        // and the outer catch would only log a warning).
        var nestedDir = Path.Join(_tempDir, "nested", "deep", "cache");
        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(nestedDir);
        var mockPluginLog = new Mock<IPluginLogService>();
        var mockLogger = new Mock<ILogger<UserActivityCacheService>>();

        Assert.False(Directory.Exists(nestedDir), "precondition: nested cache dir must not exist");

        var service = new UserActivityCacheService(mockPaths.Object, mockPluginLog.Object, mockLogger.Object);
        service.SaveResult(new UserActivityResult { TotalPlayCount = 7 });

        Assert.True(Directory.Exists(nestedDir), "SaveResult must create the missing directory chain");
        var loaded = service.LoadResult();
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.TotalPlayCount);
    }

    [Fact]
    public void LoadResult_EmptyFile_ReturnsNullAndLogsWarning()
    {
        // BUG GUARD: an empty file (e.g. a zero-byte artefact of a crash during
        // a previous save) must NOT crash the caller with a JsonException at the
        // top of the LoadResult method - the try/catch must swallow it and
        // return null. Without this test a regression that narrows the catch
        // filter would silently break next-boot recovery. We also lock the
        // "logs a warning" contract by constructing a service with a captured
        // IPluginLogService mock - the class-level _cacheService is built with
        // its own log-service mock the tests cannot reach, so we build a
        // dedicated instance here (same pattern as the literal-null test above).
        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDir);
        var mockPluginLog = new Mock<IPluginLogService>();
        var warningCount = 0;
        mockPluginLog.Setup(l => l.LogWarning(
                "UserActivityCache",
                It.IsAny<string>(),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()))
            .Callback(() => warningCount++);
        var mockLogger = new Mock<ILogger<UserActivityCacheService>>();

        var service = new UserActivityCacheService(mockPaths.Object, mockPluginLog.Object, mockLogger.Object);

        var cacheFile = Path.Join(_tempDir, "jellyfin-helper-useractivity-latest.json");
        File.WriteAllText(cacheFile, string.Empty);

        var loaded = service.LoadResult();

        Assert.Null(loaded);
        // Contract: the empty-file branch MUST emit at least one warning so operators
        // can spot the corruption in logs. Zero warnings would mean the catch filter
        // silently swallowed the JsonException without diagnostics.
        Assert.True(warningCount >= 1, $"expected at least one warning to be logged, got {warningCount}");
    }
}
