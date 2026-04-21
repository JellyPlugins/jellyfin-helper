using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Aggregated watch profile for a single Jellyfin user.
/// </summary>
public sealed class UserWatchProfile
{
    /// <summary>
    ///     Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user's display name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the total number of watched movies.
    /// </summary>
    public int WatchedMovieCount { get; set; }

    /// <summary>
    ///     Gets or sets the total number of watched episodes.
    /// </summary>
    public int WatchedEpisodeCount { get; set; }

    /// <summary>
    ///     Gets or sets the total number of watched series (at least one episode played).
    /// </summary>
    public int WatchedSeriesCount { get; set; }

    /// <summary>
    ///     Gets or sets the total watch time in ticks (based on runtime of watched items).
    /// </summary>
    public long TotalWatchTimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets the date of the most recent play activity (UTC).
    /// </summary>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    ///     Gets or sets the genre distribution (genre name → watch count).
    /// </summary>
    public Dictionary<string, int> GenreDistribution { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets or sets the number of items marked as favorites.
    /// </summary>
    public int FavoriteCount { get; set; }

    /// <summary>
    ///     Gets or sets the average community rating of watched items.
    /// </summary>
    public double AverageCommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the list of watched items with detailed play data.
    /// </summary>
    public Collection<WatchedItemInfo> WatchedItems { get; set; } = [];
}