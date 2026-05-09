using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Bidirectional mapping between TMDb genre IDs and Jellyfin genre strings.
///     TMDb uses integer IDs; Jellyfin uses localized strings. This map uses the
///     English TMDb genre names which match Jellyfin's default genre metadata.
/// </summary>
internal static class TmdbGenreMap
{
    private static readonly Dictionary<int, string> MovieGenres = new()
    {
        [28] = "Action",
        [12] = "Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [14] = "Fantasy",
        [36] = "History",
        [27] = "Horror",
        [10402] = "Music",
        [9648] = "Mystery",
        [10749] = "Romance",
        [878] = "Science Fiction",
        [10770] = "TV Movie",
        [53] = "Thriller",
        [10752] = "War",
        [37] = "Western"
    };

    private static readonly Dictionary<int, string> TvGenres = new()
    {
        [10759] = "Action & Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [10762] = "Kids",
        [9648] = "Mystery",
        [10763] = "News",
        [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",
        [10767] = "Talk",
        [10768] = "War & Politics",
        [37] = "Western"
    };

    // Reverse lookup: Jellyfin genre string -> TMDb movie genre ID (case-insensitive)
    private static readonly Dictionary<string, int> ReverseMovieGenres = BuildReverseMovieGenres();
    private static readonly Dictionary<string, int> ReverseTvGenres = BuildReverseTvGenres();

    private static Dictionary<string, int> BuildReverseMovieGenres()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in MovieGenres)
        {
            dict.TryAdd(name, id);
        }

        // Aliases for common Jellyfin genre variations
        dict.TryAdd("Sci-Fi", 878);
        dict.TryAdd("SciFi", 878);
        return dict;
    }

    private static Dictionary<string, int> BuildReverseTvGenres()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in TvGenres)
        {
            dict.TryAdd(name, id);
        }

        dict.TryAdd("Science Fiction", 10765);
        dict.TryAdd("Sci-Fi", 10765);
        return dict;
    }

    /// <summary>
    ///     Converts TMDb genre IDs to Jellyfin genre strings.
    ///     Unknown IDs are skipped gracefully.
    /// </summary>
    /// <param name="tmdbIds">The list of TMDb genre IDs.</param>
    /// <returns>A list of Jellyfin genre strings for mapped IDs.</returns>
    internal static List<string> ToJellyfinGenres(IReadOnlyList<int> tmdbIds)
    {
        ArgumentNullException.ThrowIfNull(tmdbIds);

        var result = new List<string>(tmdbIds.Count);
        foreach (var id in tmdbIds)
        {
            if (MovieGenres.TryGetValue(id, out var name) || TvGenres.TryGetValue(id, out name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    ///     Converts a Jellyfin genre string to TMDb movie genre ID.
    /// </summary>
    /// <param name="jellyfinGenre">The Jellyfin genre string.</param>
    /// <returns>The TMDb movie genre ID, or null if unmapped.</returns>
    internal static int? ToMovieTmdbId(string jellyfinGenre)
        => ReverseMovieGenres.TryGetValue(jellyfinGenre, out var id) ? id : null;

    /// <summary>
    ///     Converts a Jellyfin genre string to TMDb TV genre ID.
    /// </summary>
    /// <param name="jellyfinGenre">The Jellyfin genre string.</param>
    /// <returns>The TMDb TV genre ID, or null if unmapped.</returns>
    internal static int? ToTvTmdbId(string jellyfinGenre)
        => ReverseTvGenres.TryGetValue(jellyfinGenre, out var id) ? id : null;
}