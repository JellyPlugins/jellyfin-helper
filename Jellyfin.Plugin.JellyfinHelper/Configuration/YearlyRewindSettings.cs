using System;
using System.Globalization;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>Shared validation for annual rewind dates, independent of the current calendar year.</summary>
internal static class YearlyRewindSettings
{
    internal const string DefaultStartDate = "12-15";
    internal const string DefaultEndDate = "01-15";

    internal static bool IsValidDate(string? value)
        => value is { Length: 5 }
            && DateOnly.TryParseExact("2001-" + value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    internal static DateOnly InYear(string value, int year)
    {
        if (!IsValidDate(value))
        {
            throw new ArgumentException("Expected an annual MM-dd date excluding February 29.", nameof(value));
        }

        var date = DateOnly.ParseExact("2001-" + value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateOnly(year, date.Month, date.Day);
    }
}
