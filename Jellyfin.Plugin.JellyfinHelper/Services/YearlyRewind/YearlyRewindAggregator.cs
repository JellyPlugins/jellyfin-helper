using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Builds one language-independent snapshot from only observations confirmed by its cutoff.</summary>
internal static class YearlyRewindAggregator
{
    internal static YearlyRewindSnapshot Build(
        Guid userId,
        UserActivityHistory history,
        DateTimeOffset start,
        DateTimeOffset cutoff,
        DateTimeOffset generated,
        TimeZoneInfo zone,
        YearlyRewindSnapshot? previous = null)
    {
        var sessions = history.Sessions.Where(session => session.QualifiedAtUtc <= cutoff && (session.Item.ItemType is "Movie" or "Episode")).ToArray();
        var itemGroups = sessions.GroupBy(session => session.Item.ItemId).ToArray();
        var itemGenres = itemGroups.ToDictionary(group => group.Key, group => GenreNames(group.First().Item));
        var metadata = itemGroups.ToDictionary(group => group.Key, group => group.First().Item);
        var estimated = new Dictionary<Guid, double>();
        var months = CreateMonths(start, cutoff, zone);
        var totals = Measure(sessions, start, cutoff, zone, itemGenres, months);
        var reconstructed = Reconstruct(history, start, cutoff, generated);
        AddReconstructed(reconstructed, metadata, itemGenres, estimated, totals, months);
        CompleteMonthlyGenres(months, totals);
        var seconds = totals.Seconds;
        var plays = itemGroups.ToDictionary(group => group.Key, group => group.Count(session => session.Intervals.Any(interval => interval.EndUtc > start && interval.StartUtc < cutoff)));
        var items = metadata.Where(pair => seconds.ContainsKey(pair.Key)).Select(pair => new YearlyRewindItem
        {
            ItemId = pair.Key,
            Name = pair.Value.Name,
            ItemType = pair.Value.ItemType,
            SeriesId = pair.Value.SeriesId == Guid.Empty ? null : pair.Value.SeriesId,
            SeriesName = pair.Value.SeriesName,
            Genres = pair.Value.Genres,
            ActiveSeconds = seconds[pair.Key],
            EstimatedSeconds = estimated.GetValueOrDefault(pair.Key),
            HasReconstructedActivity = estimated.ContainsKey(pair.Key),
            PlayCount = plays.GetValueOrDefault(pair.Key) + (estimated.ContainsKey(pair.Key) ? 1 : 0)
        }).OrderByDescending(item => item.ActiveSeconds).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.ItemId).ToList();
        var coverage = history.Coverage.OrderBy(period => period.StartUtc).ToArray();
        var state = DescribeCollection(coverage, start, cutoff, reconstructed.Length, items.Count > 0);
        YearlyRewindComparison? comparison = null;
        if (previous is not null && previous.UserId == userId && previous.SnapshotAtUtc == start
            && previous.DataState is "Ready" or "Empty" or "Partial")
        {
            comparison = Summarize(previous);
        }

