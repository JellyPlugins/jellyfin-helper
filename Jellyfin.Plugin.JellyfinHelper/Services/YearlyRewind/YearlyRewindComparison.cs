using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Only the preceding report's comparison facts, without its items or older comparisons.</summary>
internal sealed record YearlyRewindComparison
{
    public required DateTimeOffset PeriodStartUtc { get; init; }

    public required DateTimeOffset SnapshotAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required string DataState { get; init; }

    public double ActiveSeconds { get; init; }

    public double EstimatedSeconds { get; init; }

    public int ReconstructedItemCount { get; init; }

    public int MovieCount { get; init; }

    public int EpisodeCount { get; init; }

    public int SeriesCount { get; init; }

    public int ActiveDays { get; init; }

    public Collection<string> Genres { get; init; } = [];

    public string? TopGenre { get; init; }
}
