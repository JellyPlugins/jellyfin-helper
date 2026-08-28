using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for DiscoveryCacheService. Uses the shared plugin instance so Plugin.Instance.DataFolderPath resolves to a real writable directory; each test wipes the cache file up-front to stay independent from sibling tests.
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

        var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty;
        _cacheFilePath = Path.Join(dataPath, CacheFileName);

        // Delete any stale cache file BEFORE constructing _sut so the service starts
        // with no pre-warmed in-memory state from a previous test.
        SafeDelete(_cacheFilePath);

        var pluginLog = new Mock<IPluginLogService>();
        var logger = new Mock<ILogger<DiscoveryCacheService>>();
        _sut = new DiscoveryCacheService(pluginLog.Object, logger.Object);
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
        // The cache must never alias the caller's list - subsequent mutations by the caller
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

    [Fact]
    public void MarkAsRequested_WriteFailure_RollsBackInMemoryMutationAndDoesNotThrow()
    {
        // REGRESSION GUARD (v3.0.0.0): MarkAsRequestedLocked applies AlreadyRequested=true in memory BEFORE the atomic write, then rolls it back if the write fails.
        _sut.Save([
            new DiscoveryResult
            {
                UserId = Guid.NewGuid(),
                Recommendations = [new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie", Title = "A" }]
            }
        ]);
        Assert.True(File.Exists(_cacheFilePath));
        Assert.False(_sut.Load()[0].Recommendations[0].AlreadyRequested);

        SafeDelete(_cacheFilePath);
        Directory.CreateDirectory(_cacheFilePath);

        try
        {
            var ex = Record.Exception(() => _sut.MarkAsRequested(100, "movie"));

            Assert.Null(ex);
            Assert.False(
                _sut.Load()[0].Recommendations[0].AlreadyRequested,
                "a failed persist must roll back the in-memory AlreadyRequested mutation");
        }
        finally
        {
            if (Directory.Exists(_cacheFilePath))
            {
                Directory.Delete(_cacheFilePath, recursive: true);
            }
        }
    }

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
        // Files above the 50 MB safety cap are treated as tampered and MUST be removed so the plugin does not sit in a repeated-deserialize loop.
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        using (var stream = new FileStream(
                   _cacheFilePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            // 50 MiB + 1 byte - strictly above the cap.
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
        // The cap must not misfire on ordinary-sized cache files.
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

    [Fact]
    public void Load_CacheFileContainsNullEntries_FiltersNullsAndReturnsValidEntries()
    {
        // A JSON array with a null element (e.g. [null, {valid}]) must not produce NullReferenceException when downstream code accesses r.UserId on every element.
        var userId = Guid.NewGuid();
        var json = $$"""[null, {"UserId":"{{userId}}","Recommendations":[]}]""";
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        File.WriteAllText(_cacheFilePath, json);

        // Re-instantiate so there is no pre-warmed _memoryCache and the file is actually read.
        using var freshSut = new DiscoveryCacheService(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryCacheService>>().Object);

        var results = freshSut.Load();

        Assert.Single(results);
        Assert.Equal(userId, results[0].UserId);
    }

    [Fact]
    public async Task MarkAsRequestedAsync_WithUserId_OnlyMarksThatUsersEntry()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userA,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 42, MediaType = "movie" }]
            },
            new DiscoveryResult
            {
                UserId = userB,
                Recommendations = [new DiscoveryRecommendation { TmdbId = 42, MediaType = "movie" }]
            }
        ]);

        await _sut.MarkAsRequestedAsync(42, "movie", userA, CancellationToken.None);

        var loaded = _sut.Load();
        var recA = loaded.First(r => r.UserId == userA).Recommendations[0];
        var recB = loaded.First(r => r.UserId == userB).Recommendations[0];
        Assert.True(recA.AlreadyRequested, "user A's entry must be marked");
        Assert.False(recB.AlreadyRequested, "user B's entry must not be touched");
    }

    [Fact]
    public async Task MarkAsRequestedAsync_WithUserId_DoesNotMarkOtherTmdbIds()
    {
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations =
                [
                    new DiscoveryRecommendation { TmdbId = 1, MediaType = "movie" },
                    new DiscoveryRecommendation { TmdbId = 2, MediaType = "movie" }
                ]
            }
        ]);

        await _sut.MarkAsRequestedAsync(1, "movie", userId, CancellationToken.None);

        var loaded = _sut.Load();
        var recs = loaded[0].Recommendations;
        Assert.True(recs.First(r => r.TmdbId == 1).AlreadyRequested);
        Assert.False(recs.First(r => r.TmdbId == 2).AlreadyRequested);
    }

    [Fact]
    public async Task MarkAsRequestedAsync_WithUserId_CancelledBeforeStart_ThrowsAndDoesNotMutate()
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
            _sut.MarkAsRequestedAsync(99, "movie", userId, cts.Token));

        var loaded = _sut.Load();
        Assert.False(loaded[0].Recommendations[0].AlreadyRequested);
    }

    [Fact]
    public void RemoveItem_WriteFailure_RollsBackRemovalPreservingOriginalOrderAndDoesNotThrow()
    {
        // A failed persist must NOT silently drop the item AND must NOT reorder the surviving recommendations: RemoveItemLocked reinserts the removed item at its ORIGINAL index so a subsequent Save can't persist a shuffled ranking.
        var userId = Guid.NewGuid();
        _sut.Save([
            new DiscoveryResult
            {
                UserId = userId,
                Recommendations =
                [
                    new DiscoveryRecommendation { TmdbId = 100, MediaType = "movie", Title = "target" },
                    new DiscoveryRecommendation { TmdbId = 200, MediaType = "movie", Title = "sibling" }
                ]
            }
        ]);
        Assert.True(File.Exists(_cacheFilePath));

        SafeDelete(_cacheFilePath);
        Directory.CreateDirectory(_cacheFilePath);

        try
        {
            var ex = Record.Exception(() => _sut.RemoveItem(100, "movie", userId));

            Assert.Null(ex);
            var recs = _sut.Load()[0].Recommendations;
            Assert.Equal(2, recs.Count);
            Assert.Equal(100, recs[0].TmdbId); // restored at its original index 0
            Assert.Equal(200, recs[1].TmdbId); // sibling order intact
        }
        finally
        {
            if (Directory.Exists(_cacheFilePath))
            {
                Directory.Delete(_cacheFilePath, recursive: true);
            }
        }
    }

    [Fact]
    public void RemoveItem_LoadThrowsCorruptedJson_IsSwallowedAndCacheReset()
    {
        // If EnsureLoadedLocked's deserialize throws before the write try (corrupt file, no pre-warmed cache), the broad outer catch must swallow it and reset the cache to [] so the service stays usable rather than propagating the JsonException.
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        File.WriteAllText(_cacheFilePath, "{ not valid json ");

        using var freshSut = new DiscoveryCacheService(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryCacheService>>().Object);

        var ex = Record.Exception(() => freshSut.RemoveItem(1, "movie", Guid.NewGuid()));

        Assert.Null(ex);
        var loaded = freshSut.Load();
        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public void Save_WriteFailure_ReturnsFalseWithoutThrowing()
    {
        // A write error (here: the cache path is a directory, so AtomicFile's File.Move throws IOException) must be caught and reported as false - distinct from the null-arg guard, which throws ArgumentNullException.
        SafeDelete(_cacheFilePath);
        Directory.CreateDirectory(_cacheFilePath);

        try
        {
            bool result = true;
            var ex = Record.Exception(() => result = _sut.Save([
                new DiscoveryResult { UserId = Guid.NewGuid() }
            ]));

            Assert.Null(ex);
            Assert.False(result);
        }
        finally
        {
            if (Directory.Exists(_cacheFilePath))
            {
                Directory.Delete(_cacheFilePath, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_OversizedFileUndeletable_ReturnsEmptyAndSwallowsDeleteFailure()
    {
        // The oversize guard's File.Delete is best-effort: if the file is locked by another handle, the delete failure must be swallowed and Load must still return [].
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
        using (var stream = new FileStream(
                   _cacheFilePath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength((50L * 1024 * 1024) + 1);
        }

        // Keep an exclusive handle open so File.Delete inside the oversize guard throws.
        using (new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var freshSut = new DiscoveryCacheService(
                new Mock<IPluginLogService>().Object,
                new Mock<ILogger<DiscoveryCacheService>>().Object);

            var ex = Record.Exception(() =>
            {
                var results = freshSut.Load();
                Assert.NotNull(results);
                Assert.Empty(results);
            });

            Assert.Null(ex);
        }
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.

    // Post-oversize-recovery contract: after Load() ran the oversize-file guard and DELETED the file, the service must have _memoryCache=[] rather than null.

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
