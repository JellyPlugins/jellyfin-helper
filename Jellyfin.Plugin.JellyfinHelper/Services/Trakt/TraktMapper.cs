using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Maps Trakt list items onto the public <see cref="ExternalDiscoveryCandidate"/> shape the discovery
///     scorer consumes. Items without a TMDb id cannot be scored or requested, so they are dropped (the caller
///     logs the count). Genre slugs are intentionally not mapped to TMDb genre ids here: Seerr enrichment fills
///     genres and poster downstream, so GenreIds stays empty and the parental filter relies on the enriched data.
/// </summary>
internal static class TraktMapper
{
    /// <summary>
    ///     Maps a list of plain Trakt media items for a known media type, dropping any without a TMDb id.
    /// </summary>
    /// <param name="items">The Trakt media items.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="dropped">Receives the number of items dropped for a missing TMDb id.</param>
    /// <returns>The mapped candidates.</returns>
    internal static List<ExternalDiscoveryCandidate> MapMediaItems(
        IEnumerable<TraktMediaItem>? items,
        string mediaType,
        out int dropped)
    {
        dropped = 0;
        var result = new List<ExternalDiscoveryCandidate>();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            var candidate = MapOne(item, mediaType);
            if (candidate is null)
            {
                dropped++;
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    ///     Maps a list of trending entries, unwrapping the nested movie/show and dropping any without a TMDb id.
    /// </summary>
    /// <param name="items">The Trakt trending entries.</param>
    /// <param name="mediaType">"movie" or "tv" (selects which nested member to read).</param>
    /// <param name="dropped">Receives the number of items dropped for a missing TMDb id.</param>
    /// <returns>The mapped candidates.</returns>
    internal static List<ExternalDiscoveryCandidate> MapTrendingItems(
        IEnumerable<TraktTrendingItem>? items,
        string mediaType,
        out int dropped)
    {
        dropped = 0;
        var result = new List<ExternalDiscoveryCandidate>();
        if (items is null)
        {
            return result;
        }

        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        foreach (var entry in items)
        {
            var media = isTv ? entry.Show : entry.Movie;
            var candidate = MapOne(media, mediaType);
            if (candidate is null)
            {
                dropped++;
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static ExternalDiscoveryCandidate? MapOne(TraktMediaItem? item, string mediaType)
    {
        var tmdbId = item?.Ids?.Tmdb ?? 0;
        if (item is null || tmdbId <= 0)
        {
            return null;
        }

        return new ExternalDiscoveryCandidate
        {
            TmdbId = tmdbId,
            MediaType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie",
            Title = item.Title,
            Year = item.Year,
            Overview = item.Overview,

            // Trakt rating is 0-10 like TMDb's voteAverage, so it carries straight through as the rating signal.
            VoteAverage = item.Rating ?? 0,
            Popularity = 0,
            GenreIds = [],
            PosterPath = null,
            Adult = false,
        };
    }
}
