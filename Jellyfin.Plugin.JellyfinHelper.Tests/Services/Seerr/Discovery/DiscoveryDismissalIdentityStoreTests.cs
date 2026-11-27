using System.Collections;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

public sealed class DiscoveryDismissalIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-dismissal-index-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnknownUsersAndEmptyAdmissionsDoNotCreateFilesOrPublishInventedIdentities()
    {
        var reads = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, read: _ => { reads++; throw new IOException("unexpected read"); });
        Assert.Empty(store.Snapshot(Guid.NewGuid()));
        Assert.Empty(store.CachedSnapshot(Guid.NewGuid()));
        store.Remember(Guid.Empty, []);
        Assert.Equal(0, reads);
        Assert.False(Directory.Exists(_directory));
        Assert.Throws<ArgumentNullException>(() => store.Remember(Guid.NewGuid(), null!));
        Assert.Throws<IOException>(() => store.Remember(Guid.Empty, [new(700, "tv")]));
    }

    [Fact]
    public void TypedIdentitiesAndLatestExplicitTimesSurviveReloadWithoutMutatingPublishedSnapshots()
    {
        var user = Guid.NewGuid();
        var first = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var writes = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) => { writes++; AtomicFile.WriteAllText(path, contents); });
        store.Remember(user, [new(700, "tv", first), new(700, "movie")]);
        var original = store.Snapshot(user);
        store.Remember(user, [new(700, "tv", first.AddDays(1)), new(700, "movie", DateTime.SpecifyKind(first, DateTimeKind.Local))]);
        Assert.Equal(first, original[(700, "tv")]);
        Assert.Null(original[(700, "movie")]);
        Assert.Equal(first.AddDays(1), store.Snapshot(user)[(700, "tv")]);
        Assert.Equal(DateTimeNormalization.ToUtc(DateTime.SpecifyKind(first, DateTimeKind.Local)), store.Snapshot(user)[(700, "movie")]);
        store.Remember(user, [new(700, "tv", first), new(700, "movie")]);
        Assert.Equal(2, writes);
        var reloaded = new DiscoveryDismissalIdentityStore(_directory);
        Assert.Equal(store.Snapshot(user).OrderBy(pair => pair.Key.MediaType), reloaded.Snapshot(user).OrderBy(pair => pair.Key.MediaType));
        Assert.Same(reloaded.Snapshot(user), reloaded.CachedSnapshot(user));
        Assert.Empty(reloaded.Snapshot(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("owner")]
    [InlineData("missing-identities")]
    [InlineData("null-identity")]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("count")]
    public void InvalidExistingIdentityEvidenceNeverBecomesAnEmptyIndexOrGetsDeleted(string boundary)
    {
        var user = Guid.NewGuid();
        var path = PathFor(user);
        Directory.CreateDirectory(_directory);
        var contents = boundary switch
        {
            "null" => "null",
            "malformed" => "{",
            "owner" => JsonSerializer.Serialize(new { UserId = Guid.NewGuid(), Identities = new[] { new { TmdbId = 700, MediaType = "tv" } } }),
            "missing-identities" => JsonSerializer.Serialize(new { UserId = user }),
            "null-identity" => JsonSerializer.Serialize(new { UserId = user, Identities = new object?[] { null } }),
            "id" => JsonSerializer.Serialize(new { UserId = user, Identities = new[] { new { TmdbId = 0, MediaType = "tv" } } }),
            "type" => JsonSerializer.Serialize(new { UserId = user, Identities = new[] { new { TmdbId = 700, MediaType = "TV" } } }),
            _ => JsonSerializer.Serialize(new { UserId = user, Identities = new[] { new { TmdbId = 700, MediaType = "tv" }, new { TmdbId = 701, MediaType = "tv" } } })
        };
        File.WriteAllText(path, contents);
        var store = new DiscoveryDismissalIdentityStore(_directory, identityLimit: 1);
        Assert.Throws<IOException>(() => store.Snapshot(user));
        Assert.Throws<IOException>(() => store.Snapshot(user));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("filename")]
    [InlineData("empty-owner")]
    [InlineData("duplicate")]
    [InlineData("permission")]
    [InlineData("missing-during-enumeration")]
    public void AnUnavailableOrInvalidInventoryCannotBePublishedAsEmpty(string boundary)
    {
        if (boundary == "file")
        {
            File.WriteAllText(_directory, "original");
            Assert.Throws<IOException>(() => new DiscoveryDismissalIdentityStore(_directory));
            Assert.Equal("original", File.ReadAllText(_directory));
            return;
        }

        Directory.CreateDirectory(_directory);
        var name = boundary switch
        {
            "filename" => "not-an-owner",
            "empty-owner" => Guid.Empty.ToString("N"),
            _ => Guid.NewGuid().ToString("N")
        };
        var path = Path.Join(_directory, name + ".json");
        File.WriteAllText(path, "{}");
        Func<string, IEnumerable<string>> enumerate = boundary switch
        {
            "duplicate" => _ => [path, path],
            "permission" => _ => throw new UnauthorizedAccessException("unavailable"),
            "missing-during-enumeration" => _ => throw new DirectoryNotFoundException("inventory vanished after it was observed"),
            _ => _ => [path]
        };
        Assert.ThrowsAny<IOException>(() => new DiscoveryDismissalIdentityStore(_directory, enumerate: enumerate));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void FailedColdReadCanRecoverOnTheSameStoreWithoutLosingTheOriginalIndex()
    {
        var user = Guid.NewGuid();
        new DiscoveryDismissalIdentityStore(_directory).Remember(user, [new(700, "tv")]);
        var unavailable = true;
        var store = new DiscoveryDismissalIdentityStore(_directory, read: path => unavailable ? throw new UnauthorizedAccessException("offline") : File.OpenRead(path));
        Assert.Throws<IOException>(() => store.Snapshot(user));
        Assert.Empty(store.CachedSnapshot(user));
        unavailable = false;
        Assert.Contains((700, "tv"), store.Snapshot(user).Keys);
        Assert.Same(store.Snapshot(user), store.CachedSnapshot(user));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("io")]
    [InlineData("json")]
    public void FailedNewOwnerInitializationReleasesItsSlotAndRetryLoadsFreshEvidence(string failure)
    {
        var durableOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();
        new DiscoveryDismissalIdentityStore(_directory).Remember(durableOwner, [new(700, "tv")]);
        var original = File.ReadAllText(PathFor(durableOwner));
        var unavailable = true;
        var reads = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, read: path =>
        {
            if (path == PathFor(newOwner))
            {
                reads++;
                if (unavailable)
                {
                    if (failure == "permission") throw new UnauthorizedAccessException("offline");
                    if (failure == "io") throw new IOException("offline");
                    return new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{"));
                }
            }

            return File.OpenRead(path);
        }, byteBudget: 1024, identityLimit: 2, userLimit: 2);
        Assert.Throws<IOException>(() => store.Remember(newOwner, [new(800, "movie")]));
        Assert.Empty(store.CachedSnapshot(newOwner));
        Assert.Empty(store.Snapshot(newOwner));
        Assert.Equal(1, reads);
        Assert.False(File.Exists(PathFor(newOwner)));
        Assert.Contains((700, "tv"), store.Snapshot(durableOwner).Keys);
        Assert.Equal(original, File.ReadAllText(PathFor(durableOwner)));

        unavailable = false;
        // Evidence appearing after inventory must be read by a fresh lazy, not replaced by a stale empty index.
        AtomicFile.WriteAllText(PathFor(newOwner), JsonSerializer.Serialize(new { UserId = newOwner, Identities = new[] { new { TmdbId = 801, MediaType = "tv" } } }));
        store.Remember(newOwner, [new(800, "movie")]);
        Assert.Equal(2, reads);
        Assert.Equal(2, store.Snapshot(newOwner).Count);
        Assert.Contains((801, "tv"), store.Snapshot(newOwner).Keys);
        Assert.Contains((800, "movie"), store.Snapshot(newOwner).Keys);
        Assert.Throws<IOException>(() => store.Remember(Guid.NewGuid(), [new(900, "movie")]));
        var reloaded = new DiscoveryDismissalIdentityStore(_directory, byteBudget: 1024, identityLimit: 2, userLimit: 2);
        Assert.Contains((700, "tv"), reloaded.Snapshot(durableOwner).Keys);
        Assert.Equal(2, reloaded.Snapshot(newOwner).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedColdLoadWaiterCannotMakeTheLoaderPublishAnUnregisteredOrStaleIndex(bool replacement)
    {
        var user = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var reads = 0;
        var writes = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            Interlocked.Increment(ref writes);
            AtomicFile.WriteAllText(path, contents);
        }, read: path =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                releaseRead.Wait(cancellationToken);
                throw new FileNotFoundException("The cold read observed no user index.");
            }

            return File.OpenRead(path);
        }, byteBudget: 1024, identityLimit: 2, userLimit: 1);
        var loading = Task.Run(() => store.Remember(user, [new(800, "movie")]), CancellationToken.None);
        var waiter = new Thread(() =>
        {
            started.TrySetResult();
            try
            {
                store.Remember(user, [new(700, "tv")]);
                interrupted.TrySetException(new InvalidOperationException("The blocked initialization waiter was not interrupted."));
            }
            catch (ThreadInterruptedException exception)
            {
                interrupted.TrySetResult(exception);
            }
        }) { IsBackground = true };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            waiter.Start();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            // Thread interruption is a recoverable failure of a waiter, not of the active lazy factory.
            waiter.Interrupt();
            Assert.IsType<ThreadInterruptedException>(await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Empty(store.CachedSnapshot(user));
            Assert.False(File.Exists(PathFor(user)));
            if (replacement) store.Remember(user, [new(801, "tv")]);
            Assert.Equal(replacement ? 1 : 0, Volatile.Read(ref writes));
        }
        finally
        {
            releaseRead.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.Equal(2, Volatile.Read(ref reads));
        var snapshot = store.Snapshot(user);
        Assert.Equal(replacement ? 2 : 1, snapshot.Count);
        Assert.Contains((800, "movie"), snapshot.Keys);
        Assert.DoesNotContain((700, "tv"), snapshot.Keys);
        if (replacement) Assert.Contains((801, "tv"), snapshot.Keys);
        var reloaded = new DiscoveryDismissalIdentityStore(_directory, byteBudget: 1024, identityLimit: 2, userLimit: 1);
        Assert.Equal(snapshot.OrderBy(pair => pair.Key.MediaType), reloaded.Snapshot(user).OrderBy(pair => pair.Key.MediaType));
    }

    [Fact]
    public void AFailedWriteDoesNotPublishOrSpendTheOldIndexAndItsRetryRemainsPossible()
    {
        var user = Guid.NewGuid();
        var fail = false;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            if (fail) throw new IOException("offline");
            AtomicFile.WriteAllText(path, contents);
        });
        store.Remember(user, [new(700, "tv")]);
        var original = store.Snapshot(user);
        var contents = File.ReadAllText(PathFor(user));
        fail = true;
        Assert.Throws<IOException>(() => store.Remember(user, [new(701, "movie")]));
        Assert.Same(original, store.Snapshot(user));
        Assert.Equal(contents, File.ReadAllText(PathFor(user)));
        fail = false;
        store.Remember(user, [new(701, "movie")]);
        Assert.Equal(2, new DiscoveryDismissalIdentityStore(_directory).Snapshot(user).Count);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("identity")]
    [InlineData("bytes")]
    public void FailedNewOwnerAdmissionsLeaveUnloadedDurableOwnersReadableAndReleaseRetryCapacity(string boundary)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        new DiscoveryDismissalIdentityStore(_directory).Remember(first, [new(700, "tv")]);
        var original = File.ReadAllText(PathFor(first));
        var length = new FileInfo(PathFor(first)).Length;
        var unavailable = boundary == "write";
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            if (unavailable) throw new IOException("offline");
            AtomicFile.WriteAllText(path, contents);
        }, byteBudget: length * 2, identityLimit: 2, userLimit: 2);
        DiscoveryDismissalIdentityStore.Identity[] rejected = boundary switch
        {
            "identity" => [new(800, "TV")],
            "bytes" => [new(800, "tv"), new(801, "movie")],
            _ => [new(800, "tv")]
        };
        foreach (var user in new[] { second, third })
        {
            var failure = Assert.Throws<IOException>(() => store.Remember(user, rejected));
            if (boundary == "write") Assert.Equal("offline", failure.Message);
            Assert.False(File.Exists(PathFor(user)));
            Assert.Empty(store.Snapshot(user));
        }

        Assert.Empty(store.CachedSnapshot(first));
        Assert.Contains((700, "tv"), store.Snapshot(first).Keys);
        Assert.Equal(original, File.ReadAllText(PathFor(first)));
        unavailable = false;
        store.Remember(third, [new(800, "tv")]);
        Assert.Contains((800, "tv"), store.Snapshot(third).Keys);
        Assert.Throws<IOException>(() => store.Remember(second, [new(900, "movie")]));
        var reloaded = new DiscoveryDismissalIdentityStore(_directory, byteBudget: length * 2, identityLimit: 2, userLimit: 2);
        Assert.Contains((700, "tv"), reloaded.Snapshot(first).Keys);
        Assert.Contains((800, "tv"), reloaded.Snapshot(third).Keys);
    }

    [Fact]
    public async Task FailedColdReadsKeepInventoriedSlotsReservedWhileANewOwnerWriteIsStalled()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        new DiscoveryDismissalIdentityStore(_directory).Remember(first, [new(700, "tv")]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var unavailable = true;
        var reads = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            AtomicFile.WriteAllText(path, contents);
        }, read: path =>
        {
            Interlocked.Increment(ref reads);
            if (path == PathFor(first) && Volatile.Read(ref unavailable)) throw new UnauthorizedAccessException("offline");
            return File.OpenRead(path);
        }, byteBudget: 1024, identityLimit: 2, userLimit: 2);
        Assert.Throws<IOException>(() => store.Snapshot(first));
        var writing = Task.Run(() => store.Remember(second, [new(800, "movie")]), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => store.Remember(Guid.NewGuid(), [new(900, "movie")]));
            Assert.Equal(2, Volatile.Read(ref reads));
            Volatile.Write(ref unavailable, false);
            await Task.Run(() => Assert.Contains((700, "tv"), store.Snapshot(first).Keys), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        var reloaded = new DiscoveryDismissalIdentityStore(_directory, byteBudget: 1024, identityLimit: 2, userLimit: 2);
        Assert.Single(reloaded.Snapshot(first));
        Assert.Single(reloaded.Snapshot(second));
    }

    [Fact]
    public async Task QueuedSameOwnerWritersKeepTheirRegistrationAfterFailureAndCannotOverwriteANewerIndex()
    {
        var user = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var merging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWrite = new ManualResetEventSlim();
        using var releaseMerge = new ManualResetEventSlim();
        var writes = 0;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                releaseWrite.Wait(TestContext.Current.CancellationToken);
                throw new IOException("offline");
            }

            AtomicFile.WriteAllText(path, contents);
        }, byteBudget: 1024, identityLimit: 2, userLimit: 1);
        var failed = Task.Run(() => Assert.Throws<IOException>(() => store.Remember(user, [new(700, "tv")])), CancellationToken.None);
        Task? queued = null;
        Task? newer = null;
        var original = store.CachedSnapshot(user);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            queued = Task.Run(() => store.Remember(user, new PausedIdentity(new(701, "tv"), () =>
            {
                merging.TrySetResult();
                releaseMerge.Wait(TestContext.Current.CancellationToken);
            })), CancellationToken.None);
            // Fail only after the second writer has leased the old index and queued on its write lock.
            Assert.True(SpinWait.SpinUntil(() => ActiveWriterLeases(store, user) == 2 || queued.IsCompleted, TimeSpan.FromSeconds(30)));
            Assert.Equal(2, ActiveWriterLeases(store, user));
            releaseWrite.Set();
            await failed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await merging.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Same(original, store.Snapshot(user));
            Assert.Throws<IOException>(() => store.Remember(Guid.NewGuid(), [new(900, "movie")]));
            newer = Task.Run(() => store.Remember(user, [new(702, "movie")]), CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => ActiveWriterLeases(store, user) == 2 || newer.IsCompleted, TimeSpan.FromSeconds(30)));
            Assert.Equal(2, ActiveWriterLeases(store, user));
            Assert.Equal(1, Volatile.Read(ref writes));
        }
        finally
        {
            releaseWrite.Set();
            releaseMerge.Set();
            await failed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (newer is not null) await newer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.Empty(original);
        var snapshot = store.Snapshot(user);
        Assert.Equal(2, snapshot.Count);
        Assert.Contains((701, "tv"), snapshot.Keys);
        Assert.Contains((702, "movie"), snapshot.Keys);
        Assert.DoesNotContain((700, "tv"), snapshot.Keys);
        Assert.Same(snapshot, store.CachedSnapshot(user));
        var reloaded = new DiscoveryDismissalIdentityStore(_directory, byteBudget: 1024, identityLimit: 2, userLimit: 1);
        Assert.Equal(snapshot.OrderBy(pair => pair.Key.MediaType), reloaded.Snapshot(user).OrderBy(pair => pair.Key.MediaType));
    }

    [Fact]
    public async Task AStalledUserWriteKeepsItsOldPublicationReadableAndDoesNotBlockAnotherUsersIndex()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            if (stall && path == PathFor(first)) { entered.TrySetResult(); release.Wait(TestContext.Current.CancellationToken); }
            AtomicFile.WriteAllText(path, contents);
        });
        store.Remember(first, [new(700, "tv")]);
        store.Remember(second, [new(800, "movie")]);
        stall = true;
        var writing = Task.Run(() => store.Remember(first, [new(701, "tv")]), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Run(() =>
            {
                Assert.Single(store.Snapshot(first));
                store.Remember(second, [new(801, "movie")]);
                Assert.Equal(2, store.Snapshot(second).Count);
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        Assert.Equal(2, store.Snapshot(first).Count);
    }

    [Fact]
    public void IndependentCountAndByteBudgetsRejectOnlyNewEvidenceWithoutEvictingOldIdentities()
    {
        var first = Guid.NewGuid();
        var store = new DiscoveryDismissalIdentityStore(_directory, identityLimit: 2, userLimit: 1);
        store.Remember(first, [new(700, "tv"), new(700, "movie")]);
        Assert.Throws<IOException>(() => store.Remember(first, [new(701, "tv")]));
        Assert.Throws<IOException>(() => store.Remember(Guid.NewGuid(), [new(800, "movie")]));
        Assert.Throws<IOException>(() => store.Remember(first, [new(1, "tv"), new(2, "tv"), new(3, "tv")]));
        var length = new FileInfo(PathFor(first)).Length;
        Assert.Throws<IOException>(() => new DiscoveryDismissalIdentityStore(_directory, byteBudget: length - 1));
        var full = new DiscoveryDismissalIdentityStore(_directory, byteBudget: length);
        Assert.Throws<IOException>(() => full.Remember(first, [new(701, "tv")]));
        Assert.Equal(2, full.Snapshot(first).Count);
        Assert.Equal(2, new DiscoveryDismissalIdentityStore(_directory).Snapshot(first).Count);
    }

    [Fact]
    public void FullInventoryAndReaderLimitsAreBoundedEvenBeforeAnotherUserHasAFile()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var producer = new DiscoveryDismissalIdentityStore(_directory);
        producer.Remember(first, [new(700, "tv")]);
        producer.Remember(second, [new(800, "movie")]);
        Assert.Throws<IOException>(() => new DiscoveryDismissalIdentityStore(_directory, userLimit: 1));
        var full = new DiscoveryDismissalIdentityStore(_directory, userLimit: 2);
        Assert.Throws<IOException>(() => full.Remember(Guid.NewGuid(), [new(900, "movie")]));
        Assert.Single(full.Snapshot(first));
        Assert.Single(full.Snapshot(second));
        Assert.Single(new DiscoveryDismissalIdentityStore(_directory).Snapshot(second));
    }

    [Fact]
    public async Task TheReaderRegistryBoundsConcurrentNewOwnersBeforeTheirFilesArePublished()
    {
        var first = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var store = new DiscoveryDismissalIdentityStore(_directory, (path, contents) =>
        {
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            AtomicFile.WriteAllText(path, contents);
        }, userLimit: 1);
        var writing = Task.Run(() => store.Remember(first, [new(700, "tv")]), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => store.Remember(Guid.NewGuid(), [new(800, "movie")]));
            Assert.Empty(store.CachedSnapshot(first));
        }
        finally { release.Set(); await writing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        Assert.Single(store.Snapshot(first));
    }

    [Fact]
    public void AFileThatGrowsAfterInventoryIsRejectedBeforeUnboundedDeserialization()
    {
        var user = Guid.NewGuid();
        new DiscoveryDismissalIdentityStore(_directory).Remember(user, [new(700, "tv")]);
        var store = new DiscoveryDismissalIdentityStore(_directory);
        using (var file = File.OpenWrite(PathFor(user))) file.SetLength(128L * 1024 * 1024 + 1);
        Assert.Throws<IOException>(() => store.Snapshot(user));
        Assert.Throws<IOException>(() => new DiscoveryDismissalIdentityStore(_directory));
        Assert.True(File.Exists(PathFor(user)));
    }

    [Theory]
    [InlineData("bytes-zero")]
    [InlineData("bytes-high")]
    [InlineData("identities-zero")]
    [InlineData("identities-high")]
    [InlineData("users-zero")]
    [InlineData("users-high")]
    public void TestLimitsCannotDisableBoundsOrRaiseTheProductionCeilings(string boundary)
    {
        var byteBudget = boundary switch
        {
            "bytes-zero" => 0,
            "bytes-high" => DiscoveryDismissalIdentityStore.MaximumStoreBytes + 1,
            _ => DiscoveryDismissalIdentityStore.MaximumStoreBytes
        };
        var identityLimit = boundary switch
        {
            "identities-zero" => 0,
            "identities-high" => 4 * 1024 * 1024 + 1,
            _ => 4 * 1024 * 1024
        };
        var userLimit = boundary switch
        {
            "users-zero" => 0,
            "users-high" => 4097,
            _ => 4096
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryDismissalIdentityStore(_directory,
            byteBudget: byteBudget, identityLimit: identityLimit, userLimit: userLimit));
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        else if (File.Exists(_directory)) File.Delete(_directory);
        GC.SuppressFinalize(this);
    }

    private string PathFor(Guid user) => Path.Join(_directory, user.ToString("N") + ".json");

    private static int ActiveWriterLeases(DiscoveryDismissalIdentityStore store, Guid user)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = (Lock)typeof(DiscoveryDismissalIdentityStore).GetField("_gate", flags)!.GetValue(store)!;
        lock (gate)
        {
            var registry = (IDictionary)typeof(DiscoveryDismissalIdentityStore).GetField("_users", flags)!.GetValue(store)!;
            var registration = registry[user];
            if (registration is null || !(bool)registration.GetType().GetProperty("IsValueCreated")!.GetValue(registration)!) return 0;
            var index = registration.GetType().GetProperty("Value")!.GetValue(registration)!;
            return (int)index.GetType().GetProperty("ActiveWriters", flags)!.GetValue(index)!;
        }
    }

    private sealed class PausedIdentity(DiscoveryDismissalIdentityStore.Identity identity, Action pause) : IReadOnlyCollection<DiscoveryDismissalIdentityStore.Identity>
    {
        public int Count => 1;

        public IEnumerator<DiscoveryDismissalIdentityStore.Identity> GetEnumerator()
        {
            pause();
            yield return identity;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
