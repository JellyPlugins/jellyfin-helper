using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Aggregated watch profile for a single Jellyfin user.
/// </summary>
public sealed class UserWatchProfile
{
    private Dictionary<string, int> _genreDistribution = new(StringComparer.OrdinalIgnoreCase);

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
    ///     Gets or sets the total unique content runtime in ticks (sum of runtime for each
    ///     distinct played item, counted once regardless of <c>PlayCount</c>).
    ///     This represents "how much unique content was consumed", not "total time spent watching"
    ///     which would require multiplying by re-watch count.
    /// </summary>
    public long TotalWatchTimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets the date of the most recent play activity (UTC).
    /// </summary>
    public DateTime? LastActivityDate { get; set; }

    /// <summary>
    ///     Gets or sets the genre distribution (genre name → watch count).
    ///     The setter preserves <see cref="StringComparer.OrdinalIgnoreCase"/> to ensure
    ///     genre aggregation is always case-insensitive, even when a new dictionary is assigned.
    /// </summary>
    public Dictionary<string, int> GenreDistribution
    {
        get => _genreDistribution;
        set => _genreDistribution = value is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Gets or sets the number of favorite items.
    /// </summary>
    public int FavoriteCount { get; set; }

    /// <summary>
    ///     Gets the set of series IDs that the user has marked as favorite at the series level.
    ///     In Jellyfin, users can favorite a whole series (not just individual episodes).
    ///     This set captures those series-level favorites so that the recommendation engine
    ///     can treat them as positive signals even when no individual episode is favorited.
    /// </summary>
    public HashSet<Guid> FavoriteSeriesIds { get; init; } = [];

    /// <summary>
    ///     Gets or sets the average community rating of watched items.
    /// </summary>
    public double AverageCommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the list of watched items with detailed play data.
    /// </summary>
    public Collection<WatchedItemInfo> WatchedItems { get; set; } = [];
}