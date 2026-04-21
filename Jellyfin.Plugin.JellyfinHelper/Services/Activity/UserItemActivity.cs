using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Represents a single user's interaction with a specific media item.
///     Contains all available watch data from Jellyfin's user data manager.
/// </summary>
public sealed class UserItemActivity
{
    /// <summary>
    ///     Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user display name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the number of times this user has played the item.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    ///     Gets or sets the date the user last played this item (UTC).
    /// </summary>
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    ///     Gets or sets the playback position in ticks (for partially watched items).
    /// </summary>
    public long PlaybackPositionTicks { get; set; }

    /// <summary>
    ///     Gets or sets the completion percentage (0–100) based on playback position vs runtime.
    /// </summary>
    public double CompletionPercent { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the item is marked as played/watched.
    /// </summary>
    public bool Played { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the item is a user favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    ///     Gets or sets the user's personal rating (if any).
    /// </summary>
    public double? UserRating { get; set; }
}