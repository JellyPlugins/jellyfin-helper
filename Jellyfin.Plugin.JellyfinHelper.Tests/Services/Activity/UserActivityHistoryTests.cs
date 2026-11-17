using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Activity;

public sealed class UserActivityHistoryTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-shared-history-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DateTimeOffset _cutoff = new(2026, 12, 15, 0, 0, 0, TimeSpan.Zero);
    private readonly UserPlaybackItem _movie = new() { ItemId = Guid.NewGuid(), Name = "Movie", ItemType = "Movie", RuntimeTicks = TimeSpan.TicksPerHour, Genres = [" Drama ", "Drama"] };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private UserItemActivity State(DateTimeOffset? played, int count = 40, bool completed = true, long position = 0) => new()
    {
        UserId = _userId, LastPlayedDate = played?.UtcDateTime, PlayCount = count, Played = completed, PlaybackPositionTicks = position
    };

    private YearlyRewindSnapshot Build(UserActivityHistory history) => YearlyRewindAggregator.Build(
        _userId, history, _cutoff.AddYears(-1), _cutoff, _cutoff, TimeZoneInfo.Utc);

    [Fact]
    public void ObservationsAreIdempotentVersionMetadataAndRetainCounterResetsWithoutInventingDeltas()
    {
        var history = new UserActivityHistory();
        var played = _cutoff.AddDays(-10);
        Assert.True(history.Record(_movie, State(played), _cutoff, _cutoff));
        Assert.False(history.Record(_movie, State(played), _cutoff.AddMinutes(1), _cutoff));
        Assert.Null(history.Observations[0].PlayCountDelta);
        Assert.Single(history.Metadata);
        Assert.Equal("Drama", Assert.Single(history.Metadata.Values.Single().Genres));
        Assert.True(history.Record(_movie, State(played.AddDays(1), 42), _cutoff.AddMinutes(2), _cutoff.AddMinutes(2)));
        Assert.Equal(2, history.Observations[1].PlayCountDelta);
        Assert.True(history.Record(_movie, State(played.AddDays(2), 1), _cutoff.AddMinutes(3), _cutoff.AddMinutes(3)));
        Assert.Null(history.Observations[2].PlayCountDelta);
        Assert.False(history.Record(_movie, State(played), _cutoff.AddMinutes(4), _cutoff));
        Assert.True(history.Record(_movie with { Name = "Renamed" }, State(played.AddDays(2), 1), _cutoff.AddMinutes(5), _cutoff.AddMinutes(5)));
        Assert.Equal(2, history.Metadata.Count);
        var store = new UserActivityHistoryStore(_directory);
        store.Save(_userId, history);
        var saved = store.Load(_userId);
        Assert.Equal(4, saved.Observations.Count);
        Assert.Equal("Movie", saved.Observations[0].Item.Name);
        Assert.Equal("Renamed", saved.Observations[3].Item.Name);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Join(_directory, "activity-history", _userId.ToString("N") + ".json")));
        Assert.False(json.RootElement.GetProperty("observations")[0].TryGetProperty("item", out _));
    }

    [Fact]
    public void MigrationPreservesMeasuredFactsAndNeverReadsTheOldFileAgain()
    {
        var legacy = Path.Join(_directory, "yearly-rewind", "history", _userId.ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var id = Guid.NewGuid();
        var span = new UserPlaybackInterval(_cutoff.AddMinutes(-2), _cutoff.AddMinutes(-1));
        File.WriteAllText(legacy, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Coverage = new[] { new UserActivityCoverage { StartUtc = span.StartUtc, EndUtc = span.EndUtc } },
            Sessions = new[] { new { Id = id, Item = _movie, ThresholdSeconds = 1, QualifiedAtUtc = span.EndUtc, Intervals = new[] { span } } }
        }, JsonDefaults.Options));
        var store = new UserActivityHistoryStore(_directory);
        var migrated = store.Load(_userId);
        Assert.Equal(id, Assert.Single(migrated.Sessions).Id);
        Assert.Equal(_movie.ItemId, migrated.Sessions[0].Item.ItemId);
        Assert.Equal(span.StartUtc, Assert.Single(migrated.Coverage).StartUtc);
        Assert.Equal(60, Build(migrated).Items.Single().ActiveSeconds);
        File.WriteAllText(legacy, "broken rollback source");
        Assert.Equal(id, Assert.Single(store.Load(_userId).Sessions).Id);
        store.DeleteUser(_userId);
        store.DeleteUser(_userId);
        Assert.Empty(store.KnownUsers());
    }

    [Fact]
    public void BaselineUsesOneConservativePlayNotTheLifetimeCountAndDoesNotInventPatterns()
    {
        var history = new UserActivityHistory();
        history.Record(_movie, State(_cutoff.AddMonths(-2), 500), _cutoff.AddDays(-1), _cutoff.AddDays(-1));
        history.Record(_movie, State(_cutoff.AddMonths(-1), 501), _cutoff, _cutoff);
        var report = Build(history);
        var item = Assert.Single(report.Items);
        Assert.Equal(1, item.PlayCount);
        Assert.Equal(3600, item.ActiveSeconds);
        Assert.Equal(3600, item.EstimatedSeconds);
        Assert.True(item.HasReconstructedActivity);
        Assert.Equal(1, report.ReconstructedItemCount);
        Assert.Equal("Partial", report.DataState);
        Assert.Equal(0, report.ActiveDays);
        Assert.Equal(0, report.Insights!.LongestStreakDays);
        Assert.Equal(0, report.Insights.HourlySeconds.Sum());
        Assert.Equal(0, report.Insights.WeekdaySeconds.Sum());
        Assert.Equal(3600, report.Months.Sum(month => month.ActiveSeconds));
        Assert.Equal(3600, report.Months.Sum(month => month.EstimatedSeconds));
        Assert.Equal(3600, report.Months.SelectMany(month => month.Genres!).Sum(genre => genre.ActiveSeconds));
        var store = new YearlyRewindStore(_directory);
        store.Publish(report);
        Assert.Equal(1, store.Load(_userId)!.ReconstructedItemCount);
    }

    [Fact]
    public void MeasuredCoveragePreventsCountingTheSamePlaybackTwice()
    {
        var history = new UserActivityHistory();
        var played = _cutoff.AddMinutes(-30);
        history.Record(_movie, State(played), _cutoff, _cutoff);
        history.Coverage.Add(new UserActivityCoverage { StartUtc = played.AddHours(-1), EndUtc = _cutoff });
        history.Sessions.Add(new UserPlaybackSession
        {
            Item = _movie, QualifiedAtUtc = played.AddMinutes(1),
            Intervals = [new UserPlaybackInterval(played, played.AddMinutes(1))]
        });
        var report = Build(history);
        Assert.Equal(60, Assert.Single(report.Items).ActiveSeconds);
        Assert.False(report.Items[0].HasReconstructedActivity);
        Assert.Equal(60, report.Insights!.HourlySeconds.Sum());
    }

    [Fact]
    public void PartialPlaybackUsesKnownPositionAndUnknownDurationRemainsUnknown()
    {
        var history = new UserActivityHistory();
        history.Record(_movie, State(_cutoff.AddDays(-1), 1, false, TimeSpan.TicksPerMinute * 12), _cutoff, _cutoff);
        history.Record(_movie with { ItemId = Guid.NewGuid(), RuntimeTicks = 0 }, State(_cutoff.AddDays(-1)), _cutoff, _cutoff);
        history.Record(_movie with { ItemId = Guid.NewGuid() }, State(null), _cutoff, _cutoff);
        history.Record(_movie with { ItemId = Guid.NewGuid() }, State(_cutoff.AddDays(1)), _cutoff, _cutoff);
        history.Record(_movie with { ItemId = Guid.NewGuid() }, State(_cutoff.AddYears(-2)), _cutoff, _cutoff);
        var report = Build(history);
        Assert.Equal(2, report.Items.Count);
        Assert.Equal(720, report.Items.Sum(item => item.ActiveSeconds));
        Assert.Equal(2, report.ReconstructedItemCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedJournalAlwaysRecordsIndependentlyOfYearlyRewindAndIgnoresLegacyDisableFlag(bool rewind)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration><ActivityHistoryEnabled>false</ActivityHistoryEnabled></PluginConfiguration>");
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;
        config.YearlyRewindEnabled = rewind;
        var configuration = new Mock<IPluginConfigurationService>();
        configuration.SetupGet(value => value.IsInitialized).Returns(true);
        configuration.Setup(value => value.GetConfiguration()).Returns(config);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.DataPath).Returns(_directory);
        var user = new User("test", "default", "default") { Id = _userId };
        var users = new Mock<IUserManager>();
        users.Setup(value => value.GetUsers()).Returns([user]);
        users.Setup(value => value.GetUserById(_userId)).Returns(user);
        var source = new Mock<IUserActivityInsightsService>();
        var snapshot = new UserActivityResult { GeneratedAt = _cutoff.UtcDateTime };
        snapshot.Items.Add(new UserActivitySummary
        {
            ItemId = _movie.ItemId, ItemName = "Movie", ItemType = "Movie", RuntimeTicks = _movie.RuntimeTicks,
            UserActivities = [State(_cutoff.AddDays(-10))]
        });
        source.Setup(value => value.BuildActivityReport()).Returns(snapshot);
        var cache = new Mock<IUserActivityCacheService>();
        cache.Setup(value => value.LoadResult()).Returns(snapshot);
        var consumer = new YearlyRewindService(configuration.Object, paths.Object, NullLogger<YearlyRewindService>.Instance);
        using var service = new UserActivityHistoryService(configuration.Object, Mock.Of<ISessionManager>(), users.Object, Mock.Of<ILibraryManager>(),
            new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance), source.Object, cache.Object,
            paths.Object, new FixedClock(_cutoff), [consumer], NullLogger<UserActivityHistoryService>.Instance);
        service.Tick(_cutoff);
        var journal = Path.Join(_directory, "activity-history", _userId.ToString("N") + ".json");
        Assert.True(File.Exists(journal));
        if (!rewind)
        {
            Assert.Single(new UserActivityHistoryStore(_directory).Load(_userId).Observations);
            Assert.Null(consumer.Load(_userId));
            config.YearlyRewindEnabled = true;
            service.Tick(_cutoff.AddMinutes(1));
        }
        var report = consumer.Load(_userId)!;
        Assert.NotNull(report);
        Assert.Equal(1, report.ReconstructedItemCount);
        snapshot.Items.Clear();
        service.Record(snapshot);
        service.Tick(_cutoff.AddHours(2));
        Assert.Equal(report.SnapshotId, consumer.Load(_userId)!.SnapshotId);
        Assert.True(File.Exists(journal));
        cache.Verify(value => value.SaveResult(It.IsAny<UserActivityResult>()), Times.AtLeastOnce);
    }

    [Fact]
    public void FailedCacheImportDoesNotPreventJellyfinRefreshAndSourceFailuresRetry()
    {
        var config = new PluginConfiguration { YearlyRewindEnabled = false };
        var configuration = new Mock<IPluginConfigurationService>();
        configuration.SetupGet(value => value.IsInitialized).Returns(true);
        configuration.Setup(value => value.GetConfiguration()).Returns(config);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.DataPath).Returns(_directory);
        var user = new User("test", "default", "default") { Id = _userId };
        var users = new Mock<IUserManager>();
        users.Setup(value => value.GetUsers()).Returns([user]);
        users.Setup(value => value.GetUserById(_userId)).Returns(user);
        var snapshot = new UserActivityResult { GeneratedAt = _cutoff.UtcDateTime };
        snapshot.Items.Add(new UserActivitySummary { ItemId = _movie.ItemId, ItemName = "Movie", ItemType = "Movie", UserActivities = [State(_cutoff.AddDays(-1))] });
        var source = new Mock<IUserActivityInsightsService>();
        source.SetupSequence(value => value.BuildActivityReport()).Throws(new IOException("temporarily unavailable")).Returns(snapshot);
        var cache = new Mock<IUserActivityCacheService>();
        cache.Setup(value => value.LoadResult()).Throws(new IOException("bad cache"));
        using var service = new UserActivityHistoryService(configuration.Object, Mock.Of<ISessionManager>(), users.Object, Mock.Of<ILibraryManager>(),
            new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance), source.Object, cache.Object,
            paths.Object, new FixedClock(_cutoff), [], NullLogger<UserActivityHistoryService>.Instance);
        service.Tick(_cutoff);
        service.Tick(_cutoff.AddSeconds(30));
        source.Verify(value => value.BuildActivityReport(), Times.Once);
        service.Tick(_cutoff.AddMinutes(1));
        source.Verify(value => value.BuildActivityReport(), Times.Exactly(2));
        var store = new UserActivityHistoryStore(_directory);
        Assert.Single(store.Load(_userId).Observations);
        config.YearlyRewindEnabled = false;
        snapshot.Items[0].UserActivities[0].PlayCount++;
        service.Record(snapshot);
        Assert.Equal(2, store.Load(_userId).Observations.Count);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
