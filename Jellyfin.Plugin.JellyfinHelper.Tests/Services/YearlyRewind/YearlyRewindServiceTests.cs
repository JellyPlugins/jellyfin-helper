using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.YearlyRewind;

public sealed class YearlyRewindServiceTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-rewind-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { YearlyRewindEnabled = true };
    private readonly Mock<IPluginConfigurationService> _configuration = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserActivityInsightsService> _source = new();
    private readonly Mock<IUserActivityCacheService> _cache = new();
    private readonly User _user = new("rewind", "default", "default") { Id = Guid.NewGuid() };
    private readonly TestClock _clock = new();
    private readonly YearlyRewindService _service;
    private readonly UserActivityHistoryService _history;

    public YearlyRewindServiceTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(path => path.DataPath).Returns(_directory);
        _configuration.SetupGet(config => config.IsInitialized).Returns(true);
        _configuration.Setup(config => config.GetConfiguration()).Returns(_config);
        _users.Setup(users => users.GetUsers()).Returns([_user]);
        _users.Setup(users => users.GetUserById(_user.Id)).Returns(_user);
        _service = new YearlyRewindService(_configuration.Object, paths.Object, NullLogger<YearlyRewindService>.Instance);
        _source.Setup(service => service.BuildActivityReport()).Returns(() => new UserActivityResult());
        _history = new UserActivityHistoryService(_configuration.Object, _sessions.Object, _users.Object, _library.Object,
            new VirtualItemDetector(paths.Object, NullLogger<VirtualItemDetector>.Instance), _source.Object, _cache.Object,
            paths.Object, _clock, [_service], NullLogger<UserActivityHistoryService>.Instance);
    }

    public void Dispose()
    {
        _history.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static DateTimeOffset Local(int year, int month, int day) => new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day), TimeZoneInfo.Local));

    private UserYearlyRewindController Controller(bool authenticated = true)
    {
        var controller = new UserYearlyRewindController(_service, _configuration.Object, _users.Object, _library.Object,
            _clock, NullLogger<UserYearlyRewindController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        if (authenticated) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", _user.Id.ToString())], "test"));
        return controller;
    }

    [Fact]
    public void Storage_UsesTheSameDataRootAsTheExistingActivityCache()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(path => path.DataPath).Returns(_directory);
        var activity = new Jellyfin.Plugin.JellyfinHelper.Services.Activity.UserActivityCacheService(
            paths.Object,
            Mock.Of<Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService>(),
            NullLogger<Jellyfin.Plugin.JellyfinHelper.Services.Activity.UserActivityCacheService>.Instance);
        activity.SaveResult(new Jellyfin.Plugin.JellyfinHelper.Services.Activity.UserActivityResult());
        _history.Tick(Local(2026, 12, 15));
        Assert.True(File.Exists(Path.Join(_directory, "jellyfin-helper-useractivity-latest.json")));
        Assert.True(File.Exists(Path.Join(_directory, "yearly-rewind", "reports", _user.Id.ToString("N") + ".json")));
    }

    [Fact]
    public void AnnualCutoff_GeneratesWithoutRequestAndOnlyReplacesNextYear()
    {
        _history.Tick(Local(2026, 12, 14));
        Assert.Null(_service.Load(_user.Id));
        _history.Tick(Local(2026, 12, 15));
        var first = _service.Load(_user.Id)!;
        Assert.Equal(Local(2026, 12, 15), first.SnapshotAtUtc);
        Assert.NotNull(first.Insights);
        Assert.Null(first.Comparison);
        _history.Tick(Local(2027, 1, 16));
        Assert.Equal(first.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
        _history.Tick(Local(2027, 12, 15));
        var second = _service.Load(_user.Id)!;
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.SnapshotAtUtc, second.Comparison!.SnapshotAtUtc);
        Assert.Equal(first.ActiveDays, second.Comparison.ActiveDays);
        Assert.Single(Directory.GetFiles(Path.Join(_directory, "yearly-rewind", "reports")));
    }

    [Fact]
    public void ReportRemainsAvailableAfterWindowAndRequestsNeverGenerate()
    {
        var controller = Controller();
        Assert.IsType<NotFoundResult>(controller.GetMyRewind());
        _history.Tick(Local(2026, 12, 15));
        _clock.Now = Local(2027, 1, 16);
        var original = _service.Load(_user.Id)!;
        Assert.IsType<OkObjectResult>(controller.GetMyRewind());
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        _history.Tick(_clock.Now);
        Assert.Equal(original.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
    }

    [Fact]
    public void DisabledFeatureDeniesAccessButDoesNotDeleteReport()
    {
        _history.Tick(Local(2026, 12, 15));
        var original = _service.Load(_user.Id)!;
        _config.YearlyRewindEnabled = false;
        _history.Tick(Local(2027, 12, 15));
        Assert.IsType<ForbidResult>(Controller().GetMyRewind());
        Assert.Equal(original.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
        Assert.IsType<UnauthorizedResult>(Controller(false).GetMyRewind());
        Assert.IsType<UnauthorizedResult>(Controller(false).GetStatus());
    }

    [Fact]
    public void ChangedServerZone_PreservesTheAlreadyPublishedYearAtNewYearsBoundary()
    {
        _config.YearlyRewindStartDate = "01-01";
        _config.YearlyRewindEndDate = "01-31";
        var cutoff = new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero);
        var original = new YearlyRewindSnapshot
        {
            UserId = _user.Id, PeriodStartUtc = cutoff.AddYears(-1), SnapshotAtUtc = cutoff,
            GeneratedAtUtc = cutoff, TimeZoneId = "Pacific/Kiritimati", DataState = "NotCollected"
        };
        new YearlyRewindStore(_directory).Publish(original);
        _history.Tick(Local(2026, 1, 2));
        Assert.Equal(original.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
    }

    [Fact]
    public void PendingPublicationSurvivesRestartWithItsOriginalFrozenContent()
    {
        var pending = new YearlyRewindSnapshot
        {
            UserId = _user.Id, PeriodStartUtc = Local(2025, 12, 15), SnapshotAtUtc = Local(2026, 12, 15),
            GeneratedAtUtc = Local(2026, 12, 15), TimeZoneId = TimeZoneInfo.Local.Id, DataState = "NotCollected"
        };
        new YearlyRewindPendingStore(_directory).Seal(pending);
        _history.Tick(Local(2026, 12, 16));
        Assert.Equal(pending.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
        Assert.Null(new YearlyRewindPendingStore(_directory).LoadPending(_user.Id));
    }

    [Fact]
    public void MissedYears_DiscardAnOlderPendingReportAndPublishOnlyTheLatestCycle()
    {
        new YearlyRewindPendingStore(_directory).Seal(new YearlyRewindSnapshot
        {
            UserId = _user.Id, PeriodStartUtc = Local(2023, 12, 15), SnapshotAtUtc = Local(2024, 12, 15),
            GeneratedAtUtc = Local(2024, 12, 15), TimeZoneId = TimeZoneInfo.Local.Id, DataState = "NotCollected"
        });
        _history.Tick(Local(2026, 12, 16));
        Assert.Equal(Local(2026, 12, 15), _service.Load(_user.Id)!.SnapshotAtUtc);
        Assert.Single(Directory.GetFiles(Path.Join(_directory, "yearly-rewind", "reports")));
        Assert.Null(new YearlyRewindPendingStore(_directory).LoadPending(_user.Id));
    }

    [Fact]
    public void CollectionHistory_RoundtripsSeparatelyAndRejectsForeignPendingIdentity()
    {
        var store = new UserActivityHistoryStore(_directory);
        var history = new UserActivityHistory();
        history.Coverage.Add(new UserActivityCoverage { StartUtc = Local(2026, 12, 14), EndUtc = Local(2026, 12, 15) });
        store.Save(_user.Id, history);
        Assert.Equal(Local(2026, 12, 14), Assert.Single(store.Load(_user.Id).Coverage).StartUtc);
        var foreign = new YearlyRewindSnapshot
        {
            UserId = Guid.NewGuid(), PeriodStartUtc = Local(2025, 12, 15), SnapshotAtUtc = Local(2026, 12, 15),
            GeneratedAtUtc = Local(2026, 12, 15), TimeZoneId = TimeZoneInfo.Local.Id, DataState = "NotCollected"
        };
        var pending = new YearlyRewindPendingStore(_directory);
        pending.Seal(foreign);
        var pendingPath = Path.Join(_directory, "yearly-rewind", "pending");
        File.Move(Path.Join(pendingPath, foreign.UserId.ToString("N") + ".json"), Path.Join(pendingPath, _user.Id.ToString("N") + ".json"));
        Assert.Throws<InvalidDataException>(() => pending.LoadPending(_user.Id));
    }

    [Fact]
    public void UserDeletionRemovesOnlyThatUsersStoredData()
    {
        _history.Tick(Local(2026, 12, 15));
        Assert.NotNull(_service.Load(_user.Id));
        _users.Setup(users => users.GetUsers()).Returns([]);
        _history.Tick(Local(2026, 12, 17));
        Assert.Null(_service.Load(_user.Id));
    }

    [Fact]
    public void AnnualCutoffWaitsForFreshBaselineAndKeepsPreviousReportDuringFailures()
    {
        var previous = new YearlyRewindSnapshot
        {
            UserId = _user.Id, PeriodStartUtc = Local(2024, 12, 15), SnapshotAtUtc = Local(2025, 12, 15),
            GeneratedAtUtc = Local(2025, 12, 15), TimeZoneId = TimeZoneInfo.Local.Id, DataState = "NotCollected"
        };
        new YearlyRewindStore(_directory).Publish(previous);
        _history.Tick(Local(2026, 12, 14));
        _source.Setup(service => service.BuildActivityReport()).Throws(new IOException("Jellyfin temporarily unavailable"));
        _cache.Setup(service => service.LoadResult()).Returns(new UserActivityResult());
        var cutoff = Local(2026, 12, 15);
        _history.Tick(cutoff, false); // Playback must not freeze a cutoff before its fresh baseline.
        Assert.Equal(previous.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
        _history.Tick(cutoff); // A cached baseline is insufficient when the fresh query fails.
        Assert.Equal(previous.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
        var snapshot = new UserActivityResult
        {
            Items = [new UserActivitySummary
            {
                ItemId = Guid.NewGuid(), ItemName = "Recovered movie", ItemType = "Movie", RuntimeTicks = TimeSpan.TicksPerHour,
                UserActivities = [new UserItemActivity { UserId = _user.Id, Played = true, PlayCount = 1, LastPlayedDate = cutoff.AddDays(-10).UtcDateTime }]
            }]
        };
        _source.Setup(service => service.BuildActivityReport()).Returns(snapshot);
        _history.Tick(cutoff.AddMinutes(1));
        var recovered = _service.Load(_user.Id)!;
        Assert.Equal(cutoff, recovered.SnapshotAtUtc);
        Assert.Equal("Recovered movie", Assert.Single(recovered.Items).Name);
        Assert.Equal(1, recovered.ReconstructedItemCount);
        _source.Setup(service => service.BuildActivityReport()).Returns(new UserActivityResult());
        _history.Tick(cutoff.AddHours(2));
        Assert.Equal(recovered.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("disabled")]
    public void ApiRejectsInvalidMissingAndDisabledUserIdentities(string identity)
    {
        var controller = Controller();
        if (identity == "invalid" || identity == "empty")
        {
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", identity == "invalid" ? "not-a-guid" : Guid.Empty.ToString())], "test"));
        }
        else if (identity == "missing")
        {
            _users.Setup(users => users.GetUserById(_user.Id)).Returns((User?)null);
        }
        else
        {
            _user.SetPermission(PermissionKind.IsDisabled, true);
        }

        Assert.IsType<UnauthorizedResult>(controller.GetStatus());
        Assert.IsType<UnauthorizedResult>(controller.GetMyRewind());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StatusDistinguishesDisabledMissingAndAvailableReports(bool enabled, bool stored)
    {
        YearlyRewindSnapshot? snapshot = stored ? StoredMovieReport() : null;
        _config.YearlyRewindEnabled = enabled;
        var controller = Controller();
        var status = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.GetStatus()).Value);
        Assert.Equal(enabled, status.GetProperty("Enabled").GetBoolean());
        Assert.Equal(enabled, status.GetProperty("TabVisible").GetBoolean());
        Assert.Equal(enabled && stored, status.GetProperty("Available").GetBoolean());
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        if (enabled && stored)
        {
            Assert.Equal(snapshot!.SnapshotId, status.GetProperty("SnapshotId").GetGuid());
            Assert.Equal(snapshot.SnapshotAtUtc, status.GetProperty("SnapshotAtUtc").GetDateTimeOffset());
            Assert.Equal("Ready", status.GetProperty("DataState").GetString());
            Assert.Equal(snapshot.SnapshotId, Assert.IsType<YearlyRewindSnapshot>(Assert.IsType<OkObjectResult>(controller.GetMyRewind()).Value).SnapshotId);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, status.GetProperty("SnapshotId").ValueKind);
            Assert.Equal("Unavailable", status.GetProperty("DataState").GetString());
        }
    }

    [Fact]
    public void UninitializedConfigurationDoesNotExposeAReport()
    {
        var original = StoredMovieReport();
        _configuration.SetupGet(config => config.IsInitialized).Returns(false);
        var controller = Controller();
        Assert.IsType<ForbidResult>(controller.GetMyRewind());
        var status = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.GetStatus()).Value);
        Assert.False(status.GetProperty("Enabled").GetBoolean());
        Assert.Equal(_clock.Now.AddMinutes(1), status.GetProperty("NextCheckAtUtc").GetDateTimeOffset());
        Assert.Equal(original.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
    }

    [Fact]
    public void FailedPermissionLookupReturnsUnavailableWithoutChangingTheReport()
    {
        var original = StoredMovieReport();
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Throws(new IOException("Library temporarily unavailable"));
        var controller = Controller();
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(controller.GetStatus()).StatusCode);
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(controller.GetMyRewind()).StatusCode);
        Assert.Equal(original.SnapshotId, _service.Load(_user.Id)!.SnapshotId);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("foreign")]
    public void UnreadableOrForeignReportsAreNotRebuiltOrExposed(string corruption)
    {
        var original = StoredMovieReport();
        var path = Path.Join(_directory, "yearly-rewind", "reports", _user.Id.ToString("N") + ".json");
        var content = corruption == "foreign" ? JsonSerializer.Serialize(original with { UserId = Guid.NewGuid() }, JsonDefaults.Options) : "not JSON";
        File.WriteAllText(path, content);
        var controller = Controller();
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(controller.GetStatus()).StatusCode);
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(controller.GetMyRewind()).StatusCode);
        Assert.Equal(content, File.ReadAllText(path));
    }

    private YearlyRewindSnapshot StoredMovieReport()
    {
        var cutoff = Local(2026, 12, 15);
        var snapshot = new YearlyRewindSnapshot
        {
            UserId = _user.Id, PeriodStartUtc = cutoff.AddYears(-1), SnapshotAtUtc = cutoff,
            GeneratedAtUtc = cutoff, TimeZoneId = TimeZoneInfo.Local.Id, DataState = "Ready",
            Items = [new YearlyRewindItem { ItemId = Guid.NewGuid(), ItemType = "Movie", Name = "Deleted movie", ActiveSeconds = 3600, PlayCount = 1 }]
        };
        new YearlyRewindStore(_directory).Publish(snapshot);
        return snapshot;
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 12, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
