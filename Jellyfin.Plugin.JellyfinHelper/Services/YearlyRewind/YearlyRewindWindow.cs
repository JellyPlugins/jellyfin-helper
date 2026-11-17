using System;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Evaluates annual snapshot boundaries separately from seasonal tab visibility.</summary>
internal static class YearlyRewindWindow
{
    internal static DateTimeOffset LatestSnapshotAt(DateTimeOffset now, string startDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var localYear = TimeZoneInfo.ConvertTime(now, timeZone).Year;
        var candidate = AtMidnight(YearlyRewindSettings.InYear(startDate, localYear), timeZone);
        return candidate <= now
            ? candidate
            : AtMidnight(YearlyRewindSettings.InYear(startDate, localYear - 1), timeZone);
    }

    internal static DateTimeOffset NextSnapshotAt(DateTimeOffset now, string startDate, TimeZoneInfo timeZone)
    {
        var latest = LatestSnapshotAt(now, startDate, timeZone);
        var year = TimeZoneInfo.ConvertTime(latest, timeZone).Year;
        return AtMidnight(YearlyRewindSettings.InYear(startDate, year + 1), timeZone);
    }

    internal static DateTimeOffset PeriodStart(DateTimeOffset snapshotAt, string startDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var year = TimeZoneInfo.ConvertTime(snapshotAt, timeZone).Year;
        return AtMidnight(YearlyRewindSettings.InYear(startDate, year - 1), timeZone);
    }

    internal static bool IsTabVisible(DateTimeOffset now, string startDate, string endDate, TimeZoneInfo timeZone)
    {
        var start = LatestSnapshotAt(now, startDate, timeZone);
        var year = TimeZoneInfo.ConvertTime(start, timeZone).Year;
        var firstDay = YearlyRewindSettings.InYear(startDate, year);
        var lastDay = YearlyRewindSettings.InYear(endDate, year);
        if (lastDay < firstDay)
        {
            lastDay = lastDay.AddYears(1);
        }

        return now >= start && now < AtMidnight(lastDay.AddDays(1), timeZone);
    }

    internal static DateTimeOffset NextVisibilityChange(DateTimeOffset now, string startDate, string endDate, TimeZoneInfo timeZone)
    {
        if (!IsTabVisible(now, startDate, endDate, timeZone))
        {
            return NextSnapshotAt(now, startDate, timeZone);
        }

        var start = LatestSnapshotAt(now, startDate, timeZone);
        var year = TimeZoneInfo.ConvertTime(start, timeZone).Year;
        var end = YearlyRewindSettings.InYear(endDate, year);
        if (end < YearlyRewindSettings.InYear(startDate, year))
        {
            end = end.AddYears(1);
        }

        return AtMidnight(end.AddDays(1), timeZone);
    }

    internal static string ClientTimeZoneId(TimeZoneInfo timeZone)
        => TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId) ? ianaId : timeZone.Id;

    private static DateTimeOffset AtMidnight(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones skip midnight. Use the first valid local instant on that date.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(local);
            var earlierOffset = offsets[0] > offsets[1] ? offsets[0] : offsets[1];
            return new DateTimeOffset(local, earlierOffset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}
