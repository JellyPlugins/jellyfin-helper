using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.YearlyRewind;

public sealed class YearlyRewindPlaybackTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _user = Guid.NewGuid();
    private readonly UserPlaybackTracker _tracker = new();
    private readonly UserActivityHistory _history = new();
    private readonly UserPlaybackItem _item = new() { ItemId = Guid.NewGuid(), Name = "Film", ItemType = "Movie" };

    private void Observe(int at, int position, bool pause = false, bool automated = false, bool start = false, bool stop = false)
        => _tracker.Observe(_user, "session", _item, null, new UserPlaybackTracker.Progress(TimeSpan.FromSeconds(position).Ticks, pause, automated, start, stop, Start.AddSeconds(at)), _history);

    [Fact]
    public void ActualProgress_QualifiesAfter60SecondsAndDuplicateStopsDoNotAddTime()
    {
        Observe(0, 0, start: true);
        Observe(30, 30);
        Assert.Null(Assert.Single(_history.Sessions).QualifiedAtUtc);
        Observe(60, 60, stop: true);
        Observe(60, 60, stop: true);
        var session = Assert.Single(_history.Sessions);
        Assert.Equal(Start.AddSeconds(60), session.QualifiedAtUtc);
        Assert.Equal(60, Assert.Single(session.Intervals).EndUtc.Subtract(Start).TotalSeconds);
    }

    [Fact]
    public void PausesSeeksAndLongGapsDoNotCountAsWatchedTime()
    {
        Observe(0, 0, start: true);
        Observe(30, 30);
        Observe(60, 30, pause: true);
        Observe(90, 30);
        Observe(120, 900);
        Observe(150, 930);
        Observe(300, 1080);
        var session = Assert.Single(_history.Sessions);
        Assert.Equal(60, session.Intervals.Sum(span => (span.EndUtc - span.StartUtc).TotalSeconds));
    }

    [Fact]
    public void AutomatedProgressCannotInventWatchTime()
    {
        Observe(0, 0, start: true);
        Observe(60, 60, automated: true);
        Assert.Empty(Assert.Single(_history.Sessions).Intervals);
    }

    [Fact]
    public void DoubleSpeedCountsElapsedTimeRatherThanContentPosition()
    {
        Observe(0, 0, start: true);
        Observe(60, 120);
        Assert.Equal(60, Assert.Single(Assert.Single(_history.Sessions).Intervals).EndUtc.Subtract(Start).TotalSeconds);
    }

    [Fact]
    public void ShortTitlesQualifyAtHalfTheirRuntime()
    {
        _tracker.Observe(_user, "short", _item, TimeSpan.FromSeconds(20).Ticks, new UserPlaybackTracker.Progress(0, false, false, true, false, Start), _history);
        _tracker.Observe(_user, "short", _item, TimeSpan.FromSeconds(20).Ticks, new UserPlaybackTracker.Progress(TimeSpan.FromSeconds(10).Ticks, false, false, false, true, Start.AddSeconds(10)), _history);
        Assert.Equal(Start.AddSeconds(10), Assert.Single(_history.Sessions).QualifiedAtUtc);
    }

    [Fact]
    public void ConcurrentSessionsHaveConsistentUnionTimeAcrossItemsAndMonths()
    {
        Observe(0, 0, start: true);
        Observe(60, 60);
        var other = _item with { ItemId = Guid.NewGuid(), Name = "Other" };
        _tracker.Observe(_user, "other", other, null, new UserPlaybackTracker.Progress(0, false, false, true, false, Start), _history);
        _tracker.Observe(_user, "other", other, null, new UserPlaybackTracker.Progress(TimeSpan.FromSeconds(60).Ticks, false, false, false, true, Start.AddSeconds(60)), _history);
        _history.Coverage.Add(new UserActivityCoverage { StartUtc = Start.AddYears(-1), EndUtc = Start.AddDays(1) });
        var snapshot = YearlyRewindAggregator.Build(_user, _history, Start.AddDays(-1), Start.AddDays(1), Start.AddDays(1), TimeZoneInfo.Utc);
        Assert.Equal(60, snapshot.Items.Sum(item => item.ActiveSeconds));
        Assert.All(snapshot.Items, item => Assert.Equal(30, item.ActiveSeconds));
        Assert.Equal(60, snapshot.Months.Sum(month => month.ActiveSeconds));
        Assert.Equal(1, snapshot.ActiveDays);
        Assert.Equal("Ready", snapshot.DataState);
    }

    [Fact]
    public void ViewingMonths_PreserveUnionTimeAcrossDaylightSavingBoundaries()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var start = YearlyRewindWindow.LatestSnapshotAt(new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero), "03-30", zone);
        var cutoff = YearlyRewindWindow.NextSnapshotAt(start, "03-30", zone);
        var session = new UserPlaybackSession { Item = _item, QualifiedAtUtc = cutoff.AddMinutes(-10) };
        session.Intervals.Add(new UserPlaybackInterval(start, start.AddHours(2)));
        session.Intervals.Add(new UserPlaybackInterval(cutoff.AddHours(-2), cutoff));
        _history.Sessions.Add(session);
        var report = YearlyRewindAggregator.Build(_user, _history, start, cutoff, cutoff, zone);
        Assert.Equal(start, report.Months[0].StartUtc);
        Assert.Equal(cutoff, report.Months[^1].EndUtc);
        Assert.Equal(report.Items.Sum(item => item.ActiveSeconds), report.Months.Sum(month => month.ActiveSeconds));
        Assert.Equal(14400, report.Months.Sum(month => month.ActiveSeconds));
    }

    [Fact]
    public void QualificationAfterCutoffCannotChangeAlreadyFrozenReport()
    {
        Observe(0, 0, start: true);
        Observe(30, 30);
        var before = YearlyRewindAggregator.Build(_user, _history, Start.AddYears(-1), Start.AddSeconds(30), Start.AddSeconds(30), TimeZoneInfo.Utc);
        Observe(60, 60);
        var regenerated = YearlyRewindAggregator.Build(_user, _history, Start.AddYears(-1), Start.AddSeconds(30), Start.AddSeconds(60), TimeZoneInfo.Utc);
        Assert.Empty(before.Items);
        Assert.Empty(regenerated.Items);
    }
}
