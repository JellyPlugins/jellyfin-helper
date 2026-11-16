using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>Title-balanced personal retrieval and a bounded detail frontier independent of broad genre dominance.</summary>
internal static class PersonalDiscoveryRetrieval
{
    /// <summary>Chooses one positively evidenced provider title per media type, without counting every episode as a vote.</summary>
    /// <param name="profile">Only the current user's own history.</param>
    /// <returns>At most one movie and one TV seed; missing provider or thematic evidence gives no seed.</returns>
    internal static IReadOnlyDictionary<string, int> Seeds(UserWatchProfile profile)
    {
        var content = ContentFitProfile.Create(profile);
        var titles = profile.WatchedItems.Where(row => row.HasMeaningfulInteraction())
            .Select(row => (Row: row, Type: MediaType(row), Id: row.SeriesId.HasValue ? row.SeriesTmdbId : row.TmdbId))
            .Where(item => item.Type is not null && item.Id > 0)
            .GroupBy(item => (Type: item.Type!, item.Id))
            .Where(group => group.Any(item => item.Row.Played || item.Row.IsFavorite)
                && group.Where(item => item.Row.UserRating is > 0 && double.IsFinite(item.Row.UserRating.Value))
                    .Select(item => item.Row.UserRating!.Value).DefaultIfEmpty(10).Average() > 5)
            .Select(group => new
            {
                group.Key,
                Favorite = group.Any(item => item.Row.IsFavorite),
                LastPlay = group.Max(item => item.Row.LastPlayedDate),
                Thematic = group.Max(item => content.ScoreTags(item.Row.SeriesId.HasValue
                    ? item.Row.SeriesTags ?? item.Row.Tags
                    : item.Row.OwnTags ?? item.Row.Tags))
            })
            .Where(title => title.Thematic > 0)
            .OrderByDescending(title => title.Favorite).ThenByDescending(title => title.LastPlay).ThenBy(title => title.Key.Id);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            result.TryAdd(title.Key.Type, title.Key.Id);
        }

        return result;
    }

    private static string? MediaType(WatchedItemInfo row)
    {
        if ((row.SeriesId is { } series && series != Guid.Empty) || string.Equals(row.ItemType, "Series", StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        return string.Equals(row.ItemType, "Movie", StringComparison.OrdinalIgnoreCase) ? "movie" : null;
    }

    /// <summary>Reserves half the request slots for high pre-scores and shares the rest across seed/type/genre lanes.</summary>
    /// <param name="ordered">Already age-eligible own-source candidates in descending pre-score order, never Trakt candidates.</param>
    /// <param name="budget">Existing maximum detail-request count.</param>
    /// <returns>Unattempted candidates only; cached and failed requests do not waste optional slots.</returns>
    internal static List<TmdbDiscoverItem> DetailFrontier(IEnumerable<TmdbDiscoverItem> ordered, int budget)
    {
        var pending = ordered.Where(item => !item.MetadataFetchAttempted).ToList();
        var result = pending.Take((budget + 1) / 2).ToList();
        var headCount = result.Count;
        var lanes = pending.Skip(result.Count).GroupBy(item => item.PersonalSeedKey is { } seed
                ? "seed:" + seed
                : item.MediaType + ":genre:" + item.GenreIds.Order().FirstOrDefault())
            .OrderBy(group => group.Key.StartsWith("seed:", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<TmdbDiscoverItem>(group)).ToArray();
        FillLanes(result, lanes, budget);
        if (lanes.Length <= 1 && !result.Any(item => item.PersonalSeedKey is not null))
        {
            return result;
        }

        // Dispatch fair lanes early too: three slow top-score requests must not starve a personal lane for the entire deadline.
        var scheduled = new List<TmdbDiscoverItem>(result.Count);
        for (var index = 0; index < headCount; index++)
        {
            scheduled.Add(result[index]);
            if (headCount + index < result.Count)
            {
                scheduled.Add(result[headCount + index]);
            }
        }

        return scheduled;
    }

    private static void FillLanes(List<TmdbDiscoverItem> result, Queue<TmdbDiscoverItem>[] lanes, int budget)
    {
        while (result.Count < budget)
        {
            var added = false;
            foreach (var lane in lanes)
            {
                if (result.Count == budget)
                {
                    break;
                }

                if (lane.TryDequeue(out var candidate))
                {
                    result.Add(candidate);
                    added = true;
                }
            }

            if (!added)
            {
                break;
            }
        }
    }
}
