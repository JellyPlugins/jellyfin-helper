using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Aggregated watch activity for a single media item across all users.
///     Contains per-user breakdown and summary statistics.
/// </summary>
public sealed class UserActivitySummary
{
    private DateTime? _mostRecentWatch;

    /// <summary>
    ///     Gets or sets the Jellyfin item ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    ///     Gets or sets the item name/title.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the item type (e.g. "Movie", "Episode", "Series").
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the parent series name (only for episodes).
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    ///     Gets or sets a short season/episode label like "S01E03" (only for episodes).
    /// </summary>
    public string? EpisodeLabel { get; set; }

    /// <summary>
    ///     Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    ///     Gets or sets the genres associated with this item.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "DTO for JSON serialization")]
    public string[] Genres { get; set; } = [];

    /// <summary>
    ///     Gets or sets the community rating from metadata providers.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    ///     Gets or sets the total runtime in ticks.
    /// </summary>
    public long RuntimeTicks { get; set; }

    /// <summary>
    ///     Gets or sets the total number of plays across all users.
    /// </summary>
    public int TotalPlayCount { get; set; }

    /// <summary>
    ///     Gets or sets the number of unique users who interacted with this item.
    /// </summary>
    public int UniqueViewers { get; set; }

    /// <summary>
    ///     Gets or sets the most recent watch date across all users (UTC).
    /// </summary>
    public DateTime? MostRecentWatch
    {
        get => _mostRecentWatch;
        set => _mostRecentWatch = value.HasValue ? DateTimeNormalization.ToUtc(value.Value) : null;
    }

    /// <summary>
    ///     Gets or sets the average completion percentage across all users.
    /// </summary>
    public double AverageCompletionPercent { get; set; }

    /// <summary>
    ///     Gets or sets the number of users who marked this item as favorite.
    /// </summary>
    public int FavoriteCount { get; set; }

    /// <summary>
    ///     Gets the per-user activity details for this item.
    /// </summary>
    public Collection<UserItemActivity> UserActivities { get; init; } = [];
}