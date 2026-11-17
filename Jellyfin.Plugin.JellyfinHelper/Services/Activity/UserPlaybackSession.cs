using System;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

internal sealed class UserPlaybackSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonIgnore]
    public UserPlaybackItem Item { get; set; } = null!;

    public string MetadataKey { get; init; } = string.Empty;

    public double ThresholdSeconds { get; init; } = 60;

    public DateTimeOffset? QualifiedAtUtc { get; set; }

    public Collection<UserPlaybackInterval> Intervals { get; init; } = [];
}
