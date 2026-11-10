using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Indexes first dated playback memberships once; arbitrary cutoffs never replay other users' histories.</summary>
internal sealed class TrainingHistoryIndex
{
    private readonly ImmutableArray<Guid> _userIds;
    private readonly Dictionary<Guid, MembershipTimeline> _users = new();
    private readonly Dictionary<Guid, DateTime[]> _popularity;
    private readonly MembershipTimeline _popularKeys;
    private readonly DateTime[] _membershipDates;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<(Guid Target, Guid Neighbour), TrainingNeighbourTimeline> _pairs = new();
    private Guid? _pairTarget;

    /// <summary>Initializes a new instance of the <see cref="TrainingHistoryIndex"/> class for one immutable input batch.</summary>
    /// <param name="profiles">The current batch's source profiles.</param>
    /// <param name="cancellationToken">The build cancellation token.</param>
    internal TrainingHistoryIndex(Collection<UserWatchProfile> profiles, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _userIds = profiles.Select(profile => profile.UserId).ToImmutableArray();
        var populations = new Dictionary<Guid, List<DateTime>>();
        foreach (var profile in profiles)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var firstDates = new Dictionary<Guid, DateTime>();
            foreach (var row in profile.WatchedItems.Where(row => row.LastPlayedDate.HasValue && row.HasPlaybackActivity()))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var date = DateTimeNormalization.ToUtc(row.LastPlayedDate!.Value);
                AddFirstDate(firstDates, row.ItemId, date);
                if (row.SeriesId is { } seriesId)
                {
                    AddFirstDate(firstDates, seriesId, date);
                }
            }

            _users.Add(profile.UserId, new MembershipTimeline(firstDates));
            foreach (var (key, date) in firstDates)
            {
                if (!populations.TryGetValue(key, out var dates))
                {
                    dates = [];
                    populations.Add(key, dates);
                }

                dates.Add(date);
            }
        }

        _popularity = populations.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray());
        _popularKeys = new MembershipTimeline(_popularity.ToDictionary(pair => pair.Key, pair => pair.Value[0]));
        _membershipDates = _popularity.Values.SelectMany(dates => dates).Order().ToArray();
    }

    /// <summary>Gets the bounded-cache accounting cost of the current membership snapshot, including empty users.</summary>
    internal long SnapshotRowCost { get; private set; }

    /// <summary>Returns immutable sets at the exact exclusive cutoff; earlier returned contexts never change.</summary>
    /// <param name="cutoffUtc">The normalized prediction-time cutoff.</param>
    /// <returns>The structurally shared collaborative snapshot.</returns>
    internal CollaborativeFilter.HistoricalContext Before(DateTime cutoffUtc)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var cutoff = DateTimeNormalization.ToUtc(cutoffUtc);
        var sets = _userIds.ToImmutableDictionary(id => id, id => (IReadOnlySet<Guid>)new DatedSet(_users[id], cutoff, this, id));
        SnapshotRowCost = CountBefore(_membershipDates, cutoff) + (long)_userIds.Length;
        return new CollaborativeFilter.HistoricalContext(_userIds, sets, new PopularitySnapshot(_popularity, _popularKeys.Before(cutoff), cutoff));
    }

    private static int CountBefore(DateTime[] dates, DateTime cutoff)
    {
        var lower = 0;
        var upper = dates.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (dates[middle] < cutoff)
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

    private static void AddFirstDate(Dictionary<Guid, DateTime> dates, Guid key, DateTime date)
    {
        if (!dates.TryGetValue(key, out var previous) || date < previous)
        {
            dates[key] = date;
        }
    }

    private TrainingNeighbourTimeline GetPair(Guid target, Guid neighbour)
    {
        // One target's pair timelines cost at most twice the batch's indexed memberships.
        // Retaining only that neighbourhood bounds memory without thrashing a fixed-size pair cache.
        if (_pairTarget != target)
        {
            _pairs.Clear();
            _pairTarget = target;
        }

        var key = (target, neighbour);
        if (_pairs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var pair = new TrainingNeighbourTimeline(_users[target].FirstDates, _users[neighbour].FirstDates, _cancellationToken);
        _pairs.Add(key, pair);
        return pair;
    }

    private sealed class MembershipTimeline
    {
        internal MembershipTimeline(Dictionary<Guid, DateTime> dates)
        {
            FirstDates = dates;
            var ordered = dates.OrderBy(pair => pair.Value).ToArray();
            Keys = ordered.Select(pair => pair.Key).ToArray();
            Dates = ordered.Select(pair => pair.Value).ToArray();
        }

        internal Dictionary<Guid, DateTime> FirstDates { get; }

        internal Guid[] Keys { get; }

        internal DateTime[] Dates { get; }

        internal DatedSet Before(DateTime cutoff) => new(this, cutoff);
    }

    // These immutable views share only input-sized indexes, not an unbounded collection of cutoff snapshots.
    private sealed class DatedSet(MembershipTimeline timeline, DateTime cutoff, TrainingHistoryIndex? owner = null, Guid userId = default) : CollaborativeFilter.IHistoricalWatchSet
    {
        private TrainingHistoryIndex? Owner { get; } = owner;

        private Guid UserId { get; } = userId;

        private DateTime Cutoff { get; } = cutoff;

        public int Count { get; } = CountBefore(timeline.Dates, cutoff);

        public bool Contains(Guid item) => timeline.FirstDates.TryGetValue(item, out var date) && date < Cutoff;

        public IEnumerator<Guid> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return timeline.Keys[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<Guid> other) => this.ToHashSet().IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<Guid> other) => this.ToHashSet().IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<Guid> other) => this.ToHashSet().IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<Guid> other) => this.ToHashSet().IsSupersetOf(other);

        public bool Overlaps(IEnumerable<Guid> other) => other.Any(Contains);

        public bool SetEquals(IEnumerable<Guid> other) => this.ToHashSet().SetEquals(other);

        public bool TryCountOverlap(IReadOnlySet<Guid> other, out int count)
        {
            count = 0;
            if (Owner is null || other is not DatedSet indexed || indexed.Owner != Owner || indexed.Cutoff != Cutoff)
            {
                return false;
            }

            count = Owner.GetPair(UserId, indexed.UserId).CountOverlap(Cutoff);
            return true;
        }

        public bool TryGetDifference(IReadOnlySet<Guid> other, out IEnumerable<Guid> difference)
        {
            difference = [];
            if (Owner is null || other is not DatedSet indexed || indexed.Owner != Owner || indexed.Cutoff != Cutoff)
            {
                return false;
            }

            difference = Owner.GetPair(indexed.UserId, UserId).Difference(Cutoff);
            return true;
        }
    }

    private sealed class PopularitySnapshot(Dictionary<Guid, DateTime[]> populations, DatedSet keys, DateTime cutoff) : IReadOnlyDictionary<Guid, int>
    {
        public int Count => keys.Count;

        public IEnumerable<Guid> Keys => keys;

        public IEnumerable<int> Values => keys.Select(key => this[key]);

        public int this[Guid key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(Guid key) => keys.Contains(key);

        public bool TryGetValue(Guid key, out int value)
        {
            value = populations.TryGetValue(key, out var dates) ? CountBefore(dates, cutoff) : 0;
            return value > 0;
        }

        public IEnumerator<KeyValuePair<Guid, int>> GetEnumerator() => keys.Select(key => new KeyValuePair<Guid, int>(key, this[key])).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
