using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <param name="rankOffset">Base rank for the first item, so a second list (e.g. shows after movies) continues the ordering.</param>
    /// <returns>The mapped candidates.</returns>
    internal static List<ExternalDiscoveryCandidate> MapMediaItems(
        IEnumerable<TraktMediaItem>? items,
        string mediaType,
        out int dropped,
        int rankOffset = 0)
    {
        dropped = 0;
        if (items is null)
        {
            return [];
        }

        // The incoming list order is Trakt's own ranking; carry it as SourceRank so a rank-based pipeline
        // can preserve it. The index spans dropped items too, so a drop never shifts a kept item's rank.
        var mapped = items.Select((item, i) => MapOne(item, mediaType, rankOffset + i)).ToList();
        dropped = mapped.Count(m => m is null);
        return [.. mapped.OfType<ExternalDiscoveryCandidate>()];
    }

    /// <summary>
    ///     Maps a list of trending entries, unwrapping the nested movie/show and dropping any without a TMDb id.
    /// </summary>
    /// <param name="items">The Trakt trending entries.</param>
    /// <param name="mediaType">"movie" or "tv" (selects which nested member to read).</param>
    /// <param name="dropped">Receives the number of items dropped for a missing TMDb id.</param>
    /// <param name="rankOffset">Base rank for the first item, so a second list (e.g. shows after movies) continues the ordering.</param>
    /// <returns>The mapped candidates.</returns>
    internal static List<ExternalDiscoveryCandidate> MapTrendingItems(
        IEnumerable<TraktTrendingItem>? items,
        string mediaType,
        out int dropped,
        int rankOffset = 0)
    {
        dropped = 0;
        if (items is null)
        {
            return [];
        }

        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var mapped = items.Select((entry, i) => MapOne(isTv ? entry.Show : entry.Movie, mediaType, rankOffset + i)).ToList();
        dropped = mapped.Count(m => m is null);
        return [.. mapped.OfType<ExternalDiscoveryCandidate>()];
    }

    private static ExternalDiscoveryCandidate? MapOne(TraktMediaItem? item, string mediaType, int rank)
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
            TraktSlug = string.IsNullOrWhiteSpace(item.Ids?.Slug) ? null : item.Ids.Slug.Trim(),
            SourceRank = rank,
        };
    }
}
