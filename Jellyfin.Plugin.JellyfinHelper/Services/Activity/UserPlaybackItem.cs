using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Versioned playable-item facts referenced by shared history records.</summary>
internal sealed record UserPlaybackItem
{
    public required Guid ItemId { get; init; }

    public required string Name { get; init; }

    public required string ItemType { get; init; }

    public Guid? SeriesId { get; init; }

    public string? SeriesName { get; init; }

    public Collection<string> Genres { get; init; } = [];

    public long RuntimeTicks { get; init; }
}
