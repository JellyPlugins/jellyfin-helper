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

    // ===== Error / edge cases =====

    [Fact]
    public void Load_CorruptedJson_ReturnsEmpty_AndDoesNotThrow()
    {
        // A tampered/corrupted cache file must degrade gracefully. The service caches an
        // empty result so subsequent Loads do not re-read the broken file repeatedly.
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        File.WriteAllText(_cacheFilePath, "{ this is not valid json ");

        var results = _sut.Load();

        Assert.Empty(results);
    }

    [Fact]
    public void Load_OversizedFile_DeletesFileAndReturnsEmpty()
    {
        // Files above the 50 MB safety cap are treated as tampered and MUST be removed
        // so the plugin does not sit in a repeated-deserialize loop. Rather than writing
        // 50 MB of real data (slow, expensive on CI), we use FileStream.SetLength to
        // create a SPARSE file that reports > 50 MB via FileInfo.Length without actually
        // consuming disk blocks on filesystems that support sparse files. On filesystems
        // that don't (e.g. FAT), SetLength still allocates the space — this is a one-shot
        // test so the cost is acceptable.
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        using (var stream = new FileStream(
                   _cacheFilePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            // 50 MiB + 1 byte — strictly above the cap.
            stream.SetLength((50L * 1024 * 1024) + 1);
        }

        Assert.True(File.Exists(_cacheFilePath));
        var lengthBefore = new FileInfo(_cacheFilePath).Length;
        Assert.True(lengthBefore > 50L * 1024 * 1024, $"expected > 50 MB file, got {lengthBefore}");

        // Re-instantiate the service to force a fresh disk read (the current one may
        // have already cached an empty result in memory from earlier test setup).
        using var freshSut = new DiscoveryCacheService(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryCacheService>>().Object);

        var results = freshSut.Load();

        // The oversized file must be reported as empty AND removed from disk so it
        // doesn't trip subsequent Load calls.
        Assert.Empty(results);
        Assert.False(File.Exists(_cacheFilePath),
            "the oversized cache file must be deleted after Load rejects it");
    }

    [Fact]
    public void Load_LegitimateSmallFile_IsNotAffectedByCap()
    {
        // Negative regression: the cap must not misfire on ordinary-sized cache files.
        // This complements the oversized-file test above; together they lock the
        // upper bound of the guard.
        _sut.Save([new DiscoveryResult { UserId = Guid.NewGuid() }]);
        Assert.True(File.Exists(_cacheFilePath));

        using var freshSut = new DiscoveryCacheService(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryCacheService>>().Object);

        var results = freshSut.Load();
        Assert.Single(results);
        Assert.True(File.Exists(_cacheFilePath), "legitimate cache files must survive Load");
    }

    [Fact]
    public async Task RemoveItemAsync_CancelledBeforeStart_ThrowsAndDoesNotMutate()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 99, MediaType = "movie" }]
            }
        ]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.RemoveItemAsync(99, "movie", userId, cts.Token));

        // The in-memory + on-disk state must be untouched.
        var loaded = _sut.Load();
        Assert.Single(loaded[0].Recommendations);
    }

    [Fact]
    public async Task MarkAsRequestedAsync_CancelledBeforeStart_ThrowsAndDoesNotMutate()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 99, MediaType = "movie" }]
            }
        ]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.MarkAsRequestedAsync(99, "movie", cts.Token));

        // Flag must NOT be flipped when the write was cancelled.
        var loaded = _sut.Load();
        Assert.False(loaded[0].Recommendations[0].AlreadyRequested);
    }

    [Fact]
    public void RemoveItem_MultipleMatchingItems_RemovesAllAtOriginalIndices()
    {
        // Duplicate item entries (same TmdbId + mediaType) all get removed in one call.
        // Verifies the batch-removal path where itemsToRemove.Count > 1.
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations =
                [
                    new DiscoveryRecommendation { TmdbId = 5, MediaType = "movie", Title = "A" },
                    new DiscoveryRecommendation { TmdbId = 6, MediaType = "movie", Title = "keep" },
                    new DiscoveryRecommendation { TmdbId = 5, MediaType = "movie", Title = "B" }
                ]
            }
        ]);

        _sut.RemoveItem(5, "movie", userId);

        var loaded = _sut.Load();
        var recs = loaded[0].Recommendations;
        Assert.Single(recs);
        Assert.Equal(6, recs[0].TmdbId);
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.

    // -----------------------------------------------------------------------
    // Post-oversize-recovery contract: after Load() ran the oversize-file guard
    // and DELETED the file, the service must have _memoryCache=[] rather than
    // null. The next Save() writes the caller's list, the next Load() must
    // see it. A regression that left _memoryCache=null after the delete would
    // still work for Load() (because EnsureLoadedLocked would re-run and see
    // "no file, init empty"), but the *specific bug* it guards against is a
    // future refactoring that assumes _memoryCache is non-null after Load
    // returned successfully — for example, a Save() path that skips the
    // detached-copy step because it thinks a valid cache is already loaded.
    //
    // NOTE: `Load_OversizedFile_DeletesFileAndReturnsEmpty` above already
    // exercises the delete-and-return-empty branch; this test complements it
    // by chaining Load → Save → Load through a single freshly-constructed
    // service so the post-recovery state is proven end-to-end.
    // -----------------------------------------------------------------------

    [Fact]
    public void Load_AfterOversizeRecovery_SubsequentSaveWorks()
    {
        var padSize = (50 * 1024 * 1024) + 1024;
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        using (var stream = new FileStream(
                   _cacheFilePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(padSize);
        }

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryCacheService>>();
        using var recoveringSut = new DiscoveryCacheService(pluginLog.Object, logger.Object);

        // Trigger the oversize recovery via Load().
        Assert.Empty(recoveringSut.Load());

        // Prove the service recovered: a fresh Save must succeed and round-trip.
        var userId = Guid.NewGuid();
        var saved = recoveringSut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 7, MediaType = "movie", Title = "PostRecovery" }]
            }
        ]);

        Assert.True(saved);
        var loaded = recoveringSut.Load();
        Assert.Single(loaded);
        Assert.Equal("PostRecovery", loaded[0].Recommendations[0].Title);
    }
}
