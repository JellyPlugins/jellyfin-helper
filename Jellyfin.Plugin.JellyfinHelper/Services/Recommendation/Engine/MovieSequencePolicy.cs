using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Explicit movie viewing dependencies; collection membership or release order alone never establishes a dependency.</summary>
internal static class MovieSequencePolicy
{
    // TMDb collection and movie identities for these verified continuing stories. Unknown
    // collections, anthology entries, reboots and standalone franchises (including Bond)
    // have no inferred dependency. A provider's new collection member is not auto-appended.
    private static readonly Dictionary<int, int[]> Sequences = new()
    {
        [14890] = [9737, 8961, 38700, 573435], // Bad Boys
        [119] = [120, 121, 122], // The Lord of the Rings
        [121938] = [49051, 57158, 122917], // The Hobbit
        [2344] = [603, 604, 605, 624860], // The Matrix
        [264] = [105, 165, 196], // Back to the Future
        [1241] = [671, 672, 673, 674, 675, 767, 12444, 12445], // Harry Potter
        [131635] = [70160, 101299, 131631, 131634], // Katniss's Hunger Games story, excluding the standalone prequel
        [726871] = [438631, 693134] // Dune, Parts One and Two
    };

    private static readonly HashSet<int> KnownMovieIds = Sequences.Values.SelectMany(sequence => sequence).ToHashSet();

    internal static bool IsKnownMovieId(int tmdbId) => KnownMovieIds.Contains(tmdbId);

    /// <summary>Resolves a known movie to its earliest unfinished prerequisite, or keeps its identity when no dependency is known.</summary>
    /// <param name="tmdbId">Typed movie identity.</param>
    /// <param name="collectionId">Corroborating collection identity, never a localized name.</param>
    /// <param name="completed">Only this user's permitted completed movies.</param>
    /// <returns>The required movie identity; availability is checked separately against eligible physical copies.</returns>
    internal static int RequiredMovie(int tmdbId, int collectionId, IReadOnlySet<int> completed)
    {
        if (!Sequences.TryGetValue(collectionId, out var sequence))
        {
            return tmdbId;
        }

        var position = Array.IndexOf(sequence, tmdbId);
        for (var index = 0; index < position; index++)
        {
            if (!completed.Contains(sequence[index]))
            {
                return sequence[index];
            }
        }

        return tmdbId;
    }

    /// <summary>Identifies a verified continuing movie; external provider namespaces never enter this lookup.</summary>
    /// <param name="tmdbId">Typed movie ID.</param>
    /// <param name="collectionId">Known collection ID.</param>
    /// <returns>Whether the movie belongs to an explicitly verified sequence.</returns>
    internal static bool IsKnownMovie(int tmdbId, int collectionId)
        => Sequences.TryGetValue(collectionId, out var sequence) && Array.IndexOf(sequence, tmdbId) >= 0;

    /// <summary>Substitutes within the selected slot and backfills collisions, retaining each delivered movie's own prediction.</summary>
    /// <param name="selected">The actual diversity selection, in selection order.</param>
    /// <param name="eligible">Only already scored, playable, permitted, unseen and content-eligible candidates.</param>
    /// <param name="profile">This user's currently scoped history.</param>
    /// <param name="count">Maximum delivered slots.</param>
    /// <param name="cancellationToken">The owning recommendation request's cancellation.</param>
    /// <returns>Unique selected candidates with their original scores and reasons; blocked prerequisites consume no slot.</returns>
    internal static List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> Apply(
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> selected,
        List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)> eligible,
        UserWatchProfile? profile,
        int count,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!selected.Any(row => IsKnownMovie(MovieId(row.Item), ContentAffinityResolver.ResolveTmdbCollectionId(row.Item))))
        {
            return selected;
        }

        var completed = (profile?.WatchedItems ?? []).Where(row => row.Played && row.TmdbId > 0 && profile?.AccessibleItemIds?.Contains(row.ItemId) != false
            && string.Equals(row.ItemType, "Movie", StringComparison.OrdinalIgnoreCase)).Select(row => row.TmdbId).ToHashSet();
        var movies = eligible.Where(row => row.Item is Movie && MovieId(row.Item) > 0)
            .GroupBy(row => MovieId(row.Item)).ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Score).ThenBy(row => row.Item.Id).First());
        var result = new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>();
        var used = new HashSet<Guid>();
        var usedMovies = new HashSet<int>();
        foreach (var original in selected.Concat(eligible.OrderByDescending(row => row.Score)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= count)
            {
                break;
            }

            var replacement = original;
            var movieId = MovieId(original.Item);
            var required = RequiredMovie(movieId, ContentAffinityResolver.ResolveTmdbCollectionId(original.Item), completed);
            if (required != movieId && !movies.TryGetValue(required, out replacement))
            {
                continue;
            }

            var replacementMovieId = MovieId(replacement.Item);
            if (used.Add(replacement.Item.Id) && (replacementMovieId <= 0 || usedMovies.Add(replacementMovieId)))
            {
                result.Add(replacement);
            }
        }

        return result;
    }

    private static int MovieId(BaseItem item)
        => item is Movie && int.TryParse(item.ProviderIds.GetValueOrDefault("Tmdb"), out var id) && id > 0 ? id : 0;
}
