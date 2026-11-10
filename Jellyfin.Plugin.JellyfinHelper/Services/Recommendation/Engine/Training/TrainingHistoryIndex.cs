using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Advances or rewinds dated neighbour activity instead of rescanning every user for each cutoff.</summary>
internal sealed class TrainingHistoryIndex
{
    private readonly ImmutableArray<Guid> _userIds;
    private readonly Activity[] _activities;
    private readonly CancellationToken _cancellationToken;
    private ImmutableDictionary<Guid, ImmutableDictionary<Guid, int>> _references = ImmutableDictionary<Guid, ImmutableDictionary<Guid, int>>.Empty;
    private ImmutableDictionary<Guid, ImmutableHashSet<Guid>> _sets;
    private ImmutableDictionary<Guid, int> _popularity = ImmutableDictionary<Guid, int>.Empty;
    private int _cursor;
    private long _memberships;

    /// <summary>Initializes a new instance of the <see cref="TrainingHistoryIndex"/> class for one immutable input batch.</summary>
    /// <param name="profiles">The current batch's source profiles.</param>
    /// <param name="cancellationToken">The build cancellation token.</param>
    internal TrainingHistoryIndex(Collection<UserWatchProfile> profiles, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _userIds = profiles.Select(profile => profile.UserId).ToImmutableArray();
        _sets = _userIds.ToImmutableDictionary(id => id, _ => ImmutableHashSet<Guid>.Empty);
        var activities = new List<Activity>();
        foreach (var profile in profiles)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in profile.WatchedItems.Where(row => row.LastPlayedDate.HasValue && row.HasMeaningfulInteraction()))
            {
                activities.Add(new Activity(DateTimeNormalization.ToUtc(row.LastPlayedDate!.Value), profile.UserId, row.ItemId, row.SeriesId));
            }
        }

        _activities = activities.OrderBy(activity => activity.AtUtc).ToArray();
    }

    /// <summary>Gets the bounded-cache accounting cost of the current membership snapshot, including empty users.</summary>
    internal long SnapshotRowCost => _memberships + _userIds.Length;

    /// <summary>Returns immutable sets at the exact exclusive cutoff; earlier returned contexts never change.</summary>
    /// <param name="cutoffUtc">The normalized prediction-time cutoff.</param>
    /// <returns>The structurally shared collaborative snapshot.</returns>
    internal CollaborativeFilter.HistoricalContext Before(DateTime cutoffUtc)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var end = FindExclusiveEnd(cutoffUtc);
        while (_cursor < end)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Apply(_activities[_cursor++], 1);
        }

        while (_cursor > end)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Apply(_activities[--_cursor], -1);
        }

        return new CollaborativeFilter.HistoricalContext(_userIds, _sets, _popularity);
    }

    private int FindExclusiveEnd(DateTime cutoff)
    {
        var lower = 0;
        var upper = _activities.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (_activities[middle].AtUtc < cutoff)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower;
    }

    private void Apply(Activity activity, int change)
    {
        ChangeReference(activity.UserId, activity.ItemId, change);
        if (activity.SeriesId is { } seriesId && seriesId != activity.ItemId)
        {
            ChangeReference(activity.UserId, seriesId, change);
        }
    }

    private void ChangeReference(Guid userId, Guid key, int change)
    {
        var references = _references.GetValueOrDefault(userId, ImmutableDictionary<Guid, int>.Empty);
        var previous = references.GetValueOrDefault(key);
        var next = previous + change;
        references = next == 0 ? references.Remove(key) : references.SetItem(key, next);
        _references = _references.SetItem(userId, references);
        if (previous == 0)
        {
            _sets = _sets.SetItem(userId, _sets[userId].Add(key));
            _popularity = _popularity.SetItem(key, _popularity.GetValueOrDefault(key) + 1);
            _memberships++;
        }
        else if (next == 0)
        {
            _sets = _sets.SetItem(userId, _sets[userId].Remove(key));
            var users = _popularity[key] - 1;
            _popularity = users == 0 ? _popularity.Remove(key) : _popularity.SetItem(key, users);
            _memberships--;
        }
    }

    private sealed record Activity(DateTime AtUtc, Guid UserId, Guid ItemId, Guid? SeriesId);
}
