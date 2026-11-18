using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.YearlyRewind;

public sealed class YearlyRewindStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-yearly-rewind-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _userId = Guid.NewGuid();

    private YearlyRewindSnapshot Snapshot(int year) => new()
    {
        UserId = _userId,
        PeriodStartUtc = new DateTimeOffset(year - 1, 12, 15, 0, 0, 0, TimeSpan.Zero),
        SnapshotAtUtc = new DateTimeOffset(year, 12, 15, 0, 0, 0, TimeSpan.Zero),
        GeneratedAtUtc = new DateTimeOffset(year, 12, 15, 0, 0, 1, TimeSpan.Zero),
        TimeZoneId = "UTC",
        DataState = "Partial",
        Items = [new YearlyRewindItem { ItemId = Guid.NewGuid(), ItemType = "Movie", Name = "映画", ActiveSeconds = 123, PlayCount = 1 }]
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Publish_AnnualReplacementKeepsExactlyOneReportWithoutArchive()
    {
        var store = new YearlyRewindStore(_directory);
        Assert.True(store.Publish(Snapshot(2026)));
        var next = Snapshot(2027);
        Assert.True(store.Publish(next));
        var restored = new YearlyRewindStore(_directory).Load(_userId)!;
        Assert.Equal(next.SnapshotId, restored.SnapshotId);
        Assert.Equal("映画", Assert.Single(restored.Items).Name);
        Assert.Equal(123, Assert.Single(restored.Items).ActiveSeconds);
        Assert.Single(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Publish_RepeatedAndDelayedOlderTriggersCannotMutateCurrentReport()
    {
        var store = new YearlyRewindStore(_directory);
        var current = Snapshot(2027);
        store.Publish(current);
        Assert.False(store.Publish(Snapshot(2027)));
        Assert.False(store.Publish(Snapshot(2026)));
        Assert.Equal(current.SnapshotId, store.Load(_userId)!.SnapshotId);
    }

    [Fact]
    public void Load_IsolatesUsersAndKeepsReportAfterDisplaySeason()
    {
        var store = new YearlyRewindStore(_directory);
        var original = Snapshot(2026);
        store.Publish(original);
        Assert.Null(store.Load(Guid.NewGuid()));
        // Loading has no clock or display-window dependency, including after a restart.
        Assert.Equal(original.SnapshotId, new YearlyRewindStore(_directory).Load(_userId)!.SnapshotId);
    }

    [Fact]
    public void Load_CorruptionDoesNotAllowPublicationOverExistingReport()
    {
        var store = new YearlyRewindStore(_directory);
        store.Publish(Snapshot(2026));
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories));
        File.WriteAllText(path, "{broken");
        Assert.Throws<JsonException>(() => store.Load(_userId));
        Assert.Throws<JsonException>(() => store.Publish(Snapshot(2027)));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void Publish_InvalidReplacementPreservesPreviousSnapshot()
    {
        var store = new YearlyRewindStore(_directory);
        var original = Snapshot(2026);
        store.Publish(original);
        var invalid = Snapshot(2027) with { PeriodStartUtc = DateTimeOffset.MaxValue };
        Assert.Throws<InvalidDataException>(() => store.Publish(invalid));
        Assert.Equal(original.SnapshotId, store.Load(_userId)!.SnapshotId);
    }

    [Fact]
    public void DeleteUser_DoesNotDeleteOtherUsers()
    {
        var store = new YearlyRewindStore(_directory);
        store.Publish(Snapshot(2026));
        var other = Snapshot(2026) with { UserId = Guid.NewGuid() };
        store.Publish(other);
        store.DeleteUser(_userId);
        Assert.Null(store.Load(_userId));
        Assert.NotNull(store.Load(other.UserId));
    }

    [Fact]
    public void InvalidIdentityAndRelativePathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new YearlyRewindStore("relative"));
        var store = new YearlyRewindStore(_directory);
        Assert.Throws<ArgumentException>(() => store.Load(Guid.Empty));
        Assert.Throws<ArgumentException>(() => store.Publish(Snapshot(2026) with { UserId = Guid.Empty }));
    }

    [Fact]
    public async Task ConcurrentTriggersThroughSameStorePublishOnce()
    {
        var store = new YearlyRewindStore(_directory);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.Publish(Snapshot(2026)))));
        Assert.Single(results, published => published);
        Assert.Single(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public void PendingRejectsOversizedUtf8BeforeWritingAndRetainsExistingPendingAndPublishedReports()
    {
        var store = new YearlyRewindStore(_directory);
        var pending = new YearlyRewindPendingStore(_directory);
        var current = Snapshot(2026);
        var next = Snapshot(2027);
        store.Publish(current);
        pending.Seal(next);
        var oversized = Snapshot(2028);
        oversized.Items[0] = oversized.Items[0] with { Name = new string('界', (int)(YearlyRewindStore.MaxSnapshotBytes / 3) + 1) };
        Assert.Throws<InvalidDataException>(() => pending.Seal(oversized));
        Assert.Equal(next.SnapshotId, pending.LoadPending(_userId)!.SnapshotId);
        Assert.Equal(current.SnapshotId, store.Load(_userId)!.SnapshotId);
    }

    [Fact]
    public void ExistingOversizedPendingIsQuarantinedWithoutBlockingAReplacement()
    {
        var store = new YearlyRewindStore(_directory);
        var pending = new YearlyRewindPendingStore(_directory);
        var current = Snapshot(2026);
        store.Publish(current);
        var directory = Path.Join(_directory, "yearly-rewind", "pending");
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, _userId.ToString("N") + ".json");
        using (var stream = File.Create(path)) stream.SetLength(YearlyRewindStore.MaxSnapshotBytes + 1);
        Assert.Null(pending.LoadPending(_userId));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory, "*.oversized-*"));
        Assert.Equal(current.SnapshotId, store.Load(_userId)!.SnapshotId);
        var next = Snapshot(2027);
        pending.Seal(next);
        Assert.True(store.Publish(pending.LoadPending(_userId)!));
        pending.Complete(_userId);
        Assert.Equal(next.SnapshotId, store.Load(_userId)!.SnapshotId);
    }

}
