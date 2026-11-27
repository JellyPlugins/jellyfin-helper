using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

/// <summary>Local statistics user selection, batch-first lookup and unchanged conservative failure handling.</summary>
public sealed class MediaStatisticsUserDataSourceTests
{
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IUserDataManager> _data = new();
    private readonly User _user = new("Alice", "default", "default") { Id = Guid.NewGuid() };
    private readonly BaseItem _item = new Video { Id = Guid.NewGuid() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorRejectsMissingDiagnosticDependencies(bool missingLog)
    {
        Assert.Throws<ArgumentNullException>(() => new MediaStatisticsUserDataSource(
            missingLog ? null! : TestMockFactory.CreatePluginLogService(),
            missingLog ? TestMockFactory.CreateLogger<MediaStatisticsUserDataSource>().Object : null!));
    }

    [Fact]
    public void EnabledUsersAreResolvedFreshAndDisabledAccountsNeverBecomeWatchEvidence()
    {
        var disabled = new User("Disabled", "default", "default") { Id = Guid.NewGuid() };
        disabled.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        var current = new List<User> { disabled, _user };
        _users.Setup(value => value.GetUsers()).Returns(() => current.AsQueryable());
        var source = Create();
        Assert.True(source.IsAvailable);
        Assert.Same(_user, Assert.Single(source.GetEnabledUsers()));
        current.Clear();
        Assert.Empty(source.GetEnabledUsers());
        _users.Verify(value => value.GetUsers(), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrFailedUserEnumerationIsConservativelyEmpty(bool failure)
    {
        if (failure) _users.Setup(value => value.GetUsers()).Throws(new InvalidOperationException("unavailable"));
        else _users.Setup(value => value.GetUsers()).Returns((IQueryable<User>)null!);
        Assert.Empty(Create().GetEnabledUsers());
    }

    [Fact]
    public void MissingUserServicesAndEmptyVideoListsMakeNoWatchDataCalls()
    {
        var missing = new MediaStatisticsUserDataSource(TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<MediaStatisticsUserDataSource>().Object);
        Assert.False(missing.IsAvailable);
        Assert.Empty(missing.GetEnabledUsers());
        Assert.Empty(missing.TryLoadBatch(_user, [_item])!);
        Assert.Null(missing.Lookup(_user, _item, null, "/media/video.mkv"));
        Assert.Empty(Create().TryLoadBatch(_user, [])!);
        _data.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingWatchDataReaderDoesNotInventOutcomesForKnownUsers()
    {
        _users.Setup(value => value.GetUsers()).Returns(new[] { _user }.AsQueryable());
        var source = new MediaStatisticsUserDataSource(TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<MediaStatisticsUserDataSource>().Object, userManager: _users.Object);
        Assert.False(source.IsAvailable);
        Assert.Single(source.GetEnabledUsers());
        Assert.Null(source.Lookup(_user, _item, null, "/media/video.mkv"));
    }

    [Fact]
    public void CompleteBatchEntriesAreAuthoritativeIncludingExplicitlyUnknownValues()
    {
        var outcome = new UserItemData { Key = "watch-data", Played = true, PlayCount = 4 };
        var batch = new Dictionary<Guid, UserItemData> { [_item.Id] = outcome };
        _data.Setup(value => value.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), _user)).Returns(batch);
        var source = Create();
        var loaded = source.TryLoadBatch(_user, [_item]);
        Assert.Same(batch, loaded);
        Assert.Same(outcome, source.Lookup(_user, _item, loaded, "/media/video.mkv"));
        batch[_item.Id] = null!;
        Assert.Null(source.Lookup(_user, _item, loaded, "/media/video.mkv"));
        _data.Verify(value => value.GetUserData(_user, _item), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrFailedBatchesRetainThePerItemFallback(bool failure)
    {
        if (failure) _data.Setup(value => value.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), _user)).Throws(new InvalidOperationException("unavailable"));
        else _data.Setup(value => value.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), _user)).Returns((Dictionary<Guid, UserItemData>)null!);
        var outcome = new UserItemData { Key = "watch-data", Played = true };
        _data.Setup(value => value.GetUserData(_user, _item)).Returns(outcome);
        var source = Create();
        var batch = source.TryLoadBatch(_user, [_item]);
        Assert.Null(batch);
        Assert.Same(outcome, source.Lookup(_user, _item, batch, "/media/video.mkv"));
    }

    [Fact]
    public void APartialBatchFallsBackAndAFailedUserDoesNotSuppressAnotherUser()
    {
        var other = new User("Bob", "default", "default") { Id = Guid.NewGuid() };
        _data.Setup(value => value.GetUserData(_user, _item)).Throws(new InvalidOperationException("unavailable"));
        var outcome = new UserItemData { Key = "watch-data", Played = true };
        _data.Setup(value => value.GetUserData(other, _item)).Returns(outcome);
        var source = Create();
        Assert.Null(source.Lookup(_user, _item, new Dictionary<Guid, UserItemData>(), "/media/video.mkv"));
        Assert.Same(outcome, source.Lookup(other, _item, null, "/media/video.mkv"));
    }

    [Fact]
    public void BatchCancellationRemainsAStopSignal()
    {
        _data.Setup(value => value.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), _user)).Throws(new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => Create().TryLoadBatch(_user, [_item]));
    }

    private MediaStatisticsUserDataSource Create()
        => new(TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<MediaStatisticsUserDataSource>().Object, _data.Object, _users.Object);
}
