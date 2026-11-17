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
        var hourly = new double[24];
        var weekdays = new double[7];
        var monthlyGenres = Enumerable.Range(0, 12).Select(_ => new Dictionary<string, double>(StringComparer.Ordinal)).ToArray();
        var intervals = sessions.SelectMany(session => session.Intervals
            .Where(interval => interval.StartUtc < cutoff && interval.EndUtc > start)
            .Select(interval => new Span(
                session,
                interval.StartUtc < start ? start : interval.StartUtc,
                interval.EndUtc > cutoff ? cutoff : interval.EndUtc))).ToArray();
        var points = intervals.SelectMany(interval => new[] { interval.Start, interval.End }).Distinct().Order().ToArray();
        var seconds = new Dictionary<Guid, double>();
        var days = new HashSet<DateOnly>();
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

            AddLocalTime(points[index], points[index + 1], zone, hourly, weekdays);
            var duration = (points[index + 1] - points[index]).TotalSeconds;
            foreach (var session in active.Values)
            {
                seconds[session.Item.ItemId] = seconds.GetValueOrDefault(session.Item.ItemId) + (duration / active.Count);
            }

            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(points[index], zone).DateTime);
            var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(points[index + 1].AddTicks(-1), zone).DateTime);
            for (; date <= lastDate; date = date.AddDays(1))
            {
                days.Add(date);
            }

            for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
            {
                var month = months[monthIndex];
                var from = month.StartUtc > points[index] ? month.StartUtc : points[index];
                var to = month.EndUtc < points[index + 1] ? month.EndUtc : points[index + 1];
                if (to > from)
                {
                    var allocated = (to - from).TotalSeconds;
                    month.ActiveSeconds += allocated;
                    foreach (var session in active.Values)
                    {
                        var genres = itemGenres[session.Item.ItemId];
                        foreach (var genre in genres)
                        {
                            monthlyGenres[monthIndex][genre] = monthlyGenres[monthIndex].GetValueOrDefault(genre) + (allocated / active.Count / genres.Length);
                        }
                    }
                }
            }
        }

        var reconstructed = Reconstruct(history, start, cutoff, generated);
        foreach (var row in reconstructed)
        {
            var id = row.Item.ItemId;
            if (!metadata.ContainsKey(id))
            {
                metadata[id] = row.Item;
                itemGenres[id] = GenreNames(row.Item);
            }

            var ticks = row.Played && row.Item.RuntimeTicks > 0
                ? row.Item.RuntimeTicks
                : Math.Min(row.PlaybackPositionTicks, row.Item.RuntimeTicks > 0 ? row.Item.RuntimeTicks : row.PlaybackPositionTicks);
            var duration = TimeSpan.FromTicks(ticks).TotalSeconds;
            seconds[id] = seconds.GetValueOrDefault(id) + duration;
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
                        monthlyGenres[monthIndex][genre] = monthlyGenres[monthIndex].GetValueOrDefault(genre) + (duration / itemGenres[id].Length);
                    }
                }
            }
        }

        for (var monthIndex = 0; monthIndex < months.Length; monthIndex++)
        {
            foreach (var genre in monthlyGenres[monthIndex].OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                months[monthIndex].Genres!.Add(new YearlyRewindGenre { Name = genre.Key.Length == 0 ? null : genre.Key, ActiveSeconds = genre.Value });
            }
        }

        var plays = itemGroups.ToDictionary(group => group.Key, group => group.Count(session => session.Intervals.Any(interval => interval.EndUtc > start && interval.StartUtc < cutoff)));
        var items = metadata.Where(pair => seconds.ContainsKey(pair.Key)).Select(pair => new YearlyRewindItem
        {
            ItemId = pair.Key,
            Name = pair.Value.Name,
            ItemType = pair.Value.ItemType,
            SeriesId = pair.Value.SeriesId,
            SeriesName = pair.Value.SeriesName,
            Genres = pair.Value.Genres,
            ActiveSeconds = seconds[pair.Key],
            EstimatedSeconds = estimated.GetValueOrDefault(pair.Key),
            HasReconstructedActivity = estimated.ContainsKey(pair.Key),
            PlayCount = plays.GetValueOrDefault(pair.Key) + (estimated.ContainsKey(pair.Key) ? 1 : 0)
        }).OrderByDescending(item => item.ActiveSeconds).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.ItemId).ToList();
        var coverage = history.Coverage.OrderBy(period => period.StartUtc).ToArray();
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
        if (coverage.All(period => period.StartUtc >= cutoff) && reconstructed.Length == 0)
        {
            state = "NotCollected";
        }
        else if (coveredUntil < cutoff || reconstructed.Length > 0)
        {
            state = "Partial";
        }
        else if (items.Count == 0)
        {
            state = "Empty";
        }

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
            ActiveDays = days.Count,
            Insights = BuildInsights(days, hourly, weekdays),
            Comparison = comparison,
            FirstItemId = intervals.OrderBy(span => span.Start).Select(span => (Guid?)span.Session.Item.ItemId).FirstOrDefault(),
            CollectionStartedAtUtc = coverage.Select(period => (DateTimeOffset?)period.StartUtc).FirstOrDefault()
        };
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

    private sealed record Span(UserPlaybackSession Session, DateTimeOffset Start, DateTimeOffset End);
}
