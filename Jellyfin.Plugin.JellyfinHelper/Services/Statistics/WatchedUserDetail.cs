using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
/// Per-file per-user watch detail stored in <see cref="LibraryStatistics.WatchedDetails"/>.
/// </summary>
public class WatchedUserDetail
{
    /// <summary>
    /// Gets or sets the Jellyfin username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the play count for this file by this user.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// Gets or sets the last played date (UTC) for this file by this user, or <c>null</c> when unknown.
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user has marked the item as played.
    /// </summary>
    public bool Played { get; set; }
}
