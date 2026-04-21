using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Recommendation result for a single user containing their profile and recommended items.
/// </summary>
public sealed class RecommendationResult
{
    /// <summary>
    ///     Gets or sets the user ID these recommendations belong to.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user's display name.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the user's watch profile summary.
    /// </summary>
    public UserWatchProfile? Profile { get; set; }

    /// <summary>
    ///     Gets or sets the recommended items sorted by score descending.
    /// </summary>
    public Collection<RecommendedItem> Recommendations { get; set; } = [];

    /// <summary>
    ///     Gets or sets the UTC timestamp when these recommendations were generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Gets or sets the name of the scoring strategy used to generate these recommendations.
    /// </summary>
    public string ScoringStrategy { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the i18n key for the scoring strategy name.
    /// </summary>
    public string ScoringStrategyKey { get; set; } = string.Empty;
}
