using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Maps Jellyfin library items to their TMDb provider ids.
/// </summary>
/// <remarks>
/// The recommendation engine and the Seerr discovery exclusion both need to answer
/// "which TMDb titles are already in the library". Sharing one implementation keeps the
/// media-type keying (<c>"tv"</c> for series, <c>"movie"</c> otherwise) identical on both
/// paths, which matters because the Seerr exclusion set is keyed on that exact tuple.
/// </remarks>
public static class TmdbLibraryMapper
{
    private const string TmdbProviderKey = "Tmdb";

    /// <summary>
    /// The media-type token used for series entries, matching the Seerr discovery key convention.
    /// </summary>
    public const string TvMediaType = "tv";

    /// <summary>
    /// The media-type token used for movie entries, matching the Seerr discovery key convention.
    /// </summary>
    public const string MovieMediaType = "movie";

    /// <summary>
    /// Builds a set of <c>(TmdbId, MediaType)</c> tuples for every library item that carries a
    /// positive TMDb provider id.
    /// </summary>
    /// <param name="libraryItems">The library items to scan (only Movie and Series are keyed).</param>
    /// <returns>A set of TMDb id + media-type tuples present in the library.</returns>
    public static HashSet<(int TmdbId, string MediaType)> BuildTmdbKeySet(IEnumerable<BaseItem> libraryItems)
    {
        ArgumentNullException.ThrowIfNull(libraryItems);

        // Only Movie and Series carry a discovery media type. Anything else (an Episode, a BoxSet)
        // must be skipped rather than defaulted to "movie", which would silently misclassify it.
        return libraryItems
            .Where(static item => item is Movie or Series)
            .Select(item => (Ok: TryGetTmdbId(item, out var tmdbId), TmdbId: tmdbId, IsSeries: item is Series))
            .Where(static x => x.Ok)
            .Select(static x => (x.TmdbId, x.IsSeries ? TvMediaType : MovieMediaType))
            .ToHashSet();
    }

    /// <summary>
    /// Attempts to read a positive integer TMDb id from an item's provider ids.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="tmdbId">Receives the parsed TMDb id when present and positive.</param>
    /// <returns><see langword="true"/> if a positive TMDb id was found; otherwise <see langword="false"/>.</returns>
    public static bool TryGetTmdbId(BaseItem item, out int tmdbId)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.TryGetProviderId(TmdbProviderKey, out var tmdbStr)
            && int.TryParse(tmdbStr, out tmdbId)
            && tmdbId > 0)
        {
            return true;
        }

        tmdbId = 0;
        return false;
    }
}
