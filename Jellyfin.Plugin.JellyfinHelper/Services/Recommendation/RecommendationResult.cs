using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Recommendation result for a single user containing their profile and recommended items.
/// </summary>
public sealed class RecommendationResult
{
    private DateTime _generatedAt = DateTime.UtcNow;

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
    ///     Gets the recommended items sorted by score descending.
    /// </summary>
    public Collection<RecommendedItem> Recommendations { get; init; } = [];

    /// <summary>
    ///     Gets or sets the UTC timestamp when these recommendations were generated.
    ///     Normalized to UTC on set to ensure consistency after JSON deserialization
    ///     (mirrors <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Activity.UserActivityResult.GeneratedAt"/>).
    /// </summary>
    public DateTime GeneratedAt
    {
        get => _generatedAt;
        set => _generatedAt = DateTimeNormalization.ToUtc(value);
    }

    /// <summary>
    ///     Gets or sets the name of the scoring strategy used to generate these recommendations.
    /// </summary>
    public string ScoringStrategy { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the i18n key for the scoring strategy name.
    /// </summary>
    public string ScoringStrategyKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the A/B test cohort name for this user's recommendations.
    ///     Assigned by <see cref="Scoring.IStrategySelector.GetCohortName"/> based on
    ///     deterministic user-ID hashing. Possible values:
    ///     <list type="bullet">
    ///       <item><description><c>"control"</c> — baseline alpha (no offset applied).</description></item>
    ///       <item><description><c>"explore-high"</c> — positive alpha offset (more ML weight).</description></item>
    ///       <item><description><c>"explore-low"</c> — negative alpha offset (more heuristic weight).</description></item>
    ///     </list>
    ///     Returns <c>"control"</c> when alpha exploration is inactive (insufficient training data).
    ///     May be <c>null</c> for results that bypass the strategy selector entirely.
    /// </summary>
    public string? Cohort { get; set; }
}