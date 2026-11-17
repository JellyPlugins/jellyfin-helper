using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Frozen local-time distributions and the earliest longest run of active calendar days.</summary>
internal sealed record YearlyRewindInsights
{
    public Collection<double> HourlySeconds { get; init; } = [];

    /// <summary>Gets active seconds from Monday (index zero) through Sunday.</summary>
    public Collection<double> WeekdaySeconds { get; init; } = [];

    public int LongestStreakDays { get; init; }

    public DateOnly? LongestStreakStart { get; init; }

    public DateOnly? LongestStreakEnd { get; init; }
}
