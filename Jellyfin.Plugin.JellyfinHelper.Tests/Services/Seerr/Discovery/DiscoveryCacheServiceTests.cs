using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for <see cref="DiscoveryCacheService"/>. Uses the shared plugin instance so
///     <c>Plugin.Instance.DataFolderPath</c> resolves to a real writable directory; each test
///     wipes the cache file up-front to stay independent from sibling tests.
/// </summary>
[Collection("ConfigOverride")]
public sealed class DiscoveryCacheServiceTests : IDisposable
{
    private const string CacheFileName = "jellyfin-helper-discovery-results.json";

    private readonly DiscoveryCacheService _sut;
    private readonly string _cacheFilePath;

    public DiscoveryCacheServiceTests()
    {
        ControllerTestFactory.InitializePluginInstance();

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryCacheService>>();
        _sut = new DiscoveryCacheService(pluginLog.Object, logger.Object);

        var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty;
        _cacheFilePath = Path.Join(dataPath, CacheFileName);

        SafeDelete(_cacheFilePath);
    }

    public void Dispose()
    {
        _sut.Dispose();
        SafeDelete(_cacheFilePath);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }

    // ===== Load / Save basics =====

    [Fact]
    public void Load_NoFileOnDisk_ReturnsEmpty()
    {
        var results = _sut.Load();
        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void Save_NullResults_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Save(null!));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsSingleUser()
    {
        var userId = Guid.NewGuid();
        var input = new List<DiscoveryResult>
        {
            new()
            {
                UserId = userId,
                Recommendations =
                [
                    new DiscoveryRecommendation
                    {
                        TmdbId = 42,
                        MediaType = "movie",
                        Title = "The Answer",
                        Score = 0.9
                    }
                ]
            }
        };

        var saved = _sut.Save(input);
        Assert.True(saved);

        var loaded = _sut.Load();
        Assert.Single(loaded);
        Assert.Equal(userId, loaded[0].UserId);
        Assert.Single(loaded[0].Recommendations);
        Assert.Equal(42, loaded[0].Recommendations[0].TmdbId);
    }

    [Fact]
    public void Save_DetachesFromCallerList()
    {
        // The cache must never alias the caller's list — subsequent mutations by the caller
        // must not leak into the persisted state.
        var input = new List<DiscoveryResult>
        {
            new() { UserId = Guid.NewGuid() }
        };
        _sut.Save(input);

        input.Add(new DiscoveryResult { UserId = Guid.NewGuid() });

        var loaded = _sut.Load();
        Assert.Single(loaded);
    }

    // ===== MarkAsRequested =====

    [Fact]
    public void MarkAsRequested_ExistingItem_UpdatesFlag()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations =
                [
                    new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie", Title = "A" },
                    new DiscoveryRecommendation { TmdbId = 200, MediaType = "movie", Title = "B" }
                ]
            }
        ]);

        _sut.MarkAsRequested(100, "movie");

        var loaded = _sut.Load();
        Assert.True(loaded[0].Recommendations[0].AlreadyRequested);
        Assert.False(loaded[0].Recommendations[1].AlreadyRequested);
    }

    [Fact]
    public void MarkAsRequested_UnknownItem_DoesNothing()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie" }]
            }
        ]);

        _sut.MarkAsRequested(999, "movie");

        var loaded = _sut.Load();
        Assert.False(loaded[0].Recommendations[0].AlreadyRequested);
    }

    [Fact]
    public void MarkAsRequested_MediaTypeMismatch_LeavesFlagUnchanged()
    {
        // TMDb movie IDs and TV IDs share namespaces on TMDb; the mediaType is REQUIRED to
        // avoid marking the wrong item when both types happen to reuse the same TmdbId.
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 42, MediaType = "movie" }]
            }
        ]);

        _sut.MarkAsRequested(42, "tv");

        var loaded = _sut.Load();
        Assert.False(loaded[0].Recommendations[0].AlreadyRequested);
    }

    [Fact]
    public async Task MarkAsRequestedAsync_UpdatesFlag_JustLikeSync()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 7, MediaType = "movie" }]
            }
        ]);

        await _sut.MarkAsRequestedAsync(7, "movie");

        var loaded = _sut.Load();
        Assert.True(loaded[0].Recommendations[0].AlreadyRequested);
    }

    [Fact]
    public void MarkAsRequested_EmptyCache_DoesNothing()
    {
        _sut.MarkAsRequested(1, "movie");
        Assert.Empty(_sut.Load());
    }

    // ===== RemoveItem =====

    [Fact]
    public void RemoveItem_ExistingItem_IsRemovedForOwningUser()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userA,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie" }]
            },
            new DiscoveryResult
            {
                UserId = userB,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie" }]
            }
        ]);

        _sut.RemoveItem(100, "movie", userA);

        var loaded = _sut.Load();
        var a = loaded.First(r => r.UserId == userA);
        var b = loaded.First(r => r.UserId == userB);
        Assert.Empty(a.Recommendations);
        Assert.Single(b.Recommendations); // Other user's list untouched.
    }

    [Fact]
    public void RemoveItem_UnknownUser_DoesNothing()
    {
        var owner = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = owner,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie" }]
            }
        ]);

        _sut.RemoveItem(1, "movie", Guid.NewGuid());

        var loaded = _sut.Load();
        Assert.Single(loaded[0].Recommendations);
    }

    [Fact]
    public void RemoveItem_MediaTypeMismatch_KeepsItem()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 5, MediaType = "movie" }]
            }
        ]);

        _sut.RemoveItem(5, "tv", userId);

        var loaded = _sut.Load();
        Assert.Single(loaded[0].Recommendations);
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesItem_JustLikeSync()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 12, MediaType = "movie" }]
            }
        ]);

        await _sut.RemoveItemAsync(12, "movie", userId);

        var loaded = _sut.Load();
        Assert.Empty(loaded[0].Recommendations);
    }

    [Fact]
    public void RemoveItem_EmptyCache_DoesNothing()
    {
        _sut.RemoveItem(1, "movie", Guid.NewGuid());
        Assert.Empty(_sut.Load());
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.
}