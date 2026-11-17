using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Language-independent rewind data replaced only by a later annual snapshot.</summary>
internal sealed record YearlyRewindSnapshot
{
    public int SchemaVersion { get; init; } = 1;

    public required Guid UserId { get; init; }

    public Guid SnapshotId { get; init; } = Guid.NewGuid();

    public required DateTimeOffset PeriodStartUtc { get; init; }

    public required DateTimeOffset SnapshotAtUtc { get; init; }

    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required string DataState { get; init; }

    public int ActiveDays { get; init; }

    public int ReconstructedItemCount => Items.Count(item => item.HasReconstructedActivity);

    // Optional additions keep already-frozen schema-1 reports readable without inventing facts.
    public YearlyRewindInsights? Insights { get; init; }

    public YearlyRewindComparison? Comparison { get; init; }

    public Guid? FirstItemId { get; init; }

    public DateTimeOffset? CollectionStartedAtUtc { get; init; }

    public Collection<YearlyRewindMonth> Months { get; init; } = [];

    public Collection<YearlyRewindItem> Items { get; init; } = [];
}
