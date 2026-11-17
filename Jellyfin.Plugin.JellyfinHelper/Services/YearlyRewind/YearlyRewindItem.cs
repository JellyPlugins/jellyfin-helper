using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Frozen item metadata and usage; translated sentences are rendered by the client.</summary>
internal sealed record YearlyRewindItem
{
    public required Guid ItemId { get; init; }

    public required string Name { get; init; }

    public required string ItemType { get; init; }

    public Guid? SeriesId { get; init; }

    public string? SeriesName { get; init; }

    public Collection<string> Genres { get; init; } = [];

    public double ActiveSeconds { get; init; }

    public double EstimatedSeconds { get; init; }

    public bool HasReconstructedActivity { get; init; }

    public int PlayCount { get; init; }
}
