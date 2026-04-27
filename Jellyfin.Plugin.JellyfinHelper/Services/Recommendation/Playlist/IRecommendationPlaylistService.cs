using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>
///     Manages Jellyfin playlists that surface recommendation results in the native UI.
///     Creates per-user playlists containing recommended items, ordered by score.
/// </summary>
public interface IRecommendationPlaylistService
{
    /// <summary>
    ///     Creates or updates recommendation playlists for all users based on the given results.
    ///     For each user: deletes the existing recommendation playlist (if any), then creates
    ///     a new one with the recommended items in score-ranked order.
    /// </summary>
    /// <param name="results">The recommendation results (one per user).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of the sync operation.</returns>
    Task<PlaylistSyncResult> UpdatePlaylistsForAllUsersAsync(
        IReadOnlyList<RecommendationResult> results,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes all recommendation playlists managed by this plugin for all users.
    ///     Called when the playlist sync feature is disabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of playlists removed.</returns>
    Task<int> RemoveAllRecommendationPlaylistsAsync(CancellationToken cancellationToken = default);
}