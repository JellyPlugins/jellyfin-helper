namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>
///     Result of a recommendation playlist synchronization run.
/// </summary>
public sealed class PlaylistSyncResult
{
    /// <summary>
    ///     Gets or sets the number of playlists successfully created.
    /// </summary>
    public int PlaylistsCreated { get; set; }

    /// <summary>
    ///     Gets or sets the number of playlist operations that failed.
    /// </summary>
    public int PlaylistsFailed { get; set; }

    /// <summary>
    ///     Gets or sets the total number of recommendation items added across all playlists.
    /// </summary>
    public int TotalItemsAdded { get; set; }

    /// <summary>
    ///     Gets or sets the number of old playlists that were removed before recreation.
    /// </summary>
    public int OldPlaylistsRemoved { get; set; }
}