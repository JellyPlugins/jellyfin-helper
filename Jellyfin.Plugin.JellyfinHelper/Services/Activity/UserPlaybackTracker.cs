using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Observes actual forward playback without counting seeks, pauses or automated progress.</summary>
internal sealed class UserPlaybackTracker
{
    private const int MaxActiveSessions = 2048;
    private readonly Dictionary<(Guid User, string Session), Observation> _active = [];

    internal void Clear() => _active.Clear();

    internal void Observe(
        Guid userId,
        string sessionKey,
        UserPlaybackItem item,
        long? runtimeTicks,
        long? positionTicks,
        bool paused,
        bool automated,
        bool start,
        bool stop,
        DateTimeOffset now,
        UserActivityHistory history)
    {
        if (automated || positionTicks is null || positionTicks < 0 || userId == Guid.Empty)
        {
            return;
        }

        foreach (var key in _active.Where(pair => now - pair.Value.At > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToArray())
        {
            _active.Remove(key);
        }

        var identity = (userId, sessionKey);
        if (!_active.TryGetValue(identity, out var previous) || previous.Session.Item.ItemId != item.ItemId || start)
        {
            if (stop)
            {
                return;
            }

            if (_active.Count >= MaxActiveSessions)
            {
                throw new InvalidDataException("Shared playback session capacity exceeded.");
            }

            var threshold = runtimeTicks is > 0 and < 600000000 ? TimeSpan.FromTicks(runtimeTicks.Value).TotalSeconds / 2 : 60;
            var metadataKey = history.Remember(item);
            var session = new UserPlaybackSession { Item = history.Metadata[metadataKey], MetadataKey = metadataKey, ThresholdSeconds = threshold };
            history.Sessions.Add(session);
            _active[identity] = new Observation(session, now, positionTicks.Value, paused);
            return;
        }

        if (now <= previous.At)
        {
            return;
        }

        var elapsed = (now - previous.At).TotalSeconds;
        var advance = TimeSpan.FromTicks(positionTicks.Value - previous.Position).TotalSeconds;
        if (!previous.Paused && !paused && elapsed <= 90 && advance > 0 && advance <= (elapsed * 4) + 2)
        {
            var intervals = previous.Session.Intervals;
            if (intervals.Count > 0 && intervals[^1].EndUtc == previous.At)
            {
                intervals[^1] = intervals[^1] with { EndUtc = now };
            }
            else
            {
                intervals.Add(new UserPlaybackInterval(previous.At, now));
            }

            if (!previous.Session.QualifiedAtUtc.HasValue
                && intervals.Sum(interval => (interval.EndUtc - interval.StartUtc).TotalSeconds) >= previous.Session.ThresholdSeconds)
            {
                previous.Session.QualifiedAtUtc = now;
            }
        }

        if (stop)
        {
            _active.Remove(identity);
        }
        else
        {
            _active[identity] = new Observation(previous.Session, now, positionTicks.Value, paused);
        }
    }

    private sealed record Observation(UserPlaybackSession Session, DateTimeOffset At, long Position, bool Paused);
}
