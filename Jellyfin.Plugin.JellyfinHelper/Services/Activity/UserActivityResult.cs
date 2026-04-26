using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Complete user activity result containing all item summaries and global statistics.
///     This is the top-level DTO returned by the API and persisted to cache.
/// </summary>
public sealed class UserActivityResult
{
    private DateTime _generatedAt = DateTime.UtcNow;

    /// <summary>
    ///     Gets or sets the UTC timestamp when this activity data was generated.
    /// </summary>
    public DateTime GeneratedAt
    {
        get => _generatedAt;
        set => _generatedAt = DateTimeNormalization.ToUtc(value);
    }

    /// <summary>
    ///     Gets or sets the total number of items with any user activity.
    /// </summary>
    public int TotalItemsWithActivity { get; set; }

    /// <summary>
    ///     Gets or sets the total number of users analyzed.
    /// </summary>
    public int TotalUsersAnalyzed { get; set; }

    /// <summary>
    ///     Gets or sets the total play count across all items and users.
    /// </summary>
    public long TotalPlayCount { get; set; }

    /// <summary>
    ///     Gets the per-item activity summaries. The builder
    ///     (<see cref="UserActivityInsightsService.BuildActivityReport"/>) populates this
    ///     ordered by total play count descending; cached/deserialized instances preserve
    ///     whatever order was persisted.
    /// </summary>
    public Collection<UserActivitySummary> Items { get; init; } = [];
}
