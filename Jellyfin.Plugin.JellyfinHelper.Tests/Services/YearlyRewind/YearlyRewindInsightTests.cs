using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.YearlyRewind;

public sealed class YearlyRewindInsightTests : IDisposable
{
    private readonly Guid _user = Guid.NewGuid();
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-rewind-insights-" + Guid.NewGuid().ToString("N"));
    private readonly UserActivityHistory _history = new();
    private readonly UserPlaybackItem _movie = new() { ItemId = Guid.NewGuid(), Name = "Movie", ItemType = "Movie", Genres = ["Drama", "Adventure"] };

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private void Watch(DateTimeOffset from, DateTimeOffset to, UserPlaybackItem? item = null)
    {
        var session = new UserPlaybackSession { Item = item ?? _movie, QualifiedAtUtc = to };
        session.Intervals.Add(new UserPlaybackInterval(from, to));
        _history.Sessions.Add(session);
    }

    private YearlyRewindSnapshot Build(TimeZoneInfo? zone = null, YearlyRewindSnapshot? previous = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var start = YearlyRewindWindow.LatestSnapshotAt(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), "01-01", zone);
        var cutoff = YearlyRewindWindow.NextSnapshotAt(start, "01-01", zone);
        _history.Coverage.Add(new UserActivityCoverage { StartUtc = start, EndUtc = cutoff });
        return YearlyRewindAggregator.Build(_user, _history, start, cutoff, cutoff, zone, previous);
    }

    [Fact]
    public void LocalHoursAndWeekdaysPreserveUnionTimeAcrossMidnightWithQuarterHourOffset()
    {
        var from = Parse("2026-03-02T17:45:00Z");
        Watch(from, from.AddHours(1));
        Watch(from, from.AddHours(1), new UserPlaybackItem { ItemId = Guid.NewGuid(), Name = "Episode", ItemType = "Episode", Genres = ["Drama"] });
        var report = Build(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"));
        var insights = report.Insights!;
        Assert.Equal(3600, report.Items.Sum(item => item.ActiveSeconds));
        Assert.Equal(1800, insights.HourlySeconds[23]);
        Assert.Equal(1800, insights.HourlySeconds[0]);
        Assert.Equal(1800, insights.WeekdaySeconds[0]);
        Assert.Equal(1800, insights.WeekdaySeconds[1]);
        Assert.Equal(3600, insights.HourlySeconds.Sum());
        Assert.Equal(3600, insights.WeekdaySeconds.Sum());
        Assert.Equal(2, insights.LongestStreakDays);
        Assert.Equal(new DateOnly(2026, 3, 2), insights.LongestStreakStart);
        Assert.Equal(new DateOnly(2026, 3, 3), insights.LongestStreakEnd);
        var genres = report.Months[2].Genres!;
        Assert.Equal(2700, genres.Single(genre => genre.Name == "Drama").ActiveSeconds);
        Assert.Equal(900, genres.Single(genre => genre.Name == "Adventure").ActiveSeconds);
        Assert.Equal(3600, genres.Sum(genre => genre.ActiveSeconds));
    }

    [Theory]
    [InlineData("2026-03-29T00:30:00Z", 1, 1800, 3, 1800)]
    [InlineData("2026-10-25T00:30:00Z", 2, 5400, 3, 1800)]
    public void DaylightSavingSkipsAndRepeatsHoursWithoutInventingTime(string instant, int firstHour, int firstSeconds, int lastHour, int lastSeconds)
    {
        var from = Parse(instant);
        Watch(from, from.AddSeconds(firstSeconds + lastSeconds));
        var report = Build(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        Assert.Equal(firstSeconds, report.Insights!.HourlySeconds[firstHour]);
        Assert.Equal(lastSeconds, report.Insights.HourlySeconds[lastHour]);
        Assert.Equal(firstSeconds + lastSeconds, report.Insights.WeekdaySeconds[6]);
        Assert.Equal(1, report.Insights.LongestStreakDays);
    }

    [Fact]
    public void LongestStreakCrossesNewYearAndUsesTheEarliestTie()
    {
        foreach (var date in new[] { "2025-12-30", "2025-12-31", "2026-01-01", "2026-03-01", "2026-03-02", "2026-03-03" })
        {
            var from = Parse(date + "T12:00:00Z");
            Watch(from, from.AddMinutes(10));
        }

        var start = Parse("2025-12-15T00:00:00Z");
        var report = YearlyRewindAggregator.Build(_user, _history, start, start.AddYears(1), start.AddYears(1), TimeZoneInfo.Utc);
        Assert.Equal(6, report.ActiveDays);
        Assert.Equal(3, report.Insights!.LongestStreakDays);
        Assert.Equal(new DateOnly(2025, 12, 30), report.Insights.LongestStreakStart);
        Assert.Equal(new DateOnly(2026, 1, 1), report.Insights.LongestStreakEnd);
    }

    [Fact]
    public void UnqualifiedAndFutureQualifiedSessionsDoNotEnterInsights()
    {
        var from = Parse("2026-03-02T10:00:00Z");
        Watch(from, from.AddHours(1));
        _history.Sessions[0].QualifiedAtUtc = null;
        Watch(from, from.AddHours(1));
        _history.Sessions[1].QualifiedAtUtc = Parse("2027-01-02T00:00:00Z");
        var report = Build();
        Assert.All(report.Insights!.HourlySeconds, seconds => Assert.Equal(0, seconds));
        Assert.All(report.Insights.WeekdaySeconds, seconds => Assert.Equal(0, seconds));
        Assert.Equal(0, report.Insights.LongestStreakDays);
        Assert.Null(report.Insights.LongestStreakStart);
    }

    [Fact]
    public void MonthlyGenresNormalizeDuplicatesAndIncludeUnknownMetadata()
    {
        var from = Parse("2026-04-01T10:00:00Z");
        Watch(from, from.AddHours(1), _movie with { Genres = [" Drama ", "Drama", "Adventure"] });
        Watch(from.AddHours(1), from.AddHours(2), _movie with { Genres = ["Changed later"] });
        Watch(from.AddHours(2), from.AddHours(3), _movie with { ItemId = Guid.NewGuid(), Genres = [] });
        var report = Build();
        var genres = report.Months[3].Genres!;
        Assert.Equal(3, genres.Count);
        Assert.All(genres, genre => Assert.Equal(3600, genre.ActiveSeconds));
        Assert.Contains(genres, genre => genre.Name is null);
        Assert.DoesNotContain(genres, genre => genre.Name == "Changed later");
        Assert.All(report.Months, month => Assert.Equal(month.ActiveSeconds, month.Genres!.Sum(genre => genre.ActiveSeconds)));
    }

    [Fact]
    public void ComparisonCopiesOnlyTheAdjacentReportsFactsAndNeverItsOlderComparison()
    {
        var from = Parse("2026-04-01T10:00:00Z");
        Watch(from, from.AddHours(1));
        var original = Build();
        var previous = original with { PeriodStartUtc = original.PeriodStartUtc.AddYears(-1), SnapshotAtUtc = original.PeriodStartUtc };
        var report = Build(previous: previous);
        var comparison = report.Comparison!;
        Assert.Equal(previous.PeriodStartUtc, comparison.PeriodStartUtc);
        Assert.Equal(previous.SnapshotAtUtc, comparison.SnapshotAtUtc);
        Assert.Equal(3600, comparison.ActiveSeconds);
        Assert.Equal(1, comparison.MovieCount);
        Assert.Equal(1, comparison.ActiveDays);
        Assert.Equal("Adventure", comparison.TopGenre);
        Assert.Equal(["Adventure", "Drama"], comparison.Genres);
        var again = Build(previous: previous with { Comparison = comparison });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(again, JsonDefaults.Options));
        var summary = json.RootElement.GetProperty("comparison");
        Assert.False(summary.TryGetProperty("items", out _));
        Assert.False(summary.TryGetProperty("comparison", out _));
    }

    [Fact]
    public void FirstYearSkippedYearsForeignUsersAndMissingCollectionHaveNoComparison()
    {
        var report = Build();
        Assert.Null(report.Comparison);
        var previous = report with { PeriodStartUtc = report.PeriodStartUtc.AddYears(-1), SnapshotAtUtc = report.PeriodStartUtc };
        Assert.Null(Build(previous: previous with { UserId = Guid.NewGuid() }).Comparison);
        Assert.Null(Build(previous: previous with { SnapshotAtUtc = report.PeriodStartUtc.AddYears(-1) }).Comparison);
        Assert.Null(Build(previous: previous with { DataState = "NotCollected" }).Comparison);
    }

    [Fact]
    public void NewFactsRoundtripAndInvalidTotalsOrStreaksAreRejected()
    {
        var from = Parse("2026-04-01T10:00:00Z");
        Watch(from, from.AddHours(1));
        var report = Build();
        var store = new YearlyRewindStore(_directory);
        Assert.True(store.Publish(report));
        Assert.Equal(3600, store.Load(_user)!.Insights!.HourlySeconds.Sum());
        Assert.Equal(2, store.Load(_user)!.Months[3].Genres!.Count);
        var newer = report with { SnapshotAtUtc = report.SnapshotAtUtc.AddYears(1), GeneratedAtUtc = report.GeneratedAtUtc.AddYears(1) };
        Assert.Throws<InvalidDataException>(() => store.Publish(newer with { Insights = report.Insights! with { HourlySeconds = new Collection<double>(new double[24]) } }));
        Assert.Throws<InvalidDataException>(() => store.Publish(newer with { Insights = report.Insights! with { LongestStreakDays = 2 } }));
        Assert.Equal(report.SnapshotId, store.Load(_user)!.SnapshotId);
    }
}
