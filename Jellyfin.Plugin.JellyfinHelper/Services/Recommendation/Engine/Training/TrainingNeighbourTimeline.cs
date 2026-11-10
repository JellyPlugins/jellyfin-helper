using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Indexes one neighbour pair's overlap dates and neighbour-only membership intervals once.</summary>
internal sealed class TrainingNeighbourTimeline
{
    private readonly DateTime[] _overlapDates;
    private readonly Window[] _windows;
    private readonly DateTime[] _maximumEnds;

    internal TrainingNeighbourTimeline(IReadOnlyDictionary<Guid, DateTime> target, IReadOnlyDictionary<Guid, DateTime> neighbour, CancellationToken cancellationToken)
    {
        var overlaps = new List<DateTime>();
        var windows = new List<Window>();
        foreach (var (key, start) in neighbour)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.TryGetValue(key, out var end))
            {
                windows.Add(new Window(key, start, DateTime.MaxValue));
                continue;
            }

            overlaps.Add(start > end ? start : end);
            if (start < end)
            {
                windows.Add(new Window(key, start, end));
            }
        }

        _overlapDates = overlaps.Order().ToArray();
        _windows = windows.OrderBy(window => window.Start).ToArray();
        _maximumEnds = new DateTime[_windows.Length * 4];
        if (_windows.Length > 0)
        {
            BuildMaximumEnds(0, 0, _windows.Length);
        }
    }

    /// <summary>Gets the input-sized cost used by the bounded pair cache.</summary>
    internal int RowCost => _overlapDates.Length + _windows.Length;

    internal int CountOverlap(DateTime cutoff)
    {
        var lower = 0;
        var upper = _overlapDates.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (_overlapDates[middle] < cutoff)
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

    internal IEnumerable<Guid> Difference(DateTime cutoff)
        => _windows.Length == 0 ? [] : EnumerateActive(0, 0, _windows.Length, cutoff);

    private DateTime BuildMaximumEnds(int node, int lower, int upper)
    {
        if (upper - lower == 1)
        {
            return _maximumEnds[node] = _windows[lower].End;
        }

        var middle = lower + ((upper - lower) / 2);
        var left = BuildMaximumEnds((node * 2) + 1, lower, middle);
        var right = BuildMaximumEnds((node * 2) + 2, middle, upper);
        return _maximumEnds[node] = left > right ? left : right;
    }

    private IEnumerable<Guid> EnumerateActive(int node, int lower, int upper, DateTime cutoff)
    {
        // Neighbour activity is strictly before the cutoff; a target activity exactly at it is still excluded.
        if (_windows[lower].Start >= cutoff || _maximumEnds[node] < cutoff)
        {
            yield break;
        }

        if (upper - lower == 1)
        {
            yield return _windows[lower].Key;
            yield break;
        }

        var middle = lower + ((upper - lower) / 2);
        foreach (var key in EnumerateActive((node * 2) + 1, lower, middle, cutoff))
        {
            yield return key;
        }

        foreach (var key in EnumerateActive((node * 2) + 2, middle, upper, cutoff))
        {
            yield return key;
        }
    }

    private sealed record Window(Guid Key, DateTime Start, DateTime End);
}
