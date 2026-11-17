using System.Globalization;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.YearlyRewind;

public sealed class YearlyRewindWindowTests
{
    [Theory]
    [InlineData("12-15", true)]
    [InlineData("01-15", true)]
    [InlineData("02-28", true)]
    [InlineData("02-29", false)]
    [InlineData("04-31", false)]
    [InlineData("2-01", false)]
    [InlineData("12-15 ", false)]
    [InlineData("2026-12-15", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void DateValidation_RejectsAmbiguousOrImpossibleAnnualDates(string? date, bool valid)
    {
        Assert.Equal(valid, YearlyRewindSettings.IsValidDate(date));
    }

    [Theory]
    [InlineData("2026-12-14T23:59:59Z", false, 2025)]
    [InlineData("2026-12-15T00:00:00Z", true, 2026)]
    [InlineData("2026-12-31T23:59:59Z", true, 2026)]
    [InlineData("2027-01-01T00:00:00Z", true, 2026)]
    [InlineData("2027-01-15T23:59:59Z", true, 2026)]
    [InlineData("2027-01-16T00:00:00Z", false, 2026)]
    public void DefaultWindow_ChangesVisibilityWithoutChangingSnapshotAtNewYear(string instant, bool visible, int year)
    {
        var now = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
        Assert.Equal(visible, YearlyRewindWindow.IsTabVisible(now, "12-15", "01-15", TimeZoneInfo.Utc));
        var cutoff = YearlyRewindWindow.LatestSnapshotAt(now, "12-15", TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(year, 12, 15, 0, 0, 0, TimeSpan.Zero), cutoff);
        Assert.Equal(cutoff.AddYears(-1), YearlyRewindWindow.PeriodStart(cutoff, "12-15", TimeZoneInfo.Utc));
        Assert.Equal(cutoff.AddYears(1), YearlyRewindWindow.NextSnapshotAt(now, "12-15", TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("2026-11-01T00:00:00Z", "11-01", "11-30", true)]
    [InlineData("2026-12-01T00:00:00Z", "11-01", "11-30", false)]
    [InlineData("2027-01-01T00:00:00Z", "01-01", "01-31", true)]
    [InlineData("2026-06-01T23:59:59Z", "06-01", "06-01", true)]
    [InlineData("2026-06-02T00:00:00Z", "06-01", "06-01", false)]
    public void CustomWindow_SupportsSameYearAndSingleDay(string instant, string start, string end, bool visible)
    {
        Assert.Equal(visible, YearlyRewindWindow.IsTabVisible(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), start, end, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Period_UsesCalendarAnniversaryRatherThan365Days()
    {
        var cutoff = DateTimeOffset.Parse("2024-12-15T00:00:00Z", CultureInfo.InvariantCulture);
        var start = YearlyRewindWindow.PeriodStart(cutoff, "12-15", TimeZoneInfo.Utc);
        Assert.Equal(366, (cutoff - start).Days);
    }

    [Fact]
    public void ClientTimeZone_MapsWindowsIdentifiersForBrowserDateFormatting()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Assert.Equal("Europe/Berlin", YearlyRewindWindow.ClientTimeZoneId(zone));
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.FindSystemTimeZoneById(YearlyRewindWindow.ClientTimeZoneId(TimeZoneInfo.Utc)).BaseUtcOffset);
    }

    [Fact]
    public void Midnight_UsesServerTimeZoneRatherThanUtcDate()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var now = DateTimeOffset.Parse("2026-12-14T23:00:00Z", CultureInfo.InvariantCulture);
        Assert.True(YearlyRewindWindow.IsTabVisible(now, "12-15", "01-15", zone));
        Assert.Equal(now, YearlyRewindWindow.LatestSnapshotAt(now, "12-15", zone));
        Assert.False(YearlyRewindWindow.IsTabVisible(now.AddTicks(-1), "12-15", "01-15", zone));
    }

    [Fact]
    public void Midnight_WhenDaylightSavingSkipsMidnight_UsesFirstValidInstant()
    {
        var transition = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 10, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "midnight-transition", TimeSpan.Zero, "test", "standard", "summer", [transition]);
        var cutoff = YearlyRewindWindow.LatestSnapshotAt(DateTimeOffset.Parse("2026-03-01T02:00:00Z", CultureInfo.InvariantCulture), "03-01", zone);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z", CultureInfo.InvariantCulture), cutoff);
    }
}
