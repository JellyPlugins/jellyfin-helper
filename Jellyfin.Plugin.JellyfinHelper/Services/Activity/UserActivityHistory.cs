using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Shared per-user observations, measured intervals and versioned metadata, independent of consumers.</summary>
internal sealed class UserActivityHistory
{
    private Dictionary<Guid, List<UserPlaybackObservation>>? _byItem;

    public int SchemaVersion { get; init; } = 2;

    public Dictionary<string, UserPlaybackItem> Metadata { get; init; } = new(StringComparer.Ordinal);

    public Collection<UserActivityCoverage> Coverage { get; init; } = [];

    public Collection<UserPlaybackSession> Sessions { get; init; } = [];

    public Collection<UserPlaybackObservation> Observations { get; init; } = [];

    internal string Remember(UserPlaybackItem item)
    {
        item = item with
        {
            SeriesId = item.SeriesId == Guid.Empty ? null : item.SeriesId,
            Genres = new Collection<string>(item.Genres.Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonDefaults.Options))));
        Metadata.TryAdd(key, item);
        return key;
    }

    internal bool Record(UserPlaybackItem item, UserItemActivity state, DateTimeOffset observed, DateTimeOffset source)
    {
        var key = Remember(item);
        var playedAt = state.LastPlayedDate.HasValue ? new DateTimeOffset(state.LastPlayedDate.Value).ToUniversalTime() : (DateTimeOffset?)null;
        if (playedAt > observed)
        {
            playedAt = null;
        }

        var count = Math.Max(0, state.PlayCount);
        var position = Math.Max(0, state.PlaybackPositionTicks);
        _byItem ??= Observations.GroupBy(row => row.Item.ItemId).ToDictionary(group => group.Key, group => group.ToList());
        if (!_byItem.TryGetValue(item.ItemId, out var records))
        {
            records = [];
            _byItem[item.ItemId] = records;
        }

        var previous = records.OrderBy(row => row.SourceGeneratedAtUtc).ThenBy(row => row.ObservedAtUtc).LastOrDefault();

        bool Same(UserPlaybackObservation row) => row.MetadataKey == key && row.LastPlayedAtUtc == playedAt
            && row.PlayCount == count && row.PlaybackPositionTicks == position && row.Played == state.Played;
        if (previous is not null && (Same(previous)
            || (source <= previous.SourceGeneratedAtUtc && records.Any(Same))))
        {
            return false;
        }

        int? delta = null;
        if (previous is not null && source >= previous.SourceGeneratedAtUtc && count >= previous.PlayCount)
        {
            delta = count - previous.PlayCount;
        }

        var observation = new UserPlaybackObservation
        {
            MetadataKey = key,
            Item = Metadata[key],
            ObservedAtUtc = observed,
            SourceGeneratedAtUtc = source,
            LastPlayedAtUtc = playedAt,
            PlayCount = count,
            PlaybackPositionTicks = position,
            Played = state.Played,
            PlayCountDelta = delta
        };
        Observations.Add(observation);
        records.Add(observation);
        return true;
    }
}
