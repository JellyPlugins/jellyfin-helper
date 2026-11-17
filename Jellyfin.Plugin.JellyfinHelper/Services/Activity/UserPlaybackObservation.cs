using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>A Jellyfin state observation, not an invented playback session or exact watch duration.</summary>
internal sealed record UserPlaybackObservation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string MetadataKey { get; init; }

    [JsonIgnore]
    public UserPlaybackItem Item { get; set; } = null!;

    public DateTimeOffset ObservedAtUtc { get; init; }

    public DateTimeOffset SourceGeneratedAtUtc { get; init; }

    public DateTimeOffset? LastPlayedAtUtc { get; init; }

    public int PlayCount { get; init; }

    // Null means a baseline, a counter reset or an out-of-order source snapshot.
    public int? PlayCountDelta { get; init; }

    public long PlaybackPositionTicks { get; init; }

    public bool Played { get; init; }
}
