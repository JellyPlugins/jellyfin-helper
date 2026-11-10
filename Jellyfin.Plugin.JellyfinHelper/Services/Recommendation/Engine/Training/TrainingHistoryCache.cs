using System;
using System.Collections.Generic;
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

    private readonly Collection<UserWatchProfile> _source;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<DateTime, Snapshot> _snapshots = new();
    private readonly Queue<DateTime> _insertionOrder = new();
    private long _cachedRows;

    /// <summary>Initializes a new instance of the <see cref="TrainingHistoryCache"/> class for one build only.</summary>
    /// <param name="source">The profiles collected for this training build.</param>
    /// <param name="cancellationToken">The build cancellation token.</param>
    internal TrainingHistoryCache(Collection<UserWatchProfile> source, CancellationToken cancellationToken)
    {
        _source = source;
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
        var profile = snapshot.ByUserId[userId];
        if (!profile.WatchedItems.Any(row => excludedIds.Contains(row.ItemId)))
        {
            return new Inputs(profile, snapshot.Profiles, snapshot.Collaborative);
        }

        // Series targets can have earlier episodes. Their exclusions must not poison a cached
        // cutoff, the same user's next target, or another user's collaborative evidence.
        var filtered = TrainingHistory.Before(profile, cutoff, excludedIds);
        var profiles = snapshot.Profiles.ToArray();
        profiles[snapshot.Profiles.IndexOf(profile)] = filtered;
        var userSets = new Dictionary<Guid, HashSet<Guid>>(snapshot.Collaborative.UserSets)
        {
            [userId] = CollaborativeFilter.PrecomputeUserWatchSets([filtered])[userId]
        };
        return new Inputs(filtered, new Collection<UserWatchProfile>(profiles), CollaborativeFilter.PrecomputeCollaborativeContext(userSets));
    }

    private Snapshot GetSnapshot(DateTime cutoff)
    {
        if (_snapshots.TryGetValue(cutoff, out var cached))
        {
            return cached;
        }

        var profiles = new Collection<UserWatchProfile>();
        foreach (var source in _source)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            profiles.Add(TrainingHistory.Before(source, cutoff));
        }

        var snapshot = new Snapshot(
            profiles,
            profiles.ToDictionary(profile => profile.UserId),
            CollaborativeFilter.PrecomputeCollaborativeContext(CollaborativeFilter.PrecomputeUserWatchSets(profiles)),
            profiles.Sum(profile => (long)profile.WatchedItems.Count + 1));
        _cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.RowCount > MaxCachedRows)
        {
            return snapshot;
        }

        while (_snapshots.Count >= MaxCachedCutoffs || _cachedRows + snapshot.RowCount > MaxCachedRows)
        {
            var oldest = _insertionOrder.Dequeue();
            _cachedRows -= _snapshots[oldest].RowCount;
            _snapshots.Remove(oldest);
        }

        _snapshots.Add(cutoff, snapshot);
        _insertionOrder.Enqueue(cutoff);
        _cachedRows += snapshot.RowCount;
        return snapshot;
    }

    /// <summary>A historical target and the complete corresponding neighbour context.</summary>
    /// <param name="Profile">The target's historical profile.</param>
    /// <param name="Profiles">All profiles at the same cutoff, with any target exclusion applied.</param>
    /// <param name="Collaborative">The watch sets and popularity derived from those inputs.</param>
    internal sealed record Inputs(UserWatchProfile Profile, Collection<UserWatchProfile> Profiles, CollaborativeFilter.CollaborativeContext Collaborative);

    private sealed record Snapshot(Collection<UserWatchProfile> Profiles, Dictionary<Guid, UserWatchProfile> ByUserId, CollaborativeFilter.CollaborativeContext Collaborative, long RowCount);
}