        return new YearlyRewindSnapshot
        {
            UserId = userId,
            PeriodStartUtc = start,
            SnapshotAtUtc = cutoff,
            GeneratedAtUtc = generated,
            TimeZoneId = YearlyRewindWindow.ClientTimeZoneId(zone),
            DataState = state,
            Items = new Collection<YearlyRewindItem>(items),
            Months = new Collection<YearlyRewindMonth>(months),
            ActiveDays = totals.Days.Count,
            Insights = BuildInsights(totals.Days, totals.Hourly, totals.Weekdays),
            Comparison = comparison,
            FirstItemId = totals.Intervals.OrderBy(span => span.Start).Select(span => (Guid?)span.Session.Item.ItemId).FirstOrDefault(),
            CollectionStartedAtUtc = coverage.Select(period => (DateTimeOffset?)period.StartUtc).FirstOrDefault()
        };
    }

    private static YearlyRewindMonth[] CreateMonths(DateTimeOffset start, DateTimeOffset cutoff, TimeZoneInfo zone)
    {
        var months = Enumerable.Range(0, 12).Select(index => new YearlyRewindMonth
        {
            StartUtc = start,
            EndUtc = cutoff
        }).ToArray();
        var localStart = TimeZoneInfo.ConvertTime(start, zone).Date;
        for (var index = 0; index < 12; index++)
        {
            months[index] = new YearlyRewindMonth
            {
                StartUtc = index == 0 ? start : LocalToUtc(localStart.AddMonths(index), zone),
                EndUtc = index == 11 ? cutoff : LocalToUtc(localStart.AddMonths(index + 1), zone),
                Genres = []
            };
        }

        return months;
    }

    private static MeasuredPlayback Measure(UserPlaybackSession[] sessions, DateTimeOffset start, DateTimeOffset cutoff, TimeZoneInfo zone, Dictionary<Guid, string[]> itemGenres, YearlyRewindMonth[] months)
    {
        var intervals = sessions.SelectMany(session => session.Intervals
            .Where(interval => interval.StartUtc < cutoff && interval.EndUtc > start)
            .Select(interval => new Span(
                session,
                interval.StartUtc < start ? start : interval.StartUtc,
                interval.EndUtc > cutoff ? cutoff : interval.EndUtc))).ToArray();
        var totals = new MeasuredPlayback { Intervals = intervals };
        var points = intervals.SelectMany(interval => new[] { interval.Start, interval.End }).Distinct().Order().ToArray();
        // Sweep start/end boundaries. Divide concurrent sessions so totals equal the union of active time.
        var events = intervals.SelectMany(span => new[] { (At: span.Start, Session: span.Session, Delta: 1), (At: span.End, Session: span.Session, Delta: -1) })
            .GroupBy(entry => entry.At).ToDictionary(group => group.Key, group => group.ToArray());
        var active = new Dictionary<Guid, UserPlaybackSession>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            foreach (var entry in events[points[index]])
            {
                if (entry.Delta < 0)
                {
                    active.Remove(entry.Session.Id);
                }
                else
                {
                    active[entry.Session.Id] = entry.Session;
                }
            }

            if (active.Count == 0)
            {
                continue;
            }

            AddLocalTime(points[index], points[index + 1], zone, totals.Hourly, totals.Weekdays);
            AllocateSegment(points[index], points[index + 1], active, totals, itemGenres, months, zone);
        }

        return totals;
    }

    private static void AllocateSegment(DateTimeOffset from, DateTimeOffset to, Dictionary<Guid, UserPlaybackSession> active, MeasuredPlayback totals, Dictionary<Guid, string[]> itemGenres, YearlyRewindMonth[] months, TimeZoneInfo zone)
    {
        var duration = (to - from).TotalSeconds;
        foreach (var item in active.Values.Select(session => session.Item))
        {
            totals.Seconds[item.ItemId] = totals.Seconds.GetValueOrDefault(item.ItemId) + (duration / active.Count);
        }

        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1), zone).DateTime);
        for (; date <= lastDate; date = date.AddDays(1))
        {
            totals.Days.Add(date);
        }

        AllocateMonths(from, to, active, totals, itemGenres, months);
    }

    private static void AllocateMonths(DateTimeOffset from, DateTimeOffset to, Dictionary<Guid, UserPlaybackSession> active, MeasuredPlayback totals, Dictionary<Guid, string[]> itemGenres, YearlyRewindMonth[] months)
    {
        for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
        {
            var month = months[monthIndex];
            var overlapStart = month.StartUtc > from ? month.StartUtc : from;
            var overlapEnd = month.EndUtc < to ? month.EndUtc : to;
            if (overlapEnd > overlapStart)
            {
                var allocated = (overlapEnd - overlapStart).TotalSeconds;
                month.ActiveSeconds += allocated;
                foreach (var genres in active.Values.Select(session => itemGenres[session.Item.ItemId]))
                {
                    foreach (var genre in genres)
                    {
                        totals.MonthlyGenres[monthIndex][genre] = totals.MonthlyGenres[monthIndex].GetValueOrDefault(genre) + (allocated / active.Count / genres.Length);
                    }
                }
            }
        }
    }

    private static void AddReconstructed(UserPlaybackObservation[] reconstructed, Dictionary<Guid, UserPlaybackItem> metadata, Dictionary<Guid, string[]> itemGenres, Dictionary<Guid, double> estimated, MeasuredPlayback totals, YearlyRewindMonth[] months)
    {
        foreach (var row in reconstructed)
        {
            var id = row.Item.ItemId;
            if (!metadata.ContainsKey(id))
            {
                metadata[id] = row.Item;
                itemGenres[id] = GenreNames(row.Item);
            }

            var duration = EstimatedDuration(row);
            totals.Seconds[id] = totals.Seconds.GetValueOrDefault(id) + duration;
            estimated[id] = duration;
            for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
            {
                var month = months[monthIndex];
                if (row.LastPlayedAtUtc >= month.StartUtc && row.LastPlayedAtUtc < month.EndUtc)
                {
                    month.ActiveSeconds += duration;
                    month.EstimatedSeconds += duration;
                    foreach (var genre in itemGenres[id])
                    {
                        totals.MonthlyGenres[monthIndex][genre] = totals.MonthlyGenres[monthIndex].GetValueOrDefault(genre) + (duration / itemGenres[id].Length);
                    }
                }
            }
        }
    }

    private static double EstimatedDuration(UserPlaybackObservation row)
    {
        if (row.Played && row.Item.RuntimeTicks > 0)
        {
            return TimeSpan.FromTicks(row.Item.RuntimeTicks).TotalSeconds;
        }

        var maximum = row.Item.RuntimeTicks > 0 ? row.Item.RuntimeTicks : row.PlaybackPositionTicks;
        return TimeSpan.FromTicks(Math.Min(row.PlaybackPositionTicks, maximum)).TotalSeconds;
    }

    private static void CompleteMonthlyGenres(YearlyRewindMonth[] months, MeasuredPlayback totals)
    {
        for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
        {
            foreach (var genre in totals.MonthlyGenres[monthIndex].OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                months[monthIndex].Genres!.Add(new YearlyRewindGenre { Name = genre.Key.Length == 0 ? null : genre.Key, ActiveSeconds = genre.Value });
            }
        }
    }

    private static string DescribeCollection(UserActivityCoverage[] coverage, DateTimeOffset start, DateTimeOffset cutoff, int reconstructedCount, bool hasItems)
    {
        var coveredUntil = start;
        foreach (var period in coverage)
        {
            if (period.EndUtc <= coveredUntil)
            {
                continue;
            }

            if (period.StartUtc > coveredUntil)
            {
                break;
            }

            coveredUntil = period.EndUtc;
        }

        var state = "Ready";
        if (coverage.All(period => period.StartUtc >= cutoff) && reconstructedCount == 0)
        {
            state = "NotCollected";
        }
        else if (coveredUntil < cutoff || reconstructedCount > 0)
        {
            state = "Partial";
        }
        else if (!hasItems)
        {
            state = "Empty";
        }

        return state;
    }

    private static UserPlaybackObservation[] Reconstruct(UserActivityHistory history, DateTimeOffset start, DateTimeOffset cutoff, DateTimeOffset generated)
    {
        // A lifetime counter is never assigned wholesale to a year. Retain at most one conservative
        // baseline per title, outside measured coverage, without inventing dates or playback intervals.
        return history.Observations.Where(row => row.ObservedAtUtc <= generated && row.LastPlayedAtUtc >= start && row.LastPlayedAtUtc < cutoff
                && (row.Item.ItemType is "Movie" or "Episode") && (row.Played || row.PlayCount > 0 || row.PlaybackPositionTicks > 0)
                && !history.Coverage.Any(period => row.LastPlayedAtUtc >= period.StartUtc && row.LastPlayedAtUtc < period.EndUtc))
            .GroupBy(row => row.Item.ItemId)
            .Select(group => group.OrderByDescending(row => row.LastPlayedAtUtc).ThenByDescending(row => row.SourceGeneratedAtUtc).First())
            .ToArray();
    }

    private static string[] GenreNames(UserPlaybackItem item)
    {
        var genres = item.Genres.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return genres.Length == 0 ? [string.Empty] : genres;
    }

    private static YearlyRewindComparison Summarize(YearlyRewindSnapshot previous)
    {
        var genres = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in previous.Items)
        {
            var names = item.Genres.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length == 0)
            {
                names = [string.Empty];
            }

            foreach (var name in names.Where(name => name.Length > 0))
            {
                genres[name] = genres.GetValueOrDefault(name) + (item.ActiveSeconds / names.Length);
            }
        }

        return new YearlyRewindComparison
        {
            PeriodStartUtc = previous.PeriodStartUtc,
            SnapshotAtUtc = previous.SnapshotAtUtc,
            TimeZoneId = previous.TimeZoneId,
            DataState = previous.DataState,
            ActiveSeconds = previous.Items.Sum(item => item.ActiveSeconds),
            EstimatedSeconds = previous.Items.Sum(item => item.EstimatedSeconds),
            ReconstructedItemCount = previous.ReconstructedItemCount,
            MovieCount = previous.Items.Count(item => item.ItemType == "Movie"),
            EpisodeCount = previous.Items.Count(item => item.ItemType == "Episode"),
            SeriesCount = previous.Items.Where(item => item.SeriesId.HasValue).Select(item => item.SeriesId).Distinct().Count(),
            ActiveDays = previous.ActiveDays,
            Genres = new Collection<string>(genres.Keys.Order(StringComparer.Ordinal).ToArray()),
            TopGenre = genres.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).FirstOrDefault()
        };
    }

    private static void AddLocalTime(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, double[] hourly, double[] weekdays)
    {
        // UTC minute boundaries preserve local hours, half/quarter-hour offsets and DST transitions.
        for (var cursor = from; cursor < to;)
        {
            var next = new DateTimeOffset(((cursor.UtcTicks / TimeSpan.TicksPerMinute) + 1) * TimeSpan.TicksPerMinute, TimeSpan.Zero);
            if (next > to)
            {
                next = to;
            }

            var local = TimeZoneInfo.ConvertTime(cursor, zone);
            var seconds = (next - cursor).TotalSeconds;
            hourly[local.Hour] += seconds;
            weekdays[((int)local.DayOfWeek + 6) % 7] += seconds;
            cursor = next;
        }
    }

    private static YearlyRewindInsights BuildInsights(HashSet<DateOnly> days, double[] hourly, double[] weekdays)
    {
        DateOnly? previous = null, bestStart = null, bestEnd = null;
        var length = 0;
        var best = 0;
        foreach (var day in days.Order())
        {
            length = previous.HasValue && previous.Value.AddDays(1) == day ? length + 1 : 1;
            if (length > best)
            {
                best = length;
                bestStart = day.AddDays(1 - length);
                bestEnd = day;
            }

            previous = day;
        }

        return new YearlyRewindInsights
        {
            HourlySeconds = new Collection<double>(hourly),
            WeekdaySeconds = new Collection<double>(weekdays),
            LongestStreakDays = best,
            LongestStreakStart = bestStart,
            LongestStreakEnd = bestEnd
        };
    }

    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, offsets.Max()).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    private sealed class MeasuredPlayback
    {
        public Span[] Intervals { get; init; } = [];

        public Dictionary<Guid, double> Seconds { get; } = [];

        public HashSet<DateOnly> Days { get; } = [];

        public double[] Hourly { get; } = new double[24];

        public double[] Weekdays { get; } = new double[7];

        public Dictionary<string, double>[] MonthlyGenres { get; } = Enumerable.Range(0, 12).Select(_ => new Dictionary<string, double>(StringComparer.Ordinal)).ToArray();
    }

    private sealed record Span(UserPlaybackSession Session, DateTimeOffset Start, DateTimeOffset End);
}
