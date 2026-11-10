using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Build-scoped, bounded reuse of historical neighbour profiles and collaborative inputs.</summary>
internal sealed class TrainingHistoryCache
{
    internal const int MaxCachedCutoffs = 32;
    internal const int MaxCachedRows = 32768;

    private readonly Dictionary<Guid, UserWatchProfile> _source;
    private readonly TrainingHistoryIndex _index;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<DateTime, Snapshot> _snapshots = new();
    private readonly Queue<DateTime> _insertionOrder = new();
    private long _cachedRows;

    /// <summary>Initializes a new instance of the <see cref="TrainingHistoryCache"/> class for one build only.</summary>
    /// <param name="source">The profiles collected for this training build.</param>
    /// <param name="cancellationToken">The build cancellation token.</param>
    internal TrainingHistoryCache(Collection<UserWatchProfile> source, CancellationToken cancellationToken)
    {
        _source = source.ToDictionary(profile => profile.UserId, profile => TrainingHistory.Before(profile, DateTime.MaxValue));
        _index = new TrainingHistoryIndex(source, cancellationToken);
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the number of retained cutoffs.</summary>
    internal int CachedCutoffCount => _snapshots.Count;

    /// <summary>Gets the retained profile/row count; empty profiles also consume the bounded budget.</summary>
    internal long CachedRowCount => _cachedRows;

    /// <summary>Gets historical inputs without ever modifying shared neighbour snapshots for a target exclusion.</summary>
    /// <param name="userId">The target user.</param>
    /// <param name="beforeUtc">The strictly exclusive prediction-time cutoff.</param>
    /// <param name="excludedIds">Target and episode ids to remove from this user's inputs only.</param>
    /// <returns>Coherent historical profiles and their collaborative context.</returns>
    internal Inputs Get(Guid userId, DateTime beforeUtc, IReadOnlySet<Guid> excludedIds)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var cutoff = DateTimeNormalization.ToUtc(beforeUtc);
        var snapshot = GetSnapshot(cutoff);
        var profile = GetProfile(snapshot, userId, cutoff);
        if (excludedIds.Count == 0 || !profile.WatchedItems.Any(row => excludedIds.Contains(row.ItemId)))
        {
            return new Inputs(profile, snapshot.Collaborative);
        }

        // Series targets can have earlier episodes. Their exclusions must not poison a cached
        // cutoff, the same user's next target, or another user's collaborative evidence.
        var filtered = TrainingHistory.BeforePlaybackSnapshot(profile, cutoff, excludedIds);
        var replacement = filtered.WatchedItems.SelectMany(row => row.SeriesId is { } seriesId
                ? new[] { row.ItemId, seriesId }
                : new[] { row.ItemId }).ToImmutableHashSet();
        var removed = snapshot.Collaborative.UserSets[userId].Except(replacement).ToHashSet();
        var popularity = new AdjustedPopularity(snapshot.Collaborative.ItemPopularity, removed);
        var context = snapshot.Collaborative with { UserSets = snapshot.Collaborative.UserSets.SetItem(userId, replacement), ItemPopularity = popularity };
        return new Inputs(filtered, context);
    }

    private UserWatchProfile GetProfile(Snapshot snapshot, Guid userId, DateTime cutoff)
    {
        if (snapshot.Profiles.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        // Only the target needs rich preference metadata. Neighbours use immutable indexed sets.
        var profile = TrainingHistory.BeforePlaybackSnapshot(_source[userId], cutoff);
        if (snapshot.Retained && _cachedRows + profile.WatchedItems.Count <= MaxCachedRows)
        {
            snapshot.Profiles.Add(userId, profile);
            snapshot.RowCount += profile.WatchedItems.Count;
            _cachedRows += profile.WatchedItems.Count;
        }

        return profile;
    }

    private Snapshot GetSnapshot(DateTime cutoff)
    {
        if (_snapshots.TryGetValue(cutoff, out var cached))
        {
            return cached;
        }

        var snapshot = new Snapshot(_index.Before(cutoff), _index.SnapshotRowCost);
        _cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.RowCount > MaxCachedRows)
        {
            return snapshot;
        }

        while (_snapshots.Count >= MaxCachedCutoffs || _cachedRows + snapshot.RowCount > MaxCachedRows)
        {
            var oldest = _insertionOrder.Dequeue();
            _cachedRows -= _snapshots[oldest].RowCount;
            _snapshots[oldest].Retained = false;
            _snapshots.Remove(oldest);
        }

        _snapshots.Add(cutoff, snapshot);
        _insertionOrder.Enqueue(cutoff);
        _cachedRows += snapshot.RowCount;
        snapshot.Retained = true;
        return snapshot;
    }

    /// <summary>A historical target and the complete corresponding neighbour context.</summary>
    /// <param name="Profile">The target's historical profile.</param>
    /// <param name="Collaborative">The watch sets and popularity derived from those inputs.</param>
    internal sealed record Inputs(UserWatchProfile Profile, CollaborativeFilter.HistoricalContext Collaborative);

    /// <summary>Subtracts only the excluded target user's memberships without copying the entire population map.</summary>
    private sealed class AdjustedPopularity(IReadOnlyDictionary<Guid, int> source, IReadOnlySet<Guid> removed) : IReadOnlyDictionary<Guid, int>
    {
        public int Count { get; } = source.Count - removed.Count(key => source[key] == 1);

        public IEnumerable<Guid> Keys => this.Select(pair => pair.Key);

        public IEnumerable<int> Values => this.Select(pair => pair.Value);

        public int this[Guid key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(Guid key) => TryGetValue(key, out _);

        public bool TryGetValue(Guid key, out int value)
        {
            if (!source.TryGetValue(key, out value))
            {
                return false;
            }

            if (removed.Contains(key))
            {
                value--;
            }

            return value > 0;
        }

        public IEnumerator<KeyValuePair<Guid, int>> GetEnumerator()
        {
            foreach (var pair in source)
            {
                var value = pair.Value - (removed.Contains(pair.Key) ? 1 : 0);
                if (value > 0)
                {
                    yield return new KeyValuePair<Guid, int>(pair.Key, value);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Snapshot(CollaborativeFilter.HistoricalContext collaborative, long rowCount)
    {
        internal CollaborativeFilter.HistoricalContext Collaborative { get; } = collaborative;

        internal Dictionary<Guid, UserWatchProfile> Profiles { get; } = new();

        internal long RowCount { get; set; } = rowCount;

        internal bool Retained { get; set; }
    }
}
