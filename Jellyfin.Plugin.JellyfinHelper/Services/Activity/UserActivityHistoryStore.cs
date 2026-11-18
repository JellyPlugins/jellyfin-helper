using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>Stores the common per-user journal and migrates the previous rewind-specific raw history once.</summary>
internal sealed class UserActivityHistoryStore
{
    private readonly string _dataPath;

    internal UserActivityHistoryStore(string dataPath) => _dataPath = dataPath;

    internal UserActivityHistory Load(Guid userId)
    {
        var path = GetPath(userId);
        if (!File.Exists(path))
        {
            return Migrate(userId);
        }

        using var stream = File.OpenRead(path);
        var history = JsonSerializer.Deserialize<UserActivityHistory>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException("Activity history is null.");
        ResolveAndValidate(history);
        return history;
    }

    internal void Save(Guid userId, UserActivityHistory history)
    {
        ResolveAndValidate(history);
        AtomicFile.WriteAllText(GetPath(userId), JsonSerializer.Serialize(history, JsonDefaults.Options));
    }

    internal void DeleteUser(Guid userId)
    {
        if (File.Exists(GetPath(userId)))
        {
            File.Delete(GetPath(userId));
        }

        if (File.Exists(LegacyPath(userId)))
        {
            File.Delete(LegacyPath(userId));
        }
    }

    internal Guid[] KnownUsers() => new[] { Path.Join(_dataPath, "activity-history"), Path.Join(_dataPath, "yearly-rewind", "history") }
        .Where(Directory.Exists).SelectMany(directory => Directory.GetFiles(directory, "*.json"))
        .Select(Path.GetFileNameWithoutExtension).Select(name => Guid.TryParseExact(name, "N", out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty).Distinct().ToArray();

    private UserActivityHistory Migrate(Guid userId)
    {
        var path = LegacyPath(userId);
        if (!File.Exists(path))
        {
            return new UserActivityHistory();
        }

        using var stream = File.OpenRead(path);
        var legacy = JsonSerializer.Deserialize<LegacyHistory>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException("Legacy playback history is null.");
        if (legacy.SchemaVersion != 1 || legacy.Coverage is null || legacy.Sessions is null)
        {
            throw new InvalidDataException("Invalid legacy playback history.");
        }

        var result = new UserActivityHistory { Coverage = legacy.Coverage };
        foreach (var old in legacy.Sessions)
        {
            if (old is null || old.Item is null || old.Intervals is null)
            {
                throw new InvalidDataException("Invalid legacy playback session.");
            }

            var key = result.Remember(old.Item);
            result.Sessions.Add(new UserPlaybackSession
            {
                Id = old.Id,
                MetadataKey = key,
                Item = result.Metadata[key],
                ThresholdSeconds = old.ThresholdSeconds,
                QualifiedAtUtc = old.QualifiedAtUtc,
                Intervals = old.Intervals
            });
        }

        Save(userId, result);
        // Leave the old file as a read-only rollback source; all subsequent reads and writes use the new journal.
        return result;
    }

    private static void ResolveAndValidate(UserActivityHistory history)
    {
        if (history.SchemaVersion != 2 || history.Metadata is null || history.Sessions is null || history.Coverage is null || history.Observations is null
            || history.Metadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.ItemId == Guid.Empty
                || pair.Value.Name is null || string.IsNullOrWhiteSpace(pair.Value.ItemType) || pair.Value.Genres is null || pair.Value.RuntimeTicks < 0)
            || history.Coverage.Any(period => period is null || period.StartUtc.Offset != TimeSpan.Zero || period.EndUtc.Offset != TimeSpan.Zero || period.EndUtc < period.StartUtc))
        {
            throw new InvalidDataException("Invalid shared activity history.");
        }

        ResolveSessions(history);
        ResolveObservations(history);
    }

    private static void ResolveSessions(UserActivityHistory history)
    {
        foreach (var session in history.Sessions)
        {
            if (session is null || session.Id == Guid.Empty || session.MetadataKey is null || !history.Metadata.TryGetValue(session.MetadataKey, out var item)
                || !double.IsFinite(session.ThresholdSeconds) || session.ThresholdSeconds < 0 || session.Intervals is null
                || (session.QualifiedAtUtc.HasValue && session.QualifiedAtUtc.Value.Offset != TimeSpan.Zero)
                || session.Intervals.Any(span => span is null || span.StartUtc.Offset != TimeSpan.Zero || span.EndUtc.Offset != TimeSpan.Zero || span.EndUtc <= span.StartUtc))
            {
                throw new InvalidDataException("Invalid measured playback session.");
            }

            session.Item = item;
        }
    }

    private static void ResolveObservations(UserActivityHistory history)
    {
        foreach (var observation in history.Observations)
        {
            if (observation is null || observation.Id == Guid.Empty || observation.MetadataKey is null || !history.Metadata.TryGetValue(observation.MetadataKey, out var item)
                || observation.ObservedAtUtc.Offset != TimeSpan.Zero || observation.SourceGeneratedAtUtc.Offset != TimeSpan.Zero
                || observation.SourceGeneratedAtUtc > observation.ObservedAtUtc || observation.PlayCount < 0 || observation.PlayCountDelta < 0 || observation.PlaybackPositionTicks < 0
                || (observation.LastPlayedAtUtc.HasValue && (observation.LastPlayedAtUtc.Value.Offset != TimeSpan.Zero || observation.LastPlayedAtUtc > observation.ObservedAtUtc)))
            {
                throw new InvalidDataException("Invalid Jellyfin state observation.");
            }

            observation.Item = item;
        }
    }

    private string GetPath(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        return Path.Join(_dataPath, "activity-history", userId.ToString("N") + ".json");
    }

    private string LegacyPath(Guid userId) => Path.Join(_dataPath, "yearly-rewind", "history", userId.ToString("N") + ".json");

    private sealed class LegacyHistory
    {
        [JsonInclude]
        public int SchemaVersion
        {
            get; init;
        }

        public Collection<UserActivityCoverage> Coverage { get; init; } = [];

        public Collection<LegacySession> Sessions { get; init; } = [];
    }

    private sealed class LegacySession
    {
        [JsonInclude]
        public Guid Id
        {
            get; init;
        }

        public UserPlaybackItem Item { get; init; } = null!;

        public double ThresholdSeconds { get; init; } = 60;

        [JsonInclude]
        public DateTimeOffset? QualifiedAtUtc
        {
            get; init;
        }

        public Collection<UserPlaybackInterval> Intervals { get; init; } = [];
    }
}
