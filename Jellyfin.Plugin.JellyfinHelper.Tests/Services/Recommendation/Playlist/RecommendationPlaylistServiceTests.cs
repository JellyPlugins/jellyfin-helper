using System.Collections.ObjectModel;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Interfaces;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Playlist;

public sealed class RecommendationPlaylistServiceTests : IDisposable
{
    private readonly Mock<IPlaylistManager> _playlistManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger<RecommendationPlaylistService>> _loggerMock = new();
    private readonly Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> _createdPlaylists = new();
    private readonly HashSet<Guid> _populatedPlaylists = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _registrationSignals = new();
    private readonly string _recoveryDirectory = Path.Join(Path.GetTempPath(), "jfh-playlist-recovery-" + Guid.NewGuid().ToString("N"));
    private long _registrationSequence;

    public void Dispose()
    {
        if (Directory.Exists(_recoveryDirectory)) Directory.Delete(_recoveryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AmbiguityResolutionRequiresAnOwnerAndLegacyProvidersRemainConservative()
    {
        var service = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveDeferredCreationAsync(Guid.Empty, TestContext.Current.CancellationToken));
        Assert.False(await service.ResolveDeferredCreationAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        IRecommendationPlaylistService legacy = new LegacyPlaylistProvider();
        Assert.False(await legacy.ResolveDeferredCreationAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        _playlistManagerMock.VerifyNoOtherCalls();
        _libraryManagerMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullPlaylistJournalCannotSpendSpaceFromAnUncommittedOrFailedShrinkingRewrite(bool fails)
    {
        Directory.CreateDirectory(_recoveryDirectory);
        var firstOwner = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 256).Select(index => new PlaylistCreationJournal.Attempt(Guid.NewGuid(), index == 0 ? firstOwner : Guid.NewGuid(), System.Collections.Immutable.ImmutableArray<Guid>.Empty, Guid.NewGuid())).ToArray();
        foreach (var attempt in attempts)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(attempt, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options);
            var path = Path.Join(_recoveryDirectory, attempt.Owner.ToString("N") + ".json");
            File.WriteAllText(path, json + new string(' ', 256 * 1024 - System.Text.Encoding.UTF8.GetByteCount(json)), new System.Text.UTF8Encoding(false));
            Assert.Equal(256 * 1024, new FileInfo(path).Length);
        }

        PlaylistCreationJournal? journal = null;
        journal = new PlaylistCreationJournal(_recoveryDirectory, (path, contents) =>
        {
            Assert.Throws<IOException>(() => journal!.Begin(Guid.NewGuid(), new HashSet<Guid>()));
            if (fails) throw new IOException("shrinking rewrite failed");
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        });
        Assert.Equal(256, journal.Pending().Length);
        Assert.Throws<IOException>(() => journal.Begin(Guid.NewGuid(), new HashSet<Guid>()));
        if (fails)
        {
            Assert.Throws<IOException>(() => journal.RecordMarked(firstOwner, attempts[0].Id));
            Assert.False(journal.Get(firstOwner)!.Marked);
            Assert.Throws<IOException>(() => journal.Begin(Guid.NewGuid(), new HashSet<Guid>()));
            Assert.Equal(64L * 1024 * 1024, Directory.GetFiles(_recoveryDirectory, "*.json").Sum(path => new FileInfo(path).Length));
        }
        else
        {
            Assert.True(journal.RecordMarked(firstOwner, attempts[0].Id).Marked);
            // Reconstruct at the real disk boundary: only the committed shrink
            // may release capacity for another owner's new durable attempt.
            var restored = new PlaylistCreationJournal(_recoveryDirectory);
            Assert.NotNull(restored.Begin(Guid.NewGuid(), new HashSet<Guid>()));
        }
    }

    [Fact]
    public void MaximumPlaylistOwnerSnapshotFitsTheEnvelopeAndInvalidAttemptTransitionsCannotReplaceItsProvenance()
    {
        var journal = new PlaylistCreationJournal(_recoveryDirectory);
        var owner = Guid.NewGuid();
        var ids = Enumerable.Range(0, 4096).Select(_ => Guid.NewGuid()).ToHashSet();
        var attempt = journal.Begin(owner, ids);
        Assert.Equal(4096, attempt.BeforeIds.Length);
        Assert.InRange(new FileInfo(Assert.Single(Directory.GetFiles(_recoveryDirectory, "*.json"))).Length, 1, 256 * 1024);
        Assert.Throws<IOException>(() => journal.Begin(owner, new HashSet<Guid>()));
        Assert.Throws<IOException>(() => journal.RecordReturned(owner, attempt.Id, ids.First()));
        Assert.Throws<IOException>(() => journal.RecordMarked(owner, Guid.NewGuid()));
        Assert.False(journal.Complete(owner, Guid.NewGuid()));
        Assert.Equal(attempt, journal.Get(owner));
        Assert.Throws<IOException>(() => journal.Begin(Guid.NewGuid(), ids.Append(Guid.NewGuid()).ToHashSet()));
        Assert.True(journal.Complete(owner, attempt.Id));
        Assert.False(journal.Complete(owner, attempt.Id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("path-file")]
    [InlineData("wrong-filename")]
    [InlineData("missing-before")]
    [InlineData("duplicate-before")]
    [InlineData("marked-without-return")]
    [InlineData("oversize")]
    public void InvalidPlaylistCreationEvidenceCannotBeRestoredAsEmptyOrDeleted(string boundary)
    {
        Directory.CreateDirectory(_recoveryDirectory);
        if (boundary == "path-file")
        {
            var notDirectory = Path.Join(_recoveryDirectory, "not-a-directory");
            File.WriteAllText(notDirectory, "original evidence");
            Assert.Throws<IOException>(() => new PlaylistCreationJournal(notDirectory));
            Assert.True(File.Exists(notDirectory));
            return;
        }

        var journal = new PlaylistCreationJournal(_recoveryDirectory);
        var original = journal.Begin(Guid.NewGuid(), new HashSet<Guid>());
        var path = Assert.Single(Directory.GetFiles(_recoveryDirectory, "*.json"));
        var contents = boundary switch
        {
            "null" => "null",
            "malformed" => "{",
            "missing-before" => System.Text.Json.JsonSerializer.Serialize(new { original.Id, original.Owner }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options),
            "duplicate-before" => System.Text.Json.JsonSerializer.Serialize(original with { BeforeIds = System.Collections.Immutable.ImmutableArray.Create(original.Owner, original.Owner) }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options),
            "marked-without-return" => System.Text.Json.JsonSerializer.Serialize(original with { Marked = true }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options),
            "oversize" => new string(' ', 256 * 1024 + 1),
            _ => File.ReadAllText(path)
        };
        File.WriteAllText(path, contents);
        if (boundary == "wrong-filename")
        {
            var renamed = Path.Join(_recoveryDirectory, Guid.NewGuid().ToString("N") + ".json");
            File.Move(path, renamed);
            path = renamed;
        }

        Assert.Throws<IOException>(() => new PlaylistCreationJournal(_recoveryDirectory));
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(_recoveryDirectory, "*.json"));
    }

    [Fact]
    public void InaccessiblePlaylistRecoveryDoesNotBecomeAnEmptyJournalAndItsOriginalAttemptStillRestores()
    {
        var original = new PlaylistCreationJournal(_recoveryDirectory);
        var attempt = original.Begin(Guid.NewGuid(), new HashSet<Guid>());
        Assert.Throws<IOException>(() => new PlaylistCreationJournal(_recoveryDirectory, enumerate: _ => throw new UnauthorizedAccessException("journal unavailable")));
        var restored = new PlaylistCreationJournal(_recoveryDirectory);
        Assert.Equal(attempt.Id, Assert.Single(restored.Pending()).Id);
        Assert.Single(Directory.GetFiles(_recoveryDirectory, "*.json"));
    }

    [Fact]
    public async Task APlaylistAttemptCannotBeCompletedOrRewrittenWhileItsDurableMarkIsStillBeingWritten()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var stall = false;
        var journal = new PlaylistCreationJournal(_recoveryDirectory, (path, contents) =>
        {
            if (stall)
            {
                started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }

            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        });
        var attempt = journal.Begin(Guid.NewGuid(), new HashSet<Guid>());
        journal.RecordReturned(attempt.Owner, attempt.Id, Guid.NewGuid());
        stall = true;
        var marking = Task.Run(() => journal.RecordMarked(attempt.Owner, attempt.Id), TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Throws<IOException>(() => journal.Complete(attempt.Owner, attempt.Id));
            Assert.Throws<IOException>(() => journal.RecordMarked(attempt.Owner, attempt.Id));
            Assert.False(journal.Get(attempt.Owner)!.Marked);
        }
        finally
        {
            release.Set();
            await marking.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.True(journal.Get(attempt.Owner)!.Marked);
        Assert.True(journal.Complete(attempt.Owner, attempt.Id));
        Assert.Empty(journal.Pending());
    }

    [Fact]
    public void HostOwnerPredicateHydratesOnlyTheOwnersPlaylistsIncludingHiddenOnesAndReleasesTheDatabaseContext()
    {
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var hidden = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner, OpenAccess = false, Path = "/hidden/playlists/item" };
        var visible = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner };
        static string Data(Guid user) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"OwnerUserId\":\"{user:N}\"}}");
        var playlistType = typeof(MediaBrowser.Controller.Playlists.Playlist).FullName!;
        var data = new[]
        {
            new BaseItemEntity { Id = hidden.Id, Type = playlistType, Data = Data(owner) },
            new BaseItemEntity { Id = visible.Id, Type = playlistType, Data = Data(owner) },
            new BaseItemEntity { Id = Guid.NewGuid(), Type = typeof(Movie).FullName!, OwnerId = owner, Data = Data(otherOwner) }
        }.Concat(Enumerable.Range(0, 5000).Select(_ => new BaseItemEntity { Id = Guid.NewGuid(), Type = playlistType, Data = Data(otherOwner) })).AsQueryable();
        var set = new Mock<DbSet<BaseItemEntity>>();
        set.As<IQueryable<BaseItemEntity>>().Setup(query => query.Provider).Returns(data.Provider);
        set.As<IQueryable<BaseItemEntity>>().Setup(query => query.Expression).Returns(data.Expression);
        set.As<IQueryable<BaseItemEntity>>().Setup(query => query.ElementType).Returns(data.ElementType);
        set.As<IQueryable<BaseItemEntity>>().Setup(query => query.GetEnumerator()).Returns(() => data.GetEnumerator());
        var context = new Mock<JellyfinDbContext>(new DbContextOptionsBuilder<JellyfinDbContext>().Options, Mock.Of<ILogger<JellyfinDbContext>>(), Mock.Of<IJellyfinDatabaseProvider>(), Mock.Of<IEntityFrameworkCoreLockingBehavior>());
        context.Setup(database => database.Set<BaseItemEntity>()).Returns(set.Object);
        var disposed = false;
        context.Setup(database => database.Dispose()).Callback(() => disposed = true);
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(database => database.CreateDbContext()).Returns(context.Object);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            Assert.True(disposed);
            Assert.Null(query.User);
            Assert.False(query.GroupByPresentationUniqueKey);
            Assert.Equal(new[] { hidden.Id, visible.Id }.Order(), query.ItemIds.Order());
            return [hidden, visible];
        });
        var inventory = new HostedPlaylistInventory(_libraryManagerMock.Object, factory.Object);
        Assert.Equal(new[] { hidden.Id, visible.Id }.Order(), inventory.LoadOwned(owner).Select(item => item.Id).Order());
        context.Verify(database => database.Dispose(), Times.Once);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public void EmptyHostOwnerInventoryCannotTurnIntoAnUnscopedPlaylistRead()
    {
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        var inventory = new HostedPlaylistInventory(library.Object, _ => []);
        Assert.Empty(inventory.LoadOwned(Guid.NewGuid()));
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public void HostOwnerInventoryRejectsAnIncompleteHydratedSnapshot()
    {
        var inventory = new HostedPlaylistInventory(_libraryManagerMock.Object, _ => [Guid.NewGuid()]);
        Assert.Throws<IOException>(() => inventory.LoadOwned(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostOwnerInventoryCannotPublishRowsWithUnresolvedTypeOrOwnership(bool playlist)
    {
        BaseItem item = playlist ? new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = Guid.Empty } : new Movie { Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([item]);
        var inventory = new HostedPlaylistInventory(_libraryManagerMock.Object, _ => [item.Id]);
        Assert.Throws<IOException>(() => inventory.LoadOwned(Guid.NewGuid()));
    }

    [Fact]
    public void HostOwnerPredicateBoundsIdsBeforeHydratingAnyPlaylistRows()
    {
        var owner = Guid.NewGuid();
        var data = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"OwnerUserId\":\"{owner:N}\"}}");
        var rows = Enumerable.Range(0, 4097).Select(_ => new BaseItemEntity { Id = Guid.NewGuid(), Type = typeof(MediaBrowser.Controller.Playlists.Playlist).FullName!, Data = data }).AsQueryable();
        Assert.Throws<IOException>(() => HostedPlaylistInventory.CaptureOwnedIds(rows, owner));
        Assert.Throws<ArgumentException>(() => HostedPlaylistInventory.CaptureOwnedIds(rows, Guid.Empty));
    }

    [Fact]
    public async Task OwnerLifecycleLeasesBoundAndDrainCancelledWaitersWithoutBlockingAnUnrelatedOwner()
    {
        var operations = new PlaylistOwnerOperations();
        var owner = Guid.NewGuid();
        using var current = await operations.EnterAsync(owner, TestContext.Current.CancellationToken);
        Assert.Null(operations.TryEnter(owner));
        using var cancelled = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 63).Select(_ => operations.EnterAsync(owner, cancelled.Token)).ToArray();
        Assert.All(waiting, task => Assert.False(task.IsCompleted));
        await Assert.ThrowsAsync<IOException>(() => operations.EnterAsync(owner, TestContext.Current.CancellationToken));
        Assert.Null(operations.TryEnter(owner));
        using var other = await operations.EnterAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        foreach (var task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        current.Dispose();
        using var resumed = await operations.EnterAsync(owner, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        resumed.Dispose(); other.Dispose();
        var registry = (System.Collections.IDictionary)typeof(PlaylistOwnerOperations).GetField("_entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(operations)!;
        Assert.Empty(registry);
    }

    [Fact]
    public async Task OwnerLifecycleRegistryRefusesA4097thOwnerAndReleasesItsCapacityAfterAllLeasesDrain()
    {
        var operations = new PlaylistOwnerOperations();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var _ in Enumerable.Range(0, 4096))
            {
                leases.Add(await operations.EnterAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
            }

            await Assert.ThrowsAsync<IOException>(() => operations.EnterAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
            Assert.Null(operations.TryEnter(Guid.NewGuid()));
            Assert.Null(operations.TryEnter(Guid.NewGuid()));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }

        using var resumed = await operations.EnterAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFullOwnerOperationQueueMakesPublicSyncPreserveTheIncumbentWithoutCallingTheHost()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        var service = CreateSut();
        var operations = OwnerOperations(service);
        using var current = await operations.EnterAsync(owner.Id, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 63).Select(_ => operations.EnterAsync(owner.Id, cancellation.Token)).ToArray();
        try
        {
            var result = await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken);
            Assert.Equal(1, result.PlaylistsFailed);
            Assert.Equal(incumbent.Id, Assert.Single(items.Values).Id);
            _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
            _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        }
        finally
        {
            await cancellation.CancelAsync();
            foreach (var task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    [Fact]
    public async Task DisabledOwnerMaintenanceCannotOvertakeAnActiveLeaseOrLoseItsRestoredUnmarkedReceipt()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        owner.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username), ProviderIds = new() { [RecommendationPlaylistService.ManagedOwnerProviderKey] = owner.Id.ToString("N") } };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent, [partial.Id] = partial };
        ArrangeOwnedInventory(owner, items);
        var journal = new PlaylistCreationJournal(_recoveryDirectory);
        var attempt = journal.Begin(owner.Id, new HashSet<Guid> { incumbent.Id });
        journal.RecordReturned(owner.Id, attempt.Id, partial.Id);
        partial.ProviderIds[RecommendationPlaylistService.CreationAttemptProviderKey] = attempt.Id.ToString("N");
        var service = CreateSut();
        using var current = await OwnerOperations(service).EnterAsync(owner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await service.UpdatePlaylistsForAllUsersAsync([], TestContext.Current.CancellationToken)).OldPlaylistsRemoved);
        Assert.Equal(2, items.Count);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        current.Dispose();
        await service.UpdatePlaylistsForAllUsersAsync([], TestContext.Current.CancellationToken);
        Assert.Empty(items);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Exactly(2));
    }

    private static PlaylistOwnerOperations OwnerOperations(RecommendationPlaylistService service)
        => (PlaylistOwnerOperations)typeof(RecommendationPlaylistService).GetField("_ownerOperations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;

    [Fact]
    public async Task ASecondRecoveryOrSyncCannotOvertakeAPausedPreservationAndMarkANewManagedReplacementAsPreserved()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "unknown partial" };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        var failing = true;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            if (failing) { items[partial.Id] = partial; return Task.FromException<PlaylistCreationResult>(new IOException("registered without returned ID")); }
            items[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut();
        var result = CreateResult(owner.Id, owner.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsFailed);
        failing = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preservationWrites = 0;
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(async (BaseItem item, BaseItem _, ItemUpdateType _, CancellationToken token) =>
        {
            if (item.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey) && Interlocked.Increment(ref preservationWrites) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var first = service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken);
        Task<bool>? second = null;
        Task<PlaylistSyncResult>? sync = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            second = service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken);
            sync = service.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            Assert.False(sync.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (sync is not null) await sync.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, (await sync).PlaylistsCreated);
        Assert.True(partial.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        Assert.False(replacement.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        Assert.Equal(owner.Id.ToString("N"), replacement.ProviderIds[RecommendationPlaylistService.ManagedOwnerProviderKey]);
    }

    [Fact]
    public async Task FailedDurableCreationAdmissionNeverCallsTheHostOrRemovesTheIncumbent()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        var service = CreateSut(write: (_, _) => throw new IOException("journal unavailable"));
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Single(items);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task CreationAdmissionReservesTheReturnedIdentityAndDoesNotCallTheHostWhenOnlyTheBeginStateWouldFit()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var inventory = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, inventory);
        var prototype = new PlaylistCreationJournal.Attempt(Guid.NewGuid(), owner.Id, System.Collections.Immutable.ImmutableArray.Create(incumbent.Id));
        var begin = System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(prototype, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options));
        var complete = System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(prototype with { ReturnedId = Guid.NewGuid() }, Jellyfin.Plugin.JellyfinHelper.Services.JsonDefaults.Options));
        Assert.True(complete > begin);
        var service = CreateSut(journalBudget: begin);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
        Assert.Equal(incumbent.Id, Assert.Single(inventory.Keys));
        var journal = new PlaylistCreationJournal(Path.Join(_recoveryDirectory, "bounded"), byteBudget: complete);
        var attempt = journal.Begin(owner.Id, new HashSet<Guid> { incumbent.Id });
        var returned = journal.RecordReturned(owner.Id, attempt.Id, Guid.NewGuid());
        var marked = journal.RecordMarked(owner.Id, attempt.Id);
        Assert.Equal(attempt.ReservedBytes, returned.ReservedBytes);
        Assert.Equal(attempt.ReservedBytes, marked.ReservedBytes);
        Assert.Throws<IOException>(() => journal.Begin(Guid.NewGuid(), new HashSet<Guid>()));
        Assert.True(journal.Complete(owner.Id, attempt.Id));
        Assert.NotNull(journal.Begin(Guid.NewGuid(), new HashSet<Guid>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task APostJournalSeriesRecheckKeepsTheRankedOriginAndEitherAdvancesTheEpisodeOrCommitsConfirmedEmptyCleanup(int playedAfterAdmission)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, playlists);
        var series = new Series { Id = Guid.NewGuid(), Path = "/media/series" };
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1, Path = "/media/series/1.mkv" };
        var next = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 2, Path = "/media/series/2.mkv" };
        foreach (var item in new BaseItem[] { series, first, next }) _libraryManagerMock.Setup(library => library.GetItemById(item.Id)).Returns(item);
        _libraryManagerMock.Setup(library => library.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([series.Id, first.Id, next.Id]);
        _libraryManagerMock.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Episode) ? [first, next] : playlists.Values.Cast<BaseItem>().ToArray());
        var played = 0;
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = "playback", Played = item.Id == first.Id ? played >= 1 : item.Id == next.Id && played >= 2 });
        Guid[]? linked = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            linked = request.ItemIdList.ToArray();
            playlists[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut(data.Object, write: (path, contents) =>
        {
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            played = playedAfterAdmission;
        });
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, UserName = owner.Username, Recommendations = [new() { ItemId = series.Id, ItemType = "Series" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.PlaylistsFailed);
        Assert.Equal(playedAfterAdmission == 2 ? 0 : 1, result.PlaylistsCreated);
        if (playedAfterAdmission == 2)
        {
            Assert.Null(linked);
            Assert.Empty(playlists);
            Assert.Equal(1, result.OldPlaylistsRemoved);
        }
        else
        {
            Assert.Equal(playedAfterAdmission == 1 ? next.Id : first.Id, Assert.Single(linked!));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReplacementCommitCountsOnlyPersistedEntriesAndAnEmptyHostResultKeepsTheIncumbent(int actuallyAdded)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, playlists, hostPopulatesEntries: false);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            replacement.LinkedChildren = request.ItemIdList.Take(actuallyAdded).Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            playlists[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 2)], TestContext.Current.CancellationToken);
        Assert.Equal(actuallyAdded, result.TotalItemsAdded);
        Assert.Equal(actuallyAdded == 0 ? 0 : 1, result.PlaylistsCreated);
        Assert.Equal(actuallyAdded == 0 ? 1 : 0, result.PlaylistsFailed);
        Assert.Equal(actuallyAdded == 0 ? incumbent.Id : replacement.Id, Assert.Single(playlists.Keys));
        if (actuallyAdded == 0) _libraryManagerMock.Verify(manager => manager.UpdateItemAsync(replacement, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreservationRestoresTheCachedMarkerAndCannotReleaseTheDurableCreationGate(bool cancelled)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name, ProviderIds = new() { [RecommendationPlaylistService.PreservedPlaylistProviderKey] = "prior-nonmatching-marker" } };
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, playlists);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            playlists[manual.Id] = manual;
            return Task.FromException<PlaylistCreationResult>(new IOException("registered before ID return"));
        });
        var fail = true;
        Exception preservationFailure = cancelled ? new OperationCanceledException("cancelled preservation") : new IOException("preservation failed");
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(manual, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(() => fail ? Task.FromException(preservationFailure) : Task.CompletedTask);
        var service = CreateSut();
        var result = CreateResult(owner.Id, owner.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsFailed);
        if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        else Assert.False(await service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.Equal("prior-nonmatching-marker", manual.ProviderIds[RecommendationPlaylistService.PreservedPlaylistProviderKey]);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        fail = false;
        Assert.True(await service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        _playlistManagerMock.Verify(manager => manager.SavePlaylistFile(manual), Times.Never);
    }

    [Fact]
    public async Task AManualPlaylistReusingTheReturnedIdAndOwnerAfterRestartCannotBeRolledBack()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent, [manual.Id] = manual };
        ArrangeOwnedInventory(owner, playlists);
        var journal = new PlaylistCreationJournal(_recoveryDirectory);
        var attempt = journal.Begin(owner.Id, new HashSet<Guid> { incumbent.Id });
        journal.RecordReturned(owner.Id, attempt.Id, manual.Id);
        var restarted = CreateSut();
        Assert.Equal(0, await restarted.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, playlists.Count);
        Assert.True(await restarted.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await restarted.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(manual.Id, Assert.Single(playlists.Keys));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(manual, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task APostReturnLookupCannotAdoptAManualReplacementThatReusedTheJustRegisteredId()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var original = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = original.Id, OwnerUserId = owner.Id, Name = incumbent.Name };
        var inventory = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, inventory, hostPopulatesEntries: false);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            original.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            inventory[original.Id] = original;
            SignalHostedRegistration(original, request);
            inventory[manual.Id] = manual;
            return Task.FromResult(new PlaylistCreationResult(original.Id.ToString()));
        });
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.PlaylistsFailed);
        Assert.Equal(0, result.PlaylistsCreated);
        Assert.Equal(2, inventory.Count);
        Assert.Empty(manual.ProviderIds);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(manual, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.UpdateItemAsync(manual, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AHostWithoutRegistrationEvidenceCannotTurnAReturnedIdIntoRollbackAuthority()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var unproven = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var inventory = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, inventory, hostPopulatesEntries: false);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            inventory[unproven.Id] = unproven;
            return Task.FromResult(new PlaylistCreationResult(unproven.Id.ToString()));
        });
        var result = await CreateSut(emulateRegistration: false).UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.PlaylistsFailed);
        Assert.Equal(0, result.PlaylistsCreated);
        Assert.Equal(2, inventory.Count);
        Assert.Empty(unproven.ProviderIds);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LibraryAccessWithdrawnDuringDurableAdmissionCannotReachHostCreationOrDeleteTheIncumbent(bool withdraw, bool disableOwner)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, playlists);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/current.mkv" };
        _libraryManagerMock.Setup(library => library.GetItemById(movie.Id)).Returns(movie);
        var withdrawn = false;
        _libraryManagerMock.Setup(library => library.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns(() => withdrawn ? [] : [movie.Id]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, movie)).Returns(new UserItemData { Key = "unseen", Played = false });
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            playlists[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut(data.Object, write: (path, contents) =>
        {
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
            withdrawn = withdraw;
            owner.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, disableOwner);
        });
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, UserName = owner.Username, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        var blocked = withdraw || disableOwner;
        Assert.Equal(blocked ? 0 : 1, result.PlaylistsCreated);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), blocked ? Times.Never() : Times.Once());
        Assert.Equal(disableOwner ? 1 : 0, result.PlaylistsFailed);
        if (disableOwner)
        {
            Assert.Contains(incumbent.Id, playlists.Keys);
            _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        }
        else if (withdraw)
        {
            Assert.DoesNotContain(incumbent.Id, playlists.Keys);
            _libraryManagerMock.Verify(manager => manager.DeleteItem(incumbent, It.IsAny<DeleteOptions>()), Times.Once);
        }
    }

    [Fact]
    public async Task AnUnavailableConfiguredOwnerInventoryCannotFallBackToAnUnscopedReadOrStartCreation()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([owner]);
        _userManagerMock.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        var service = CreateSut(libraryServices: new PlaylistLibraryServices(null, null) { OwnedPlaylists = _ => null! });
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => !query.GroupByPresentationUniqueKey)), Times.Never);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once); // Separate once-per-batch inactive-owner sweep, not an owner-inventory fallback.
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    public async Task ARegisteredIdLessFailureAndSameNamedManualPlaylistRemainProtectedAfterServiceReconstruction()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "unknown partial" };
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            items[partial.Id] = partial;
            return Task.FromException<PlaylistCreationResult>(new IOException("registered before ID return"));
        });
        var original = CreateSut();
        var result = CreateResult(owner.Id, owner.Username, 1);
        Assert.Equal(1, (await original.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsFailed);
        items[manual.Id] = manual;
        var restarted = CreateSut();
        Assert.Equal(1, (await restarted.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        Assert.True(await restarted.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.Equal(manual.Id.ToString("N"), manual.ProviderIds[RecommendationPlaylistService.PreservedPlaylistProviderKey]);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            items[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        Assert.Equal(1, (await restarted.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken)).PlaylistsCreated);
        Assert.Contains(manual.Id, items.Keys);
        Assert.False(replacement.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
    }

    [Fact]
    public async Task APreRegistrationFailureCanReconcileAfterRestartAndContinueExplicitFullCleanup()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ThrowsAsync(new IOException("nothing registered"));
        Assert.Equal(1, (await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, await CreateSut().RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(items);
        Assert.Empty(Directory.GetFiles(_recoveryDirectory, "*.json"));
    }

    [Fact]
    public async Task AFailedJournalCompletionPreservesTheIncumbentAndRestoresTheMarkedReplacementWithoutPreservingIt()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            items[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut(delete: _ => throw new IOException("completion unavailable"));
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(2, items.Count);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        var restarted = CreateSut();
        Assert.True(await restarted.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.False(replacement.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        Assert.Empty(Directory.GetFiles(_recoveryDirectory, "*.json"));
    }

    [Fact]
    public async Task FailedDurableMarkRestoresOnlyTheKnownUnmarkedReplacementAndCannotResolveItAsAnAmbiguousManualPlaylist()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var incumbent = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = incumbent.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [incumbent.Id] = incumbent };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            items[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var writes = 0;
        var original = CreateSut(write: (path, contents) =>
        {
            if (++writes == 3) throw new IOException("durable mark unavailable");
            Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents);
        });
        Assert.Equal(1, (await original.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(2, items.Count);
        var restored = CreateSut();
        Assert.False(await restored.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.False(replacement.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        Assert.Equal(2, await restored.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(items);
        Assert.Empty(Directory.GetFiles(_recoveryDirectory, "*.json"));
    }

    private sealed class LegacyPlaylistProvider : IRecommendationPlaylistService
    {
        public Task<PlaylistSyncResult> UpdatePlaylistsForAllUsersAsync(IReadOnlyList<RecommendationResult> results, CancellationToken cancellationToken = default) => Task.FromResult(new PlaylistSyncResult());

        public Task<int> RemoveAllRecommendationPlaylistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    public RecommendationPlaylistServiceTests()
    {
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _playlistManagerMock.Setup(manager => manager.GetPlaylistsFolder(It.IsAny<Guid>())).Returns(new Folder { Id = Guid.NewGuid(), Path = "/data/playlists" });
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) =>
        {
            var creation = _playlistManagerMock.Invocations.LastOrDefault(invocation => invocation.Method.Name == nameof(IPlaylistManager.CreatePlaylist)
                && invocation.ReturnValue is Task<PlaylistCreationResult> task && task.IsCompletedSuccessfully && Guid.TryParse(task.Result.Id, out var returned) && returned == id);
            if (creation?.Arguments[0] is not PlaylistCreationRequest request) return null;
            if (!_createdPlaylists.TryGetValue(id, out var playlist))
            {
                playlist = new MediaBrowser.Controller.Playlists.Playlist { Id = id, OwnerUserId = request.UserId, Name = request.Name, Path = "/data/playlists/created" };
                _createdPlaylists[id] = playlist;
            }

            PopulateHostedEntries(playlist);
            return playlist;
        });
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AHostFailureAfterRegistrationBlocksFurtherCreatesWithoutGuessingByPlaylistName()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "not name-based evidence" };
        var next = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
        var store = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(user.Id)).Returns(() => store.Values.ToArray());
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => store.GetValueOrDefault(id));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => store.Values.Cast<BaseItem>().ToArray());
        var fail = true;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            if (fail) { store[partial.Id] = partial; return Task.FromException<PlaylistCreationResult>(new IOException("host entry save failed after registration")); }
            next.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            store[next.Id] = next;
            return Task.FromResult(new PlaylistCreationResult(next.Id.ToString()));
        });
        _libraryManagerMock.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((item, _) => store.Remove(item.Id));
        var service = CreateSut();
        var input = CreateResult(user.Id, user.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        Assert.Equal(2, store.Count);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        store.Remove(partial.Id);
        fail = false;
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsCreated);
        Assert.Equal(next.Id, Assert.Single(store.Values).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministratorCanResolveAnIdLessFailureWithoutRemovingANewManualPlaylist(bool reservedName)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = reservedName ? old.Name : "manual personal playlist" };
        if (reservedName) manual.Path = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".m3u");
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
        var store = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(user.Id)).Returns(() => store.Values.ToArray());
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => store.GetValueOrDefault(id));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => store.Values.Cast<BaseItem>().ToArray());
        _libraryManagerMock.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((item, _) => store.Remove(item.Id));
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((item, _, _, _) =>
        {
            if (item.Id == manual.Id) store[manual.Id] = new MediaBrowser.Controller.Playlists.Playlist
            {
                Id = manual.Id, OwnerUserId = user.Id, Name = manual.Name, Path = manual.Path,
                ProviderIds = new Dictionary<string, string>(manual.ProviderIds)
            };
        }).Returns(Task.CompletedTask);
        var fail = true;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            if (fail) return Task.FromException<PlaylistCreationResult>(new IOException("host failed before registration"));
            replacement.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            store[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut();
        var input = CreateResult(user.Id, user.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        store[manual.Id] = manual;
        fail = false;
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.True(await service.ResolveDeferredCreationAsync(user.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsCreated);
        Assert.Equal(2, store.Count);
        Assert.Contains(manual.Id, store.Keys);
        Assert.Contains(replacement.Id, store.Keys);
        Assert.Equal(manual.Id.ToString("N"), manual.ProviderIds[RecommendationPlaylistService.PreservedPlaylistProviderKey]);
        _libraryManagerMock.Verify(manager => manager.UpdateItemAsync(manual, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken), Times.Once);
        _playlistManagerMock.Verify(manager => manager.SavePlaylistFile(manual), Times.Never());
        replacement.ProviderIds[RecommendationPlaylistService.PreservedPlaylistProviderKey] = manual.Id.ToString("N");
        // Reconstruct the registered lifecycle to prove the no-delete decision is
        // carried by durable playlist identity, not an in-process exception list.
        var restarted = CreateSut();
        await restarted.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(manual.Id, Assert.Single(store.Values).Id);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(manual, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("owner")]
    [InlineData("folder")]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("cancel")]
    public async Task AdministratorResolutionCannotReleaseAmbiguityBeforeDurableIdentityPreservation(string failure)
    {
        var owner = Guid.NewGuid();
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner };
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(owner)).Returns([manual]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([manual]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(manual.Id)).Returns(manual);
        if (failure == "missing") _libraryManagerMock.Setup(manager => manager.GetItemById(manual.Id)).Returns((BaseItem?)null);
        if (failure == "owner") _libraryManagerMock.Setup(manager => manager.GetItemById(manual.Id)).Returns(new MediaBrowser.Controller.Playlists.Playlist { Id = manual.Id, OwnerUserId = Guid.NewGuid() });
        if (failure == "folder") _playlistManagerMock.Setup(manager => manager.GetPlaylistsFolder(owner)).Returns(() => null!);
        if (failure == "read") _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new IOException("ownership unavailable"));
        if (failure is "write" or "cancel") _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure == "write" ? new IOException("metadata unavailable") : new OperationCanceledException());
        var service = CreateSut();
        var attempts = (System.Collections.Concurrent.ConcurrentDictionary<Guid, HashSet<Guid>>)typeof(RecommendationPlaylistService).GetField("_uncertainCreations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        attempts[owner] = [];
        if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => service.ResolveDeferredCreationAsync(owner, TestContext.Current.CancellationToken));
        else Assert.False(await service.ResolveDeferredCreationAsync(owner, TestContext.Current.CancellationToken));
        Assert.Contains(owner, attempts.Keys);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationResolutionPreservesOwnerOnlyRollbackReceiptsWithoutTreatingThemAsAttemptProof(bool sameOwner)
    {
        var owner = Guid.NewGuid();
        var playlist = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner };
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(owner)).Returns([playlist]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([playlist]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(playlist.Id)).Returns(playlist);
        var service = CreateSut();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var attempts = (System.Collections.Concurrent.ConcurrentDictionary<Guid, HashSet<Guid>>)typeof(RecommendationPlaylistService).GetField("_uncertainCreations", flags)!.GetValue(service)!;
        var rollbacks = (System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>)typeof(RecommendationPlaylistService).GetField("_unmarkedReplacements", flags)!.GetValue(service)!;
        attempts[owner] = [];
        var receiptOwner = sameOwner ? owner : Guid.NewGuid();
        rollbacks[playlist.Id] = receiptOwner;
        Assert.True(await service.ResolveDeferredCreationAsync(owner, TestContext.Current.CancellationToken));
        Assert.Equal(receiptOwner, rollbacks[playlist.Id]);
        Assert.True(playlist.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        _libraryManagerMock.Verify(manager => manager.UpdateItemAsync(playlist, It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, TestContext.Current.CancellationToken), Times.Once());
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    private void ArrangeOwnedInventory(Jellyfin.Database.Implementations.Entities.User owner, Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> items, bool hostPopulatesEntries = true)
    {
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([owner]);
        _userManagerMock.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => items.Values.Cast<BaseItem>().ToArray());
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) =>
        {
            var item = items.GetValueOrDefault(id);
            if (item is not null && hostPopulatesEntries) PopulateHostedEntries(item);
            return item;
        });
        _libraryManagerMock.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((item, _) => items.Remove(item.Id));
    }

    private void PopulateHostedEntries(MediaBrowser.Controller.Playlists.Playlist playlist)
    {
        var creation = _playlistManagerMock.Invocations.LastOrDefault(invocation => invocation.Method.Name == nameof(IPlaylistManager.CreatePlaylist)
            && invocation.ReturnValue is Task<PlaylistCreationResult> task && task.IsCompletedSuccessfully && Guid.TryParse(task.Result.Id, out var returned) && returned == playlist.Id);
        if (creation?.Arguments[0] is PlaylistCreationRequest request && _populatedPlaylists.Add(playlist.Id) && playlist.LinkedChildren.Length == 0)
        {
            // Default fixtures model Jellyfin's successful AddToPlaylistInternal.
            // Omission/partial-add tests supply their actual persisted links instead.
            playlist.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
        }
    }

    private void SignalHostedRegistration(MediaBrowser.Controller.Playlists.Playlist playlist, PlaylistCreationRequest request)
    {
        playlist.Name ??= request.Name;
        _registrationSignals[playlist.Id] = Interlocked.Increment(ref _registrationSequence);
        _libraryManagerMock.Raise(library => library.ItemAdded += null, _libraryManagerMock.Object, new ItemChangeEventArgs { Item = playlist });
    }

    private IPlaylistManager RegistrationAwarePlaylistManager()
    {
        var manager = new Mock<IPlaylistManager>();
        manager.Setup(value => value.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(async (PlaylistCreationRequest request) =>
        {
            var before = Volatile.Read(ref _registrationSequence);
            var result = await _playlistManagerMock.Object.CreatePlaylist(request);
            if (Guid.TryParse(result.Id, out var id) && (!_registrationSignals.TryGetValue(id, out var signalled) || signalled <= before)
                && _libraryManagerMock.Object.GetItemById(id) is MediaBrowser.Controller.Playlists.Playlist playlist)
            {
                // The native host raises ItemAdded during registration, before its
                // CreatePlaylist task returns. Explicit race tests signal it themselves.
                SignalHostedRegistration(playlist, request);
            }

            return result;
        });
        manager.Setup(value => value.GetPlaylistsFolder(It.IsAny<Guid>())).Returns((Guid owner) => _playlistManagerMock.Object.GetPlaylistsFolder(owner));
        manager.Setup(value => value.GetPlaylistsFolder()).Returns(() => _playlistManagerMock.Object.GetPlaylistsFolder());
        manager.Setup(value => value.GetPlaylists(It.IsAny<Guid>())).Returns((Guid owner) => _playlistManagerMock.Object.GetPlaylists(owner));
        manager.Setup(value => value.SavePlaylistFile(It.IsAny<MediaBrowser.Controller.Playlists.Playlist>())).Callback<MediaBrowser.Controller.Playlists.Playlist>(playlist => _playlistManagerMock.Object.SavePlaylistFile(playlist));
        return manager.Object;
    }

    [Fact]
    public async Task AnEmptyBatchCannotDeleteAnIncumbentOrSameNamedAmbiguousRegisteredReplacement()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = old.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() => { items[partial.Id] = partial; return Task.FromException<PlaylistCreationResult>(new IOException("registered before entry save failed")); });
        var service = CreateSut();
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 0)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(0, await service.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, items.Count);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task FullCleanupReconcilesAnIdLessFailureWithNoNewOwnedItemInsteadOfKeepingTheIncumbentForever()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ThrowsAsync(new IOException("no item registered"));
        var service = CreateSut();
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, await service.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothMovieOriginsSurviveInitialPrerequisiteCollisionUntilFinalPlaybackDeduplication(bool prerequisiteCompletes)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([owner]);
        _userManagerMock.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737", ["TmdbCollection"] = "14890" } };
        var sequel = new Movie { Id = Guid.NewGuid(), Path = "/media/sequel.mkv", ProviderIds = new() { ["Tmdb"] = "8961", ["TmdbCollection"] = "14890" } };
        _libraryManagerMock.Setup(manager => manager.GetItemById(first.Id)).Returns(first);
        _libraryManagerMock.Setup(manager => manager.GetItemById(sequel.Id)).Returns(sequel);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, sequel.Id]);
        var completed = false;
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.User is null && query.IncludeItemTypes.Contains(BaseItemKind.Playlist)) completed = prerequisiteCompletes;
            return query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [first, sequel] : [];
        });
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = "sequence", Played = item.Id == first.Id && completed });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, Recommendations = [new() { ItemId = first.Id, ItemType = "Movie" }, new() { ItemId = sequel.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(prerequisiteCompletes ? sequel.Id : first.Id, Assert.Single(delivered!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRelistsPlaylistsCreatedDuringMetadataPersistenceAndBoundsContinuousChanges(bool continuous)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var first = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = old.Name };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        ArrangeOwnedInventory(owner, items);
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(owner.Id)).Throws(new InvalidOperationException("viewer inventory is not ownership evidence"));
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ThrowsAsync(new IOException("host failed before registration"));
        var service = CreateSut();
        await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken);
        items[first.Id] = first;
        var writes = 0;
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns((BaseItem _, BaseItem _, ItemUpdateType _, CancellationToken _) =>
        {
            if (++writes == 1 || continuous)
            {
                var late = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = old.Name };
                items[late.Id] = late;
            }

            return Task.CompletedTask;
        });
        Assert.Equal(!continuous, await service.ResolveDeferredCreationAsync(owner.Id, TestContext.Current.CancellationToken));
        Assert.InRange(writes, 2, 4);
        if (!continuous) Assert.All(items.Values.Where(item => item.Id != old.Id), item => Assert.Equal(item.Id.ToString("N"), item.ProviderIds[RecommendationPlaylistService.PreservedPlaylistProviderKey]));
        else Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 0)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task VisibilityChangesDoNotTurnAnOwnedIncumbentIntoAnUnknownCreation()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        ArrangeOwnedInventory(owner, items);
        var visible = false;
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(owner.Id)).Returns(() => visible ? [old] : []);
        var fail = true;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            if (fail) return Task.FromException<PlaylistCreationResult>(new IOException("no registration"));
            items[replacement.Id] = replacement;
            return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString()));
        });
        var service = CreateSut();
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsFailed);
        visible = true;
        fail = false;
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(owner.Id, owner.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsCreated);
        Assert.False(old.ProviderIds.ContainsKey(RecommendationPlaylistService.PreservedPlaylistProviderKey));
        Assert.Equal(replacement.Id, Assert.Single(items.Values).Id);
        _playlistManagerMock.Verify(manager => manager.GetPlaylists(owner.Id), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnratedMovieDeliveryHonorsTheOwnersExplicitJellyfinPolicy(bool blockUnrated)
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { MaxParentalRatingScore = 12 };
        if (blockUnrated) owner.Preferences.Add(new Jellyfin.Database.Implementations.Entities.Preference(Jellyfin.Database.Implementations.Enums.PreferenceKind.BlockUnratedItems, "Movie"));
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
        var items = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        ArrangeOwnedInventory(owner, items);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/unrated.mkv" };
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, movie)).Returns(new UserItemData { Key = "unrated", Played = false });
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() => { items[replacement.Id] = replacement; return Task.FromResult(new PlaylistCreationResult(replacement.Id.ToString())); });
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(blockUnrated ? 0 : 1, result.PlaylistsCreated);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), blockUnrated ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task PlaybackStartingDuringTheOwnershipSnapshotIsCaughtBeforeHostCreation()
    {
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r");
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/current.mkv" };
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = RecommendationPlaylistService.BuildPlaylistName(owner.Username) };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([owner]);
        _userManagerMock.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemById(old.Id)).Returns(old);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        var started = false;
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => { if (!query.GroupByPresentationUniqueKey && query.IncludeItemTypes.Contains(BaseItemKind.Playlist)) started = true; return [old]; });
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, movie)).Returns(() => new UserItemData { Key = "playback", Played = started });
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.True(started);
        Assert.Equal(0, result.PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalMovieResolutionReturnsTheOriginalSequelWhenItsPrerequisiteCompletes(bool completes)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737", ["TmdbCollection"] = "14890" } };
        var sequel = new Movie { Id = Guid.NewGuid(), Path = "/media/sequel.mkv", ProviderIds = new() { ["Tmdb"] = "8961", ["TmdbCollection"] = "14890" } };
        _libraryManagerMock.Setup(manager => manager.GetItemById(first.Id)).Returns(first);
        _libraryManagerMock.Setup(manager => manager.GetItemById(sequel.Id)).Returns(sequel);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, sequel.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [first, sequel] : []);
        var reads = 0;
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = "playback", Played = item.Id == first.Id && ++reads > 1 && completes });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = sequel.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(completes ? sequel.Id : first.Id, Assert.Single(delivered!));
        Assert.Equal(1, result.PlaylistsCreated);
        Assert.Equal(0, result.PlaylistsFailed);
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("completed")]
    [InlineData("engaged")]
    [InlineData("unknown")]
    [InlineData("conflicting")]
    [InlineData("unavailable")]
    [InlineData("unavailable-direct")]
    public async Task PlaylistOnlyBoxSetMembershipEnforcesOnlyAVerifiedSequenceAndDeliversTheFirstUnseenPrerequisite(string boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        var owner = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist>();
        ArrangeOwnedInventory(owner, playlists);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737" } };
        var sequel = new Movie { Id = Guid.NewGuid(), Path = "/media/sequel.mkv", ProviderIds = new() { ["Tmdb"] = "8961" } };
        if (boundary == "unavailable-direct")
        {
            first.ProviderIds["TmdbCollection"] = "14890";
            sequel.ProviderIds["TmdbCollection"] = "14890";
        }

        var collection = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = boundary == "unknown" ? "999999" : "14890" } };
        var conflicting = new BoxSet { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "999998" } };
        var collections = boundary == "conflicting" ? new[] { collection, conflicting } : [collection];
        var unavailable = boundary.StartsWith("unavailable", StringComparison.Ordinal);
        var linked = new Mock<MediaBrowser.Controller.Persistence.ILinkedChildrenService>();
        linked.Setup(service => service.GetLinkedChildrenIds(It.IsAny<Guid>(), (int)MediaBrowser.Controller.Entities.LinkedChildType.Manual)).Returns([first.Id, sequel.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(first.Id)).Returns(first);
        _libraryManagerMock.Setup(manager => manager.GetItemById(sequel.Id)).Returns(sequel);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, sequel.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => SequenceInventory(query, first, sequel, collections, playlists, unavailable));
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(owner, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData
        {
            Key = "sequence", Played = item.Id == first.Id && boundary == "completed", PlaybackPositionTicks = item.Id == first.Id && boundary == "engaged" ? 1 : 0
        });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            delivered = request.ItemIdList.ToArray();
            var created = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner.Id };
            created.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            playlists[created.Id] = created;
            return Task.FromResult(new PlaylistCreationResult(created.Id.ToString()));
        });
        linked.Setup(children => children.GetLinkedChildrenIds(It.Is<Guid>(id => playlists.ContainsKey(id)), (int)MediaBrowser.Controller.Entities.LinkedChildType.Manual))
            .Returns((Guid id, int _) => playlists[id].LinkedChildren.Select(child => child.ItemId.GetValueOrDefault()).ToArray());
        var service = CreateSut(data.Object, new PlaylistLibraryServices(null, null) { LinkedChildren = linked.Object });
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = owner.Id, UserName = owner.Username, Recommendations = [new() { ItemId = sequel.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(boundary == "unavailable-direct", first.ProviderIds.ContainsKey("TmdbCollection"));
        Assert.Equal(boundary == "unavailable-direct", sequel.ProviderIds.ContainsKey("TmdbCollection"));
        if (boundary == "engaged")
        {
            Assert.Null(delivered);
            Assert.Equal(0, result.PlaylistsCreated);
            return;
        }

        Assert.Equal(boundary is "unseen" or "unavailable-direct" ? first.Id : sequel.Id, Assert.Single(delivered!));
        Assert.Equal(1, result.PlaylistsCreated);
        Assert.Equal(0, result.PlaylistsFailed);
    }

    private static BaseItem[] SequenceInventory(InternalItemsQuery query, Movie first, Movie sequel, BoxSet[] collections, Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> playlists, bool unavailable)
    {
        if (query.IncludeItemTypes.Contains(BaseItemKind.Movie)) return [first, sequel];
        if (query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))
        {
            if (unavailable) throw new IOException("collection inventory unavailable");
            return collections;
        }

        return playlists.Values.Cast<BaseItem>().ToArray();
    }

    [Fact]
    public async Task FullRemovalCountsActualReceiptDeletionButNotAbsentOrDifferentlyOwnedIdentities()
    {
        var owner = Guid.NewGuid();
        var owned = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = owner };
        var foreign = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(owned.Id)).Returns(owned);
        _libraryManagerMock.Setup(manager => manager.GetItemById(foreign.Id)).Returns(foreign);
        var journal = new PlaylistCreationJournal(_recoveryDirectory);
        var attempt = journal.Begin(owner, new HashSet<Guid>());
        journal.RecordReturned(owner, attempt.Id, owned.Id);
        owned.ProviderIds[RecommendationPlaylistService.CreationAttemptProviderKey] = attempt.Id.ToString("N");
        var service = CreateSut();
        var receipts = (System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>)typeof(RecommendationPlaylistService).GetField("_unmarkedReplacements", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        receipts[owned.Id] = owner;
        receipts[foreign.Id] = owner;
        receipts[Guid.NewGuid()] = owner;
        Assert.Equal(1, await service.RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(owned, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(foreign, It.IsAny<DeleteOptions>()), Times.Never);
        Assert.Empty(receipts);
    }

    [Fact]
    public async Task AReturnedIncumbentIdentityCannotBeMarkedOrRolledBackAsANewReplacement()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(user.Id)).Returns([old]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(old.Id)).Returns(old);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([old]);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ReturnsAsync(new PlaylistCreationResult(old.Id.ToString()));
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.PlaylistsFailed);
        Assert.Equal(0, result.PlaylistsCreated);
        Assert.Empty(old.ProviderIds);
        _libraryManagerMock.Verify(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnavailableOrOversizedOwnershipSnapshotCannotStartAHostCreation(bool oversized)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([old]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.User == null && query.IncludeItemTypes.Contains(BaseItemKind.Playlist)))).Returns(oversized
            ? Enumerable.Range(0, 4096).Select(_ => new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id }).Prepend(old).Cast<BaseItem>().ToArray()
            : null!);
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken);
        Assert.Equal(1, result.PlaylistsFailed);
        Assert.Equal(0, result.PlaylistsCreated);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task UnavailableReconciliationAfterAnIdLessFailurePreservesBothIncumbentAndPartialCreation()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var partial = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "unknown owned creation" };
        var store = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist> { [old.Id] = old };
        var unavailable = false;
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(user.Id)).Returns(() => unavailable ? throw new IOException("ownership query unavailable") : store.Values.ToArray());
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => unavailable && query.User is null ? throw new IOException("ownership query unavailable") : store.Values.Cast<BaseItem>().ToArray());
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns(() =>
        {
            store[partial.Id] = partial;
            return Task.FromException<PlaylistCreationResult>(new IOException("host failed after registration"));
        });
        var service = CreateSut();
        var input = CreateResult(user.Id, user.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        unavailable = true;
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task AReplacementReceiptSurvivesAnUnavailablePhysicalLookupAndBlocksAnotherCreate()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var createdId = Guid.NewGuid();
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([old]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(createdId)).Throws(new IOException("physical playlist lookup unavailable"));
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(request => SignalHostedRegistration(new MediaBrowser.Controller.Playlists.Playlist { Id = createdId, OwnerUserId = user.Id, Name = request.Name }, request))
            .ReturnsAsync(new PlaylistCreationResult(createdId.ToString()));
        var service = CreateSut();
        var input = CreateResult(user.Id, user.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        var receipts = (System.Collections.Concurrent.ConcurrentDictionary<Guid, Guid>)typeof(RecommendationPlaylistService).GetField("_unmarkedReplacements", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
        var retained = Assert.Single(receipts);
        Assert.Equal(user.Id, retained.Value);
        Assert.Equal(createdId, retained.Key);
    }

    [Fact]
    public void OwnershipSnapshotsIgnoreForeignAndDuplicateRowsButRejectUnresolvedOwnedIds()
    {
        var user = Guid.NewGuid();
        var owned = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user };
        var foreign = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid() };
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([owned, foreign, owned]);
        var service = CreateSut();
        var ids = (HashSet<Guid>)typeof(RecommendationPlaylistService).GetMethod("GetOwnedPlaylistIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [user])!;
        Assert.Equal(owned.Id, Assert.Single(ids));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([owned, foreign, new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.Empty, OwnerUserId = user }]);
        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => typeof(RecommendationPlaylistService).GetMethod("GetOwnedPlaylistIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, [user]));
        Assert.IsType<IOException>(exception.InnerException);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task ADirectoryWhoseNameEndsInM3uKeepsItsDurablyMarkedReplacementWithoutAFileSave()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer.m3u", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var directory = Path.Join(Path.GetTempPath(), "playlist-" + Guid.NewGuid().ToString("N") + ".m3u");
        Directory.CreateDirectory(directory);
        try
        {
            var created = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Path = directory };
            Assert.False(created.IsFile);
            _libraryManagerMock.Setup(manager => manager.GetItemById(created.Id)).Returns(created);
            _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
            _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
                .Callback<PlaylistCreationRequest>(request => created.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray())
                .ReturnsAsync(new PlaylistCreationResult(created.Id.ToString()));
            _playlistManagerMock.Setup(manager => manager.SavePlaylistFile(created)).Throws(new IOException("a directory is not an M3U file"));
            var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken);
            Assert.Equal(1, result.PlaylistsCreated);
            Assert.Equal(0, result.PlaylistsFailed);
            _playlistManagerMock.Verify(manager => manager.SavePlaylistFile(created), Times.Never);
            _libraryManagerMock.Verify(manager => manager.DeleteItem(created, It.IsAny<DeleteOptions>()), Times.Never);
            Assert.Equal(user.Id.ToString("N"), created.ProviderIds[RecommendationPlaylistService.ManagedOwnerProviderKey]);
        }
        finally { Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalEpisodeRecheckKeepsItsSlotOrResolvesTheNextEligibleEpisodeFromTheSameSeries(bool started)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Path = "/media/show" };
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = "/media/show/1.mkv", ParentIndexNumber = 1, IndexNumber = 1 };
        var next = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = "/media/show/2.mkv", ParentIndexNumber = 1, IndexNumber = 2 };
        foreach (var item in new BaseItem[] { series, first, next }) _libraryManagerMock.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([series.Id, first.Id, next.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Episode) ? [first, next] : []);
        var reads = 0;
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = "playback", PlaybackPositionTicks = item.Id == first.Id && ++reads > 1 && started ? 1 : 0 });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = series.Id, ItemType = "Series" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(started ? next.Id : first.Id, Assert.Single(delivered!));
        Assert.Equal(1, result.PlaylistsCreated);
        Assert.Equal(0, result.PlaylistsFailed);
    }

    [Fact]
    public async Task ReenabledOwnerKeepsTheirRenamedManagedPlaylistDuringTheStaleDisabledSweep()
    {
        var enabled = new Jellyfin.Database.Implementations.Entities.User("enabled", "test", "test");
        var disabled = new Jellyfin.Database.Implementations.Entities.User("disabled", "test", "test");
        disabled.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([enabled, disabled]);
        _userManagerMock.Setup(manager => manager.GetUserById(enabled.Id)).Returns(enabled);
        _userManagerMock.Setup(manager => manager.GetUserById(disabled.Id)).Returns(disabled);
        var renamed = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(), OwnerUserId = disabled.Id, Name = "Earlier managed name",
            ProviderIds = new() { [RecommendationPlaylistService.ManagedOwnerProviderKey] = disabled.Id.ToString("N") }
        };
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.User == enabled) disabled.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, false);
            return query.User == enabled ? [] : [renamed];
        });
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = enabled.Id }], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.OldPlaylistsRemoved);
        Assert.Equal(0, result.PlaylistsFailed);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("parent")]
    [InlineData("file")]
    public async Task FailedReplacementMarkerPreservesTheIncumbentAndRollsBackOnlyTheNewOwnedPlaylist(string failure)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var created = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Path = failure == "file" ? "/data/playlists/new.m3u" : "/data/playlists/new" };
        _libraryManagerMock.Setup(manager => manager.GetItemById(created.Id)).Returns(created);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([old]);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ReturnsAsync(new PlaylistCreationResult(created.Id.ToString()));
        if (failure == "parent") _playlistManagerMock.Setup(manager => manager.GetPlaylistsFolder(user.Id)).Returns((Folder)null!);
        if (failure == "update") _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(created, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("marker update failed"));
        if (failure == "file") _playlistManagerMock.Setup(manager => manager.SavePlaylistFile(created)).Throws(new IOException("playlist file failed"));
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.PlaylistsCreated);
        Assert.Equal(1, result.PlaylistsFailed);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(created, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedEmptyFinalRecheckRemovesStalePlaylistButUnavailableRecheckPreservesIt(bool unavailable)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/movie.mkv" };
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var removed = false;
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => removed ? [] : [old]);
        _libraryManagerMock.Setup(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>())).Callback(() => removed = true);
        var reads = 0;
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, movie)).Returns(() =>
        {
            reads++;
            if (reads == 2 && unavailable) throw new IOException("final playback state unavailable");
            return new UserItemData { Key = "playback", PlaybackPositionTicks = reads == 1 ? 0 : 1 };
        });
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(!unavailable, removed);
        Assert.Equal(unavailable ? 1 : 0, result.PlaylistsFailed);
        Assert.Equal(unavailable ? 0 : 1, result.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    public async Task FailedMarkerAndRollbackKeepOneReceiptAndBlockAnotherReplacementUntilRecovery()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
        var retryReplacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
        var store = new Dictionary<Guid, BaseItem> { [old.Id] = old };
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => store.GetValueOrDefault(id));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => store.Values.ToArray());
        _playlistManagerMock.Setup(manager => manager.GetPlaylists(user.Id)).Returns(() => store.Values.OfType<MediaBrowser.Controller.Playlists.Playlist>().ToArray());
        var creates = 0;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            var item = ++creates == 1 ? replacement : retryReplacement;
            if (creates > 1) Assert.False(store.ContainsKey(replacement.Id));
            item.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            store[item.Id] = item;
            return Task.FromResult(new PlaylistCreationResult(item.Id.ToString()));
        });
        var fail = true;
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(replacement, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(() => fail ? Task.FromException(new IOException("marker unavailable")) : Task.CompletedTask);
        _libraryManagerMock.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((item, _) =>
        {
            if (item.Id == replacement.Id && fail) throw new IOException("rollback unavailable");
            store.Remove(item.Id);
        });
        var service = CreateSut();
        var input = CreateResult(user.Id, user.Username, 1);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsFailed);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Never);
        fail = false;
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken)).PlaylistsCreated);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Exactly(2));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Once);
        Assert.Equal(retryReplacement.Id, Assert.Single(store.Values).Id);
    }

    [Fact]
    public async Task CancellationDuringMarkingRollsBackOnlyTheReturnedReplacementAndPreservesTheIncumbent()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var old = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) };
        var replacement = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
        _libraryManagerMock.Setup(manager => manager.GetItemById(replacement.Id)).Returns(replacement);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([old]);
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(request => replacement.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray())
            .ReturnsAsync(new PlaylistCreationResult(replacement.Id.ToString()));
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(replacement, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateSut().UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(replacement, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(old, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task RealHostTagInitializationFiltersTheFirstEpisodeBeforeOrderingAndKeepsTheNextEligibleEpisode()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        user.Preferences.Add(new Jellyfin.Database.Implementations.Entities.Preference(Jellyfin.Database.Implementations.Enums.PreferenceKind.BlockedTags, "blocked"));
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Path = "/media/show", InheritedParentalRatingValue = 6 };
        var blocked = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = "/media/show/1.mkv", ParentIndexNumber = 1, IndexNumber = 1, Tags = ["blocked"], InheritedParentalRatingValue = 6 };
        var allowed = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Path = "/media/show/2.mkv", ParentIndexNumber = 1, IndexNumber = 2, InheritedParentalRatingValue = 6 };
        _libraryManagerMock.Setup(manager => manager.GetItemById(series.Id)).Returns(series);
        _libraryManagerMock.Setup(manager => manager.GetItemById(allowed.Id)).Returns(allowed);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([series.Id, allowed.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Episode)
            ? new[] { blocked, allowed }.Where(episode => !query.ExcludeInheritedTags.Intersect(episode.Tags).Any()).Cast<BaseItem>().ToArray()
            : []);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns(new UserItemData { Key = "unseen" });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = series.Id, ItemType = "Series" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(allowed.Id, Assert.Single(delivered!));
        Assert.Equal(1, result.PlaylistsCreated);
        Assert.Equal(0, result.PlaylistsFailed);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.Episode) && query.ExcludeInheritedTags.Contains("blocked"))), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASequelCannotKeepCompletionEvidenceFromALibraryExcludedBeforeFinalDelivery(bool excludePredecessor)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/excluded/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737", ["TmdbCollection"] = "14890" } };
        var sequel = new Movie { Id = Guid.NewGuid(), Path = "/media/sequel.mkv", ProviderIds = new() { ["Tmdb"] = "8961", ["TmdbCollection"] = "14890" } };
        var other = new Movie { Id = Guid.NewGuid(), Path = "/media/other.mkv" };
        var config = new PluginConfiguration();
        var configuration = new Mock<IPluginConfigurationService>();
        configuration.Setup(value => value.GetConfiguration()).Returns(config);
        _libraryManagerMock.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Allowed", Locations = ["/media"] },
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Excluded", Locations = ["/media/excluded"] }
        ]);
        foreach (var item in new[] { first, sequel, other }) _libraryManagerMock.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, sequel.Id, other.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [first, sequel, other] : []);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
        {
            if (item.Id == first.Id && excludePredecessor) config.ExcludedLibraries = "Excluded";
            return new UserItemData { Key = "played", Played = item.Id == first.Id };
        });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object, new PlaylistLibraryServices(configuration.Object, null));
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = sequel.Id, ItemType = "Movie" }, new() { ItemId = other.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(excludePredecessor ? new[] { other.Id } : [sequel.Id, other.Id], delivered);
        Assert.Equal(0, result.PlaylistsFailed);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(0, true)]
    [InlineData(double.NaN, true)]
    [InlineData(10, false)]
    public async Task AnExplicitOpinionArrivingAfterResolutionIsRecheckedWithoutInventingPlayback(double rating, bool eligible)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/movie.mkv" };
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var reads = 0;
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, movie)).Returns(() => new UserItemData
        {
            Key = "opinion-only", Rating = ++reads == 1 ? null : rating, Played = false, PlayCount = 0, PlaybackPositionTicks = 0
        });
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        var saved = new RecommendationResult { UserId = user.Id, Recommendations = [new RecommendedItem { ItemId = movie.Id, ItemType = "Movie" }] };
        var result = await service.UpdatePlaylistsForAllUsersAsync([saved], TestContext.Current.CancellationToken);
        Assert.Equal(eligible ? 1 : 0, result.PlaylistsCreated);
        Assert.Equal(0, result.PlaylistsFailed);
        Assert.Equal(eligible ? 3 : 2, reads); // Successful creation also rechecks after durable admission.
        Assert.Equal(movie.Id, Assert.Single(saved.Recommendations).ItemId);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), eligible ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task ALocallyRatedUnplayedPredecessorCannotBeDeliveredAsIfItWereUnseen()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737", ["TmdbCollection"] = "14890" } };
        var sequel = new Movie { Id = Guid.NewGuid(), Path = "/media/sequel.mkv", ProviderIds = new() { ["Tmdb"] = "8961", ["TmdbCollection"] = "14890" } };
        var other = new Movie { Id = Guid.NewGuid(), Path = "/media/other.mkv" };
        foreach (var movie in new[] { first, sequel, other }) _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, sequel.Id, other.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [first, sequel, other] : []);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData
        {
            Key = item.Id.ToString(), Rating = item.Id == first.Id ? 2 : null
        });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = sequel.Id, ItemType = "Movie" }, new() { ItemId = other.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(new[] { other.Id }, delivered);
    }

    [Fact]
    public async Task AParentalRestrictionChangedDuringResolutionIsAuthoritativeAtPlaylistCreation()
    {
        var userId = Guid.NewGuid();
        var initial = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = userId, MaxParentalRatingScore = 18 };
        var restricted = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = userId, MaxParentalRatingScore = 6 };
        var resolved = false;
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([initial]);
        _userManagerMock.Setup(manager => manager.GetUserById(userId)).Returns(() => resolved ? restricted : initial);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/restricted.mkv", InheritedParentalRatingValue = 18 };
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.User == restricted ? [] : [movie.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(() => { resolved = true; return movie; });
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), movie)).Returns(new UserItemData { Key = "unseen" });
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = userId, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), restricted), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDisabledOrDeletedDuringMediaResolutionReceivesNoNewPlaylist(bool deleted)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        var resolved = false;
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(() => resolved && deleted ? null : user);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/movie.mkv" };
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(() =>
        {
            resolved = true;
            if (!deleted) user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
            return movie;
        });
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, movie)).Returns(new UserItemData { Key = "unseen" });
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CachedSequelDeliveryResolvesAnAuthorizedUnseenPredecessorWithoutAnExtraSlot(bool firstCompleted, bool firstPartial)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var first = new Movie { Id = Guid.NewGuid(), Path = "/media/first.mkv", ProviderIds = new() { ["Tmdb"] = "9737", ["TmdbCollection"] = "14890" } };
        var second = new Movie { Id = Guid.NewGuid(), Path = "/media/second.mkv", ProviderIds = new() { ["Tmdb"] = "8961", ["TmdbCollection"] = "14890" } };
        var other = new Movie { Id = Guid.NewGuid(), Path = "/media/other.mkv" };
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([first.Id, second.Id, other.Id]);
        foreach (var movie in new[] { first, second, other }) _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) && query.User == user ? [first, second, other] : []);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData
        {
            Key = item.Id.ToString(), Played = item.Id == first.Id && firstCompleted, PlaybackPositionTicks = item.Id == first.Id && firstPartial ? 800 : 0
        });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        var input = new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = second.Id, ItemType = "Movie", TmdbId = 8961 }, new() { ItemId = other.Id, ItemType = "Movie" }] };
        await service.UpdatePlaylistsForAllUsersAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(firstPartial ? new[] { other.Id } : [firstCompleted ? second.Id : first.Id, other.Id], delivered);
        Assert.Equal(second.Id, input.Recommendations[0].ItemId);
        _libraryManagerMock.Verify(manager => manager.ConfigureUserAccess(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.Movie)), user), firstPartial ? Times.Once() : Times.Exactly(3));
    }

    [Theory]
    [InlineData(300, false, 0)]
    [InlineData(800, false, 0)]
    [InlineData(0, true, 0)]
    [InlineData(0, false, 1)]
    public async Task AWatchStartedAfterRankingMustNotReappearInANewPlaylist(long position, bool completed, int playCount)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/media/movie.mkv", RunTimeTicks = 1000 };
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([movie.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(movie.Id)).Returns(movie);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, movie)).Returns(new UserItemData { Key = "progress", Played = completed, PlaybackPositionTicks = position, PlayCount = playCount });
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, Recommendations = [new() { ItemId = movie.Id, ItemType = "Movie" }] }], TestContext.Current.CancellationToken);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    public async Task PlaylistDeliveryUsesOnlyTheUserVisibleCopyOfTheSameProviderTitle()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "p", "r") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var visible = new Movie { Id = Guid.NewGuid(), Path = "/media/visible/movie.mkv", ProviderIds = new() { ["Tmdb"] = "900" } };
        var hidden = Guid.NewGuid();
        var accessPredicates = new List<Guid[]>();
        _libraryManagerMock.Setup(manager => manager.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), user))
            .Callback<InternalItemsQuery, Jellyfin.Database.Implementations.Entities.User>((query, _) => accessPredicates.Add(query.ItemIds.ToArray()));
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([visible.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(visible.Id)).Returns(visible);
        _libraryManagerMock.Setup(manager => manager.GetItemById(hidden)).Throws(new InvalidOperationException("A hidden library copy must not be resolved."));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, visible)).Returns(new UserItemData { Key = "unseen", Played = false });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object);
        await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, UserName = user.Username, Recommendations = [new() { ItemId = hidden, ItemType = "Movie", TmdbId = 900 }, new() { ItemId = visible.Id, ItemType = "Movie", TmdbId = 900 }] }], TestContext.Current.CancellationToken);
        Assert.Equal(new[] { visible.Id }, delivered);
        _libraryManagerMock.Verify(manager => manager.GetItemById(hidden), Times.Never);
        Assert.NotEmpty(accessPredicates);
        Assert.All(accessPredicates, predicate => Assert.Empty(predicate));
    }

    [Fact]
    public async Task CreatedPrivatePlaylistKeepsOwnerProvenanceAndIsCleanedAfterAccountDeletion()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = Guid.NewGuid() };
        var other = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = Guid.NewGuid() };
        var deleted = false;
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns(() => deleted ? new[] { other } : [user, other]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(() => deleted ? null : user);
        _userManagerMock.Setup(manager => manager.GetUserById(other.Id)).Returns(other);
        var stored = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist>();
        _libraryManagerMock.Setup(manager => manager.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => stored.GetValueOrDefault(id));
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
            stored.Values.Where(playlist => query.User is null || playlist.OwnerUserId == query.User.Id).Cast<BaseItem>().ToArray());
        _libraryManagerMock.Setup(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((item, _) => stored.Remove(item.Id));
        _libraryManagerMock.Setup(manager => manager.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _playlistManagerMock.Setup(manager => manager.GetPlaylistsFolder(user.Id)).Returns(new Folder { Id = Guid.NewGuid(), Path = "/data/playlists" });
        PlaylistCreationRequest? captured = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).ReturnsAsync((PlaylistCreationRequest request) =>
        {
            captured = request;
            var playlist = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = request.UserId, Name = request.Name, Path = "/data/playlists/managed" };
            playlist.LinkedChildren = request.ItemIdList.Select(id => LinkedChild.Create(new Movie { Id = id })).ToArray();
            stored.Add(playlist.Id, playlist);
            return new PlaylistCreationResult(playlist.Id.ToString());
        });
        var service = CreateSut();
        Assert.Equal(1, (await service.UpdatePlaylistsForAllUsersAsync([CreateResult(user.Id, user.Username, 1)], TestContext.Current.CancellationToken)).PlaylistsCreated);
        var created = Assert.Single(stored.Values);
        Assert.Equal(user.Id, captured!.UserId);
        Assert.False(captured.Public);
        Assert.Empty(captured.Users);
        Assert.Equal(user.Id.ToString("N"), created.ProviderIds[RecommendationPlaylistService.ManagedOwnerProviderKey]);
        _playlistManagerMock.Verify(manager => manager.SavePlaylistFile(created), Times.Never);
        var manual = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = RecommendationPlaylistService.BuildPlaylistName(user.Username) + "_Backup" };
        var otherOwned = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = other.Id, Name = RecommendationPlaylistService.BuildPlaylistName(other.Username), ProviderIds = new() { [RecommendationPlaylistService.ManagedOwnerProviderKey] = other.Id.ToString("N") } };
        stored.Add(manual.Id, manual);
        stored.Add(otherOwned.Id, otherOwned);
        deleted = true;
        var cleanup = await service.UpdatePlaylistsForAllUsersAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(1, cleanup.OldPlaylistsRemoved);
        Assert.DoesNotContain(created.Id, stored.Keys);
        Assert.Contains(manual.Id, stored.Keys);
        Assert.Contains(otherOwned.Id, stored.Keys);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public async Task InactiveOwnerSweepNeedsACompleteRosterAndVerifiedOwnership(bool rosterAvailable, bool ownerUnavailable, int expectedRemoved)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("Disabled", "default", "default") { Id = Guid.NewGuid() };
        user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns(rosterAvailable ? new[] { user } : null!);
        if (ownerUnavailable) _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Throws(new IOException("test owner unavailable"));
        else _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var playlist = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id, Name = "Earlier managed name", ProviderIds = new() { [RecommendationPlaylistService.ManagedOwnerProviderKey] = user.Id.ToString("N") } };
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.User is null ? [playlist] : []);
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(expectedRemoved, result.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(manager => manager.DeleteItem(playlist, It.IsAny<DeleteOptions>()), Times.Exactly(expectedRemoved));
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    public async Task AVisiblePlaylistWithAnotherOwnerIsPreservedEvenWhenItsNameMatches()
    {
        var user = Guid.NewGuid();
        SetupUserManagerSingleUser(user, "Alice");
        var other = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Name = RecommendationPlaylistService.BuildPlaylistName("Alice") };
        SetupPlaylistLookup([other]);
        Assert.Equal(0, await CreateSut().RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
        _libraryManagerMock.Verify(manager => manager.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task CancellationDuringDatabaseOnlyPlaylistCleanupIsPropagated()
    {
        var user = Guid.NewGuid();
        SetupUserManagerSingleUser(user, "Alice");
        var playlist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup([playlist]);
        _libraryManagerMock.Setup(manager => manager.DeleteItem(playlist, It.IsAny<DeleteOptions>())).Callback<BaseItem, DeleteOptions>((_, options) =>
        {
            if (options.DeleteFileLocation) throw new IOException("test file-location failure");
            throw new OperationCanceledException("test cancelled fallback");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateSut().RemoveAllRecommendationPlaylistsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FinalPlaylistResolutionRechecksCurrentLibraryScopeWatchedEpisodesVirtualStubsAndParentalPolicy()
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "default", "default") { Id = Guid.NewGuid(), MaxParentalRatingScore = 12 };
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns([user]);
        _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        var series = new Series { Id = Guid.NewGuid(), Path = "/media/show", InheritedParentalRatingValue = 6 };
        Episode Episode(int index, string? path = null, bool virtualItem = false, int rating = 6) => new()
        {
            Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = index,
            Path = path ?? $"/media/show/{index}.mkv", IsVirtualItem = virtualItem, InheritedParentalRatingValue = rating
        };
        var watched = Episode(1);
        var excluded = Episode(2, "/media/excluded/episode.mkv");
        var flagged = Episode(3, virtualItem: true);
        var restricted = Episode(4, rating: 18);
        var pluginStub = Episode(5, "/data/plugins/JellyNext/virtual/episode.mkv");
        var permitted = Episode(6);
        var watchedMovie = new Movie { Id = Guid.NewGuid(), Path = "/media/watched.mkv", InheritedParentalRatingValue = 6 };
        var excludedMovie = new Movie { Id = Guid.NewGuid(), Path = "/media/excluded/movie.mkv", InheritedParentalRatingValue = 6 };
        var allowedMovie = new Movie { Id = Guid.NewGuid(), Path = "/media/allowed.mkv", InheritedParentalRatingValue = 6 };
        var result = new RecommendationResult
        {
            UserId = user.Id, UserName = user.Username,
            Recommendations = [new() { ItemId = watchedMovie.Id, ItemType = "Movie" }, new() { ItemId = excludedMovie.Id, ItemType = "Movie" }, new() { ItemId = series.Id, ItemType = "Series" }, new() { ItemId = allowedMovie.Id, ItemType = "Movie" }]
        };
        foreach (var item in new BaseItem[] { series, watchedMovie, excludedMovie, allowedMovie }) _libraryManagerMock.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
        _libraryManagerMock.Setup(manager => manager.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([series.Id, watchedMovie.Id, excludedMovie.Id, allowedMovie.Id, permitted.Id]);
        _libraryManagerMock.Setup(manager => manager.GetItemById(permitted.Id)).Returns(permitted);
        _libraryManagerMock.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Allowed", Locations = ["/media", "/data"] },
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Excluded", Locations = ["/media/excluded"] }
        ]);
        _libraryManagerMock.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Episode) ? [watched, excluded, flagged, restricted, pluginStub, permitted] : []);
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = item.Id.ToString(), Played = item.Id == watched.Id || item.Id == watchedMovie.Id });
        var config = new Mock<IPluginConfigurationService>();
        config.Setup(value => value.GetConfiguration()).Returns(new PluginConfiguration { ExcludedLibraries = "Excluded" });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Callback<PlaylistCreationRequest>(request => delivered = request.ItemIdList.ToArray()).ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        var service = CreateSut(data.Object,
            new PlaylistLibraryServices(config.Object, Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.TestMockFactory.CreateVirtualItemDetector()));
        var synced = await service.UpdatePlaylistsForAllUsersAsync([result], TestContext.Current.CancellationToken);
        Assert.Equal(1, synced.PlaylistsCreated);
        Assert.Equal(new[] { permitted.Id, allowedMovie.Id }, delivered);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.Episode) && query.User == user)), Times.Once);
        Assert.Equal(4, result.Recommendations.Count);
    }

    private RecommendationPlaylistService CreateSut(IUserDataManager? data = null, PlaylistLibraryServices? libraryServices = null, Action<string, string>? write = null, Action<string>? delete = null, long journalBudget = PlaylistCreationJournal.MaximumJournalBytes, bool emulateRegistration = true) =>
        new(
            emulateRegistration ? RegistrationAwarePlaylistManager() : _playlistManagerMock.Object,
            _userManagerMock.Object,
            _libraryManagerMock.Object,
            _pluginLogMock.Object,
            _loggerMock.Object,
            data,
            libraryServices)
        {
            RecoveryDirectory = _recoveryDirectory,
            WriteCreationState = write ?? ((path, contents) => Jellyfin.Plugin.JellyfinHelper.Services.Common.AtomicFile.WriteAllText(path, contents)),
            DeleteCreationState = delete ?? File.Delete,
            CreationJournalByteBudget = journalBudget
        };

    private static RecommendationResult CreateResult(Guid userId, string userName, int itemCount)
    {
        var items = new Collection<RecommendedItem>();
        for (var i = 0; i < itemCount; i++)
        {
            items.Add(new RecommendedItem
            {
                ItemId = Guid.NewGuid(),
                Name = $"Item {i}",
                ItemType = "Movie",
                Score = 1.0 - (i * 0.05)
            });
        }

        return new RecommendationResult
        {
            UserId = userId,
            UserName = userName,
            Recommendations = items
        };
    }

    /// <summary>
    ///     Convenience wrapper: sets up the playlist-lookup query to return an empty list. Equivalent to SetupPlaylistLookup(Array.Empty&lt;BaseItem&gt;()).
    /// </summary>
    private void SetupPlaylistQuery() => SetupPlaylistLookup(Array.Empty<BaseItem>());

    private void SetupEpisodeResolution(Dictionary<Guid, Guid>? seriesEpisodeMap = null)
    {
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns<InternalItemsQuery>(query =>
            {
                if (query.AncestorIds is { Length: > 0 })
                {
                    var seriesId = query.AncestorIds[0];
                    if (seriesEpisodeMap != null && seriesEpisodeMap.TryGetValue(seriesId, out var episodeId))
                    {
                        return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = episodeId, Path = "/media/ep.mkv" } };
                    }

                    return new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Path = "/media/ep.mkv" } };
                }

                return new List<BaseItem>();
            });
    }

    [Fact]
    public async Task UpdatePlaylists_CreatesPlaylistForEachUser()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var results = new List<RecommendationResult>
        {
            CreateResult(user1, "Alice", 5),
            CreateResult(user2, "Bob", 3)
        };

        SetupEnabledUsers(results);
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(() => new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(2, syncResult.PlaylistsCreated);
        Assert.Equal(8, syncResult.TotalItemsAdded);
        Assert.Equal(0, syncResult.PlaylistsFailed);
    }

    [Fact]
    public async Task UpdatePlaylists_CreatesPrivatePlaylistsPerUser()
    {
        var userId = Guid.NewGuid();
        var results = new List<RecommendationResult> { CreateResult(userId, "Alice", 3) };
        SetupEnabledUsers(results);

        PlaylistCreationRequest? captured = null;
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => captured = req)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(userId, captured.UserId);
        Assert.Equal(false, captured.Public);
        Assert.NotNull(captured.Users);
        Assert.Empty(captured.Users);
    }

    [Fact]
    public async Task UpdatePlaylists_SkipsUsersWithNoRecommendations()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 0)
        };

        SetupEnabledUsers(results);
        SetupPlaylistQuery();
        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.TotalItemsAdded);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task UpdatePlaylists_100Users20Items_CompletesWithin5Seconds()
    {
        var results = new List<RecommendationResult>(100);
        var users = new List<Jellyfin.Database.Implementations.Entities.User>(100);
        for (var i = 0; i < 100; i++)
        {
            var userId = Guid.NewGuid();
            results.Add(CreateResult(userId, $"User{i}", 20));

            // The SUT verifies each user before building a playlist and skips any it cannot resolve,
            // so the full roster must be configured or every user is skipped and nothing is created.
            var user = new Jellyfin.Database.Implementations.Entities.User($"User{i}", "default", "default") { Id = userId };
            users.Add(user);
            _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
        }

        _userManagerMock.Setup(m => m.GetUsers()).Returns(users);

        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(() => new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);
        sw.Stop();

        Assert.Equal(100, syncResult.PlaylistsCreated);
        Assert.Equal(2000, syncResult.TotalItemsAdded);
        if (Environment.GetEnvironmentVariable("RUN_PERF_ASSERTS") == "1")
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "Sync took too long: " + sw.ElapsedMilliseconds + "ms");
        }
    }

    [Fact]
    public async Task UpdatePlaylists_HandlesCreationFailureGracefully()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 5)
        };

        SetupEnabledUsers(results);
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ThrowsAsync(new InvalidOperationException("Playlist creation failed"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
    }

    [Fact]
    public async Task UpdatePlaylists_CancellationRespected()
    {
        var results = new List<RecommendationResult>
        {
            CreateResult(Guid.NewGuid(), "Alice", 5)
        };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(results, cts.Token));
    }

    [Fact]
    public async Task UpdatePlaylists_EmptyResultsList_Succeeds()
    {
        var results = new List<RecommendationResult>();
        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.TotalItemsAdded);
        Assert.Equal(0, syncResult.PlaylistsFailed);
    }

    [Fact]
    public void BuildPlaylistName_ContainsPrefixAndUserName()
    {
        var name = RecommendationPlaylistService.BuildPlaylistName("Alice");

        Assert.StartsWith(RecommendationPlaylistService.PlaylistNamePrefix, name);
        Assert.Contains("for Alice", name);
    }

    [Fact]
    public void BuildPlaylistName_FallsBackToYou_WhenNameEmpty()
    {
        var name = RecommendationPlaylistService.BuildPlaylistName("");

        Assert.StartsWith(RecommendationPlaylistService.PlaylistNamePrefix, name);
        Assert.Contains("for you", name);
    }

    [Fact]
    public async Task UpdatePlaylists_NullResults_ThrowsArgumentNull()
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlaylists_PreservesEngineOrder()
    {
        var userId = Guid.NewGuid();
        // Verify that the service preserves the engine's diversity-reranked order (no re-sort by score)
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>
            {
                new() { ItemId = Guid.NewGuid(), Name = "Low",  ItemType = "Movie", Score = 0.30 },
                new() { ItemId = Guid.NewGuid(), Name = "High", ItemType = "Movie", Score = 0.95 },
                new() { ItemId = Guid.NewGuid(), Name = "Mid",  ItemType = "Movie", Score = 0.60 }
            }
        };
        var results = new List<RecommendationResult> { result };
        SetupEnabledUsers(results);

        SetupPlaylistQuery();

        IReadOnlyList<Guid>? capturedItemIds = null;
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => capturedItemIds = req.ItemIdList)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        await sut.UpdatePlaylistsForAllUsersAsync(results, CancellationToken.None);

        Assert.NotNull(capturedItemIds);
        var expectedIds = result.Recommendations
            .Select(r => r.ItemId)
            .ToArray();
        Assert.Equal(expectedIds, capturedItemIds);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MoviesPassedThrough()
    {
        var sut = CreateSut();
        var movieId = Guid.NewGuid();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = movieId, Name = "Test Movie", ItemType = "Movie", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(movieId, resultItem);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolvedToFirstEpisode()
    {
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Breaking Bad", ItemType = "Series", Score = 0.95 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(episodeId, resultItem);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesWithNoEpisodes_Skipped()
    {
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>());

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "Empty Series", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MixedContent_CorrectlyResolved()
    {
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = movieId, Name = "Inception", ItemType = "Movie", Score = 0.95 },
            new() { ItemId = seriesId, Name = "Breaking Bad", ItemType = "Series", Score = 0.90 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        Assert.Equal(2, result.Length);
        Assert.Equal(movieId, result[0]);
        Assert.Equal(episodeId, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_MultipleSeriesEachResolvedOnce()
    {
        var series1 = Guid.NewGuid();
        var series2 = Guid.NewGuid();
        var ep1 = Guid.NewGuid();
        var ep2 = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid>
        {
            { series1, ep1 },
            { series2, ep2 }
        });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = series1, Name = "Series A", ItemType = "Series", Score = 0.9 },
            new() { ItemId = series2, Name = "Series B", ItemType = "Series", Score = 0.8 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        // Each series should produce exactly one episode entry
        Assert.Equal(2, result.Length);
        Assert.Equal(ep1, result[0]);
        Assert.Equal(ep2, result[1]);
    }

    [Fact]
    public async Task UpdatePlaylists_WithSeriesRecommendations_ResolvesToEpisodes()
    {
        // Arrange - mixed recommendations with movies and series
        var userId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = new Collection<RecommendedItem>
            {
                new() { ItemId = movieId, Name = "Movie", ItemType = "Movie", Score = 0.95 },
                new() { ItemId = seriesId, Name = "Series", ItemType = "Series", Score = 0.85 }
            }
        };

        SetupEnabledUsers([result]);
        SetupPlaylistQuery();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        IReadOnlyList<Guid>? capturedItemIds = null;
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(req => capturedItemIds = req.ItemIdList)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();

        // Act
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { result }, CancellationToken.None);

        // Assert - playlist should contain movie ID + resolved episode ID (not series ID)
        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(2, syncResult.TotalItemsAdded);
        Assert.NotNull(capturedItemIds);
        Assert.Equal(2, capturedItemIds.Count);
        Assert.Contains(movieId, capturedItemIds);
        Assert.Contains(episodeId, capturedItemIds);
        Assert.DoesNotContain(seriesId, capturedItemIds);
    }

    private static MediaBrowser.Controller.Playlists.Playlist BuildFakePlaylist(string name)
    {
        // Playlist is a Folder subclass in Jellyfin; using the concrete Playlist type
        // keeps the property surface (Name, Id) intact for the SUT.
        return new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private void SetupUserManagerSingleUser(Guid userId, string username)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = userId
        };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
    }

    private void SetupEnabledUsers(IEnumerable<RecommendationResult> results)
    {
        var users = results.Select(result => new Jellyfin.Database.Implementations.Entities.User(result.UserName, "default", "default") { Id = result.UserId }).ToArray();
        _userManagerMock.Setup(manager => manager.GetUsers()).Returns(users);
        foreach (var user in users) _userManagerMock.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
    }

    private void SetupUserManagerDisabledUser(Guid userId, string username)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User(username, "default", "default")
        {
            Id = userId
        };
        user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock.Setup(m => m.GetUserById(userId)).Returns(user);
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledUserLookupCancelled_Propagates()
    {
        // Cancellation during disabled-user cleanup must propagate rather than
        // masquerade as a failed removal, or the sync would report success.
        var userId = Guid.NewGuid();
        var user = new Jellyfin.Database.Implementations.Entities.User("Disabled", "default", "default") { Id = userId };
        user.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock.Setup(m => m.GetUserById(userId)).Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(
                new List<RecommendationResult> { CreateResult(userId, "Disabled", 2) },
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlaylists_RosterUnavailableAndLookupCancelled_Propagates()
    {
        // Without a roster every result resolves against the live user record; a
        // cancelled lookup there must propagate instead of degrading to a skip.
        var userId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.GetUsers()).Returns((IEnumerable<Jellyfin.Database.Implementations.Entities.User>)null!);
        _userManagerMock.Setup(m => m.GetUserById(userId)).Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(
                new List<RecommendationResult> { CreateResult(userId, "Alice", 2) },
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledUserWithResults_RemovesStalePlaylistsAndSkipsCreation()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerDisabledUser(userId, "Disabled");
        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Disabled"));
        SetupPlaylistLookup(new[] { stale });

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Disabled", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledUserWithoutResults_RemovesStalePlaylistsViaSweep()
    {
        var aliceId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bobId = Guid.NewGuid();
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        bob.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);

        var bobStale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        SetupPlaylistLookup(new BaseItem[] { bobStale });
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(aliceId, "Alice", 2) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(bobStale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_ExactNameMatch_IsRemoved()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var managed = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { managed });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(managed, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NumericSuffix_IsRemoved()
    {
        // Jellyfin auto-appends numeric suffixes to disambiguate identical playlist names.
        // We must recognize e.g. "Recommended for Alice1", "Alice2", "Alice42" as managed.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");
        var suffix1 = BuildFakePlaylist(expected + "1");
        var suffix42 = BuildFakePlaylist(expected + "42");
        SetupPlaylistLookup(new[] { suffix1, suffix42 });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(suffix1, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(suffix42, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NonNumericSuffix_IsPreserved()
    {
        // A user who manually copied our playlist and appended a custom suffix
        // (e.g. "Recommended for Alice_Backup") must NOT be silently deleted.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");
        var manual = BuildFakePlaylist(expected + "_ManualCopy");
        var alsoManual = BuildFakePlaylist(expected + " Extra");
        SetupPlaylistLookup(new[] { manual, alsoManual });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_NameThatIsPrefixOfExpected_IsPreserved()
    {
        // Critical defense: a playlist named for another user whose username is a prefix of the current user's must NOT be deleted (this is the case the "AllDigit" check was explicitly introduced to prevent - see the guard comment in the SUT).
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var otherUsersPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Al"));
        SetupPlaylistLookup(new[] { otherUsersPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(otherUsersPlaylist, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_UnrelatedPlaylist_IsPreserved()
    {
        // A user-created playlist with no relation to our name pattern must be untouched.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var unrelated = BuildFakePlaylist("My Favorite 80s Movies");
        SetupPlaylistLookup(new[] { unrelated });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_UserNotFound_ReturnsZeroWithoutError()
    {
        // If GetUserById returns null, the SUT must silently skip that user (not throw).
        var userId = Guid.NewGuid();
        var user = new Jellyfin.Database.Implementations.Entities.User("Ghost", "default", "default") { Id = userId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { user });
        _userManagerMock
            .Setup(m => m.GetUserById(userId))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_UserDeletedAfterRosterLoad_DoesNotResolveItemsOrCreatePlaylist()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Deleted");
        _userManagerMock.Setup(manager => manager.GetUserById(userId)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);
        var recommendation = CreateResult(userId, "Deleted", 3);
        recommendation.Recommendations[0].ItemType = "Series";
        var result = await CreateSut().UpdatePlaylistsForAllUsersAsync([recommendation], CancellationToken.None);

        Assert.Equal(0, result.PlaylistsCreated);
        _playlistManagerMock.Verify(manager => manager.CreatePlaylist(It.IsAny<MediaBrowser.Model.Playlists.PlaylistCreationRequest>()), Times.Never);
        _libraryManagerMock.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.Episode))), Times.Never);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_MixedManagedAndUnrelated_OnlyManagedRemoved()
    {
        // End-to-end sanity check: ensure only the exact managed names are removed
        // while all other playlists remain in place.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");
        var expected = RecommendationPlaylistService.BuildPlaylistName("Alice");

        var managedExact = BuildFakePlaylist(expected);
        var managedNumeric = BuildFakePlaylist(expected + "3");
        var otherUser = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        var manualCopy = BuildFakePlaylist(expected + "_Copy");
        var unrelated = BuildFakePlaylist("Weekend Watchlist");

        SetupPlaylistLookup(new BaseItem[] { managedExact, managedNumeric, otherUser, manualCopy, unrelated });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(managedExact, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(managedNumeric, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(otherUser, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(m => m.DeleteItem(manualCopy, It.IsAny<DeleteOptions>()), Times.Never);
        _libraryManagerMock.Verify(m => m.DeleteItem(unrelated, It.IsAny<DeleteOptions>()), Times.Never);
    }

    /// <summary>
    ///     Sets up the playlist-lookup query to return the given items for both the
    ///     initial existence probe and the subsequent cleanup lookup.
    /// </summary>
    private void SetupPlaylistLookup(IEnumerable<BaseItem> playlists)
    {
        var list = playlists.ToList();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Returns(list);
    }

    [Fact]
    public async Task UpdatePlaylists_CreatesNewPlaylistBeforeDeletingOldOnes()
    {
        // The order of side effects must be: Create -> Delete. If deletion ran first
        // and creation later failed, the user would be left with nothing.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        var operations = new List<string>();
        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(_ => operations.Add("Create"))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));
        _libraryManagerMock
            .Setup(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((_, _) => operations.Add("Delete"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        Assert.Equal(new[] { "Create", "Delete" }, operations);
    }

    [Fact]
    public async Task UpdatePlaylists_DoesNotDeleteFreshlyCreatedPlaylist()
    {
        // The cleanup pass runs against ALL managed-named playlists including the one we just created. The SUT must exclude the freshly created ID; otherwise it would delete its own new playlist.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var newPlaylistId = Guid.NewGuid();
        var freshlyCreated = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = newPlaylistId,
            Name = RecommendationPlaylistService.BuildPlaylistName("Alice")
        };
        SetupPlaylistLookup(new BaseItem[] { freshlyCreated });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(newPlaylistId.ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(freshlyCreated, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_NoRecommendations_StillRemovesStalePlaylists()
    {
        // When a user has zero recommendations we skip creation but MUST still clean
        // up any stale managed playlists so the user doesn't keep seeing outdated data.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { stale });

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 0) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_NoResolvableItems_StillRemovesStalePlaylists()
    {
        // A recommendation list of only unresolvable series (no episodes on disk) yields zero playable items. The SUT must skip creation but still remove stale playlists so the user isn't stuck with outdated recommendations.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { stale });

        // Series exists in the recommendations, but the library returns no episodes.
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>());

        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "Ghost Series", ItemType = "Series", Score = 0.9 }
        };
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "Alice",
            Recommendations = recs
        };

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { result },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_CreationReturnsEmptyId_CountsAsFailureAndPreservesOldPlaylists()
    {
        // Jellyfin's CreatePlaylist can return an empty ID under edge conditions (e.g. permission failure).
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(string.Empty));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_CreationThrows_PreservesOldPlaylists()
    {
        // When creation throws, we count it as a failure and MUST NOT delete old
        // playlists - otherwise a transient error would wipe the user's playlist.
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        var oldPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        SetupPlaylistLookup(new[] { oldPlaylist });

        _playlistManagerMock
            .Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(userId, "Alice", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.PlaylistsFailed);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    /// <summary>
    ///     Builds an <see cref="Episode"/> with explicit season/episode indexes and path.
    /// </summary>
    private static Episode BuildEpisode(int? season, int? episode, string path = "/media/ep.mkv")
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            ParentIndexNumber = season,
            IndexNumber = episode,
            Path = path
        };
    }

    [Fact]
    public void ResolvePlaylistItemIds_RespectsMaxItems()
    {
        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = Guid.NewGuid(), Name = "M1", ItemType = "Movie", Score = 0.9 },
            new() { ItemId = Guid.NewGuid(), Name = "M2", ItemType = "Movie", Score = 0.8 },
            new() { ItemId = Guid.NewGuid(), Name = "M3", ItemType = "Movie", Score = 0.7 },
            new() { ItemId = Guid.NewGuid(), Name = "M4", ItemType = "Movie", Score = 0.6 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, maxItems: 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(recs[0].ItemId, result[0]);
        Assert.Equal(recs[1].ItemId, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_UnresolvableSeries_BackfillsFromLaterCandidates()
    {
        // A series without episodes is skipped, and the resolver continues iterating so
        // the final list still reaches maxItems whenever enough valid candidates remain.
        var brokenSeries = Guid.NewGuid();
        var validSeries = Guid.NewGuid();
        var validEpisode = Guid.NewGuid();
        var trailingMovie = Guid.NewGuid();

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns<InternalItemsQuery>(query =>
            {
                if (query.AncestorIds is { Length: > 0 } && query.AncestorIds[0] == validSeries)
                {
                    return new List<BaseItem> { new Episode { Id = validEpisode, Path = "/media/ep.mkv" } };
                }

                return new List<BaseItem>();
            });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = brokenSeries, Name = "Broken", ItemType = "Series", Score = 0.95 },
            new() { ItemId = validSeries, Name = "Valid", ItemType = "Series", Score = 0.90 },
            new() { ItemId = trailingMovie, Name = "Movie", ItemType = "Movie", Score = 0.80 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, maxItems: 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(validEpisode, result[0]);
        Assert.Equal(trailingMovie, result[1]);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_DeprioritizesSpecials()
    {
        // When a series has both a specials episode (season 0) and a regular pilot,
        // the resolver must pick the pilot (S01E01), not the special (S00E01).
        var seriesId = Guid.NewGuid();
        var specialEpisode = BuildEpisode(season: 0, episode: 1);
        var pilotEpisode = BuildEpisode(season: 1, episode: 1);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { specialEpisode, pilotEpisode });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(pilotEpisode.Id, resultItem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSeriesDeliveryNeverUsesASpecialWhenNoRegularUnseenEpisodeRemains(bool regularUnseen)
    {
        var user = new Jellyfin.Database.Implementations.Entities.User("viewer", "test", "test");
        var playlists = new Dictionary<Guid, MediaBrowser.Controller.Playlists.Playlist>();
        ArrangeOwnedInventory(user, playlists);
        var series = new Series { Id = Guid.NewGuid(), Path = "/media/series" };
        var special = BuildEpisode(season: 0, episode: 1);
        var pilot = BuildEpisode(season: 1, episode: 1);
        _libraryManagerMock.Setup(library => library.GetItemById(series.Id)).Returns(series);
        _libraryManagerMock.Setup(library => library.GetItemById(special.Id)).Returns(special);
        _libraryManagerMock.Setup(library => library.GetItemById(pilot.Id)).Returns(pilot);
        _libraryManagerMock.Setup(library => library.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([series.Id, special.Id, pilot.Id]);
        _libraryManagerMock.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Episode) ? [special, pilot] : playlists.Values.Cast<BaseItem>().ToArray());
        var data = new Mock<IUserDataManager>();
        data.Setup(manager => manager.GetUserData(user, It.IsAny<BaseItem>())).Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) => new UserItemData { Key = "own episode", Played = item.Id == pilot.Id && !regularUnseen });
        Guid[]? delivered = null;
        _playlistManagerMock.Setup(manager => manager.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns((PlaylistCreationRequest request) =>
        {
            delivered = request.ItemIdList.ToArray();
            var created = new MediaBrowser.Controller.Playlists.Playlist { Id = Guid.NewGuid(), OwnerUserId = user.Id };
            playlists[created.Id] = created;
            return Task.FromResult(new PlaylistCreationResult(created.Id.ToString()));
        });
        var service = CreateSut(data.Object);
        var result = await service.UpdatePlaylistsForAllUsersAsync([new RecommendationResult { UserId = user.Id, UserName = user.Username, Recommendations = [new() { ItemId = series.Id, ItemType = "Series" }] }], TestContext.Current.CancellationToken);
        Assert.Equal(regularUnseen ? 1 : 0, result.PlaylistsCreated);
        if (regularUnseen) Assert.Equal(pilot.Id, Assert.Single(delivered!));
        else Assert.Null(delivered);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_SortsBySeasonThenEpisode()
    {
        // Given an arbitrarily ordered mix of episodes across multiple seasons,
        // the resolver must pick S01E01 as the "first" episode.
        var seriesId = Guid.NewGuid();
        var s2e5 = BuildEpisode(2, 5);
        var s1e3 = BuildEpisode(1, 3);
        var s1e1 = BuildEpisode(1, 1);
        var s3e1 = BuildEpisode(3, 1);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { s2e5, s1e3, s1e1, s3e1 });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(s1e1.Id, resultItem);
    }

    [Fact]
    public void ResolvePlaylistItemIds_SeriesResolution_SkipsEpisodesWithoutPath()
    {
        // Episodes without a file path represent unavailable media (metadata-only stubs)
        // and must never end up as the resolved first episode.
        var seriesId = Guid.NewGuid();
        var missingPathEpisode = BuildEpisode(1, 1, path: string.Empty);
        var playableEpisode = BuildEpisode(1, 2);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { missingPathEpisode, playableEpisode });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = "Series", Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(playableEpisode.Id, resultItem);
    }

    [Theory]
    [InlineData("Series")]
    [InlineData("series")]
    [InlineData("SERIES")]
    [InlineData("SeRiEs")]
    public void ResolvePlaylistItemIds_SeriesTypeMatch_IsCaseInsensitive(string itemType)
    {
        // The ItemType string arrives from serialized recommendation payloads and may vary in casing across engine versions.
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        SetupEpisodeResolution(new Dictionary<Guid, Guid> { { seriesId, episodeId } });

        var sut = CreateSut();
        var recs = new Collection<RecommendedItem>
        {
            new() { ItemId = seriesId, Name = "Show", ItemType = itemType, Score = 0.9 }
        };

        var result = sut.ResolvePlaylistItemIds(recs, 100);

        var resultItem = Assert.Single(result);
        Assert.Equal(episodeId, resultItem);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_MultipleUsers_AggregatesRemovedCount()
    {
        // With two users each owning a managed playlist, the returned total must equal
        // the sum of removals across all users.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);

        var alicePlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Alice"));
        var bobPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));

        // The library query is called once per user; return the full set both times and
        // let the SUT filter by each user's expected name.
        SetupPlaylistLookup(new BaseItem[] { alicePlaylist, bobPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(2, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(alicePlaylist, It.IsAny<DeleteOptions>()), Times.Once);
        _libraryManagerMock.Verify(m => m.DeleteItem(bobPlaylist, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_OneUserThrows_ContinuesWithRemainingUsers()
    {
        // A failure while processing one user must not abort the whole cleanup; other
        // users' playlists must still be removed.
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Throws(new InvalidOperationException("db hiccup"));
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);

        var bobPlaylist = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        SetupPlaylistLookup(new BaseItem[] { bobPlaylist });

        var sut = CreateSut();
        var removed = await sut.RemoveAllRecommendationPlaylistsAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        _libraryManagerMock.Verify(m => m.DeleteItem(bobPlaylist, It.IsAny<DeleteOptions>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAllRecommendationPlaylists_CancellationRespected()
    {
        var userId = Guid.NewGuid();
        SetupUserManagerSingleUser(userId, "Alice");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.RemoveAllRecommendationPlaylistsAsync(cts.Token));

        _libraryManagerMock.Verify(
            m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledUserCleanupThrows_ContinuesWithRemainingUsers()
    {
        // A cleanup failure for one disabled user must not stop synchronization
        // before remaining users are processed.
        var aliceId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        var bobId = Guid.NewGuid();
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        bob.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { alice, bob });
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Throws(new InvalidOperationException("db hiccup"));
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult>
            {
                CreateResult(bobId, "Bob", 3),
                CreateResult(aliceId, "Alice", 2)
            },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.PlaylistsFailed);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to remove stale playlists", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_GetUsersReturnsNull_StillSyncsEnabledUsers()
    {
        // A null user roster must degrade to "no disabled users" instead of throwing,
        // so enabled users still get their playlists.
        var aliceId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        _userManagerMock.Setup(m => m.GetUsers()).Returns((IEnumerable<Jellyfin.Database.Implementations.Entities.User>)null!);
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(aliceId, "Alice", 2) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
    }

    [Fact]
    public async Task UpdatePlaylists_GetUsersThrows_ContinuesSyncForEnabledUsers()
    {
        // A roster failure must not abort the sync: disabled-user cleanup is skipped
        // with a warning while enabled users are still processed.
        var aliceId = Guid.NewGuid();
        var alice = new Jellyfin.Database.Implementations.Entities.User("Alice", "default", "default") { Id = aliceId };
        _userManagerMock.Setup(m => m.GetUsers()).Throws(new InvalidOperationException("db hiccup"));
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Returns(alice);
        SetupPlaylistQuery();
        _playlistManagerMock.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString()));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(aliceId, "Alice", 2) },
            CancellationToken.None);

        Assert.Equal(1, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.PlaylistsFailed);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to list disabled users", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_GetUsersThrowsCancellation_Rethrows()
    {
        // Cancellation while listing users must propagate instead of degrading to an empty set.
        _userManagerMock.Setup(m => m.GetUsers()).Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(
                new List<RecommendationResult> { CreateResult(Guid.NewGuid(), "Alice", 2) },
                CancellationToken.None));

        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledSweepLookupThrows_ContinuesSync()
    {
        // Mirrors RemoveAllRecommendationPlaylists_OneUserThrows: a cleanup failure for a
        // disabled user must not fail the whole sync.
        var bobId = Guid.NewGuid();
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        bob.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { bob });
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Throws(new InvalidOperationException("db hiccup"));

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult>(),
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.OldPlaylistsRemoved);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("Failed to remove stale playlists", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_DisabledSweepCancellation_Rethrows()
    {
        // Cancellation during disabled-user cleanup must propagate instead of being swallowed.
        var bobId = Guid.NewGuid();
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        bob.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Returns(new[] { bob });
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null &&
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Playlist)))
            .Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.UpdatePlaylistsForAllUsersAsync(
                new List<RecommendationResult>(),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePlaylists_RosterUnavailable_DisabledUserResolvedViaLookup_RemovesStaleAndSkipsCreation()
    {
        // A stale result for a user disabled after generation must not create a playlist
        // when the roster is unavailable: the per-result GetUserById fallback routes them
        // through disabled-user cleanup instead.
        var bobId = Guid.NewGuid();
        var bob = new Jellyfin.Database.Implementations.Entities.User("Bob", "default", "default") { Id = bobId };
        bob.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        _userManagerMock.Setup(m => m.GetUsers()).Throws(new InvalidOperationException("db hiccup"));
        _userManagerMock.Setup(m => m.GetUserById(bobId)).Returns(bob);
        var stale = BuildFakePlaylist(RecommendationPlaylistService.BuildPlaylistName("Bob"));
        SetupPlaylistLookup(new[] { stale });

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(bobId, "Bob", 3) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(1, syncResult.OldPlaylistsRemoved);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _libraryManagerMock.Verify(
            m => m.DeleteItem(stale, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdatePlaylists_RosterUnavailable_UnknownUser_SkipsResult()
    {
        // When the roster is unavailable and the user no longer resolves, the result is
        // skipped instead of creating a playlist for a potentially deleted user.
        var ghostId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.GetUsers()).Returns((IEnumerable<Jellyfin.Database.Implementations.Entities.User>)null!);
        _userManagerMock.Setup(m => m.GetUserById(ghostId)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);
        SetupPlaylistQuery();

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(ghostId, "Ghost", 2) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.PlaylistsFailed);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdatePlaylists_RosterUnavailable_LookupThrows_SkipsResult()
    {
        // A failing per-user lookup must skip the result without failing the sync or
        // creating a playlist that could belong to a disabled user.
        var aliceId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.GetUsers()).Throws(new InvalidOperationException("db hiccup"));
        _userManagerMock.Setup(m => m.GetUserById(aliceId)).Throws(new InvalidOperationException("lookup hiccup"));
        SetupPlaylistQuery();

        var sut = CreateSut();
        var syncResult = await sut.UpdatePlaylistsForAllUsersAsync(
            new List<RecommendationResult> { CreateResult(aliceId, "Alice", 2) },
            CancellationToken.None);

        Assert.Equal(0, syncResult.PlaylistsCreated);
        Assert.Equal(0, syncResult.PlaylistsFailed);
        _playlistManagerMock.Verify(
            m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()),
            Times.Never);
        _pluginLogMock.Verify(
            m => m.LogWarning(
                "PlaylistSync",
                It.Is<string>(s => s.Contains("user lookup failed", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }
}
