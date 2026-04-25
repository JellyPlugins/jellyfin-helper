using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Pure computation methods for content-based scoring signals:
///     collaborative score normalization, community rating, recency, year proximity,
///     user rating, completion ratio, average year, and engagement labels.
/// </summary>
internal static class ContentScoring
{
    /// <summary>
    ///     Returns a normalized collaborative score (0–1) for a candidate item.
    /// </summary>
    /// <param name="itemId">The candidate item ID.</param>
    /// <param name="coOccurrence">The collaborative co-occurrence map.</param>
    /// <param name="maxCoOccurrence">The pre-computed maximum co-occurrence value.</param>
    /// <returns>A normalized score between 0 and 1.</returns>
    internal static double ComputeCollaborativeScore(Guid itemId, Dictionary<Guid, double> coOccurrence, double maxCoOccurrence)
    {
        if (maxCoOccurrence <= 0 || !coOccurrence.TryGetValue(itemId, out var count))
        {
            return 0;
        }

        return Math.Clamp(count / maxCoOccurrence, 0.0, 1.0);
    }

    /// <summary>
    ///     Normalizes a community rating (typically 0–10) to a 0–1 score.
    /// </summary>
    /// <param name="communityRating">The community rating value.</param>
    /// <returns>A normalized rating between 0 and 1.</returns>
    internal static double NormalizeRating(float? communityRating)
    {
        if (!communityRating.HasValue || communityRating.Value <= 0)
        {
            return 0.5; // neutral default for unrated items
        }

        return Math.Min(communityRating.Value / 10.0, 1.0);
    }

    /// <summary>
    ///     Computes a recency score based on how recently the item was added or premiered.
    ///     Newer items get a slight boost.
    /// </summary>
    /// <param name="itemDate">
    ///     The item's premiere or creation date. Should be <see cref="DateTimeKind.Utc"/>.
    ///     <see cref="DateTimeKind.Unspecified"/> values are subtracted from <see cref="DateTime.UtcNow"/>
    ///     without conversion, effectively treating them as UTC.
    /// </param>
    /// <param name="now">
    ///     Reference point for "now" (defaults to <see cref="DateTime.UtcNow"/>).
    ///     Exposed for deterministic unit testing.
    /// </param>
    /// <returns>A recency score between 0 and 1.</returns>
    internal static double ComputeRecencyScore(DateTime itemDate, DateTime? now = null)
    {
        var ageInDays = ((now ?? DateTime.UtcNow) - itemDate).TotalDays;
        if (ageInDays <= 0)
        {
            return 1.0;
        }

        // Exponential decay: half-life of ~365 days
        return Math.Exp(-EngineConstants.RecencyDecayConstant * ageInDays);
    }

    /// <summary>
    ///     Computes year proximity score: items closer to the user's average watched year score higher.
    /// </summary>
    /// <param name="candidateYear">The candidate item's production year.</param>
    /// <param name="averageYear">The user's average watched production year.</param>
    /// <returns>A proximity score between 0 and 1.</returns>
    internal static double ComputeYearProximity(int? candidateYear, double averageYear)
    {
        if (!candidateYear.HasValue || averageYear <= 0)
        {
            return 0.5; // neutral default
        }

        var diff = Math.Abs(candidateYear.Value - averageYear);

        // Gaussian-like decay with σ ≈ 10 years
        return Math.Exp(-diff * diff / EngineConstants.YearProximityDenominator);
    }

    /// <summary>
    ///     Computes a normalized user rating score (0–1) for a candidate item.
    ///     If the user has not rated this item, returns 0.5 (neutral).
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A normalized user rating between 0 and 1.</returns>
    internal static double ComputeUserRatingScore(WatchedItemInfo? watchedItem)
    {
        if (watchedItem?.UserRating is null or <= 0)
        {
            return 0.5; // neutral default — no user rating available
        }

        // User ratings are typically 0–10, normalize to 0–1
        return Math.Clamp(watchedItem.UserRating.Value / 10.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes a completion-ratio-modulated engagement label for watched items.
    ///     Instead of a flat label, this interpolates between <see cref="EngineConstants.WatchedLabelFloor"/>
    ///     and <see cref="EngineConstants.WatchedLabel"/> based on how much of the item the user completed.
    ///     This gives the model richer gradient signal: fully-watched items get ~0.85,
    ///     while barely-started items still get a positive label (~0.5) since the user chose to watch.
    /// </summary>
    /// <param name="completionRatio">The watch completion ratio (0–1).</param>
    /// <returns>An engagement label between <see cref="EngineConstants.WatchedLabelFloor"/> and <see cref="EngineConstants.WatchedLabel"/>.</returns>
    internal static double ComputeEngagementLabel(double completionRatio)
    {
        // Clamp input to valid range
        var ratio = Math.Clamp(completionRatio, 0.0, 1.0);

        // Linear interpolation: floor + ratio * (ceiling - floor)
        // At 0% completion: WatchedLabelFloor (0.5) — user chose to watch, still positive
        // At 100% completion: WatchedLabel (0.85) — strong positive signal
        return EngineConstants.WatchedLabelFloor + (ratio * (EngineConstants.WatchedLabel - EngineConstants.WatchedLabelFloor));
    }

    /// <summary>
    ///     Computes the watch completion ratio for a candidate item.
    ///     Returns 0 if the user has never started the item (new candidate),
    ///     or a ratio of played ticks to runtime ticks for partially watched items.
    /// </summary>
    /// <param name="watchedItem">The watched item entry, or null if the user hasn't interacted with it.</param>
    /// <returns>A completion ratio between 0 and 1.</returns>
    internal static double ComputeCompletionRatio(WatchedItemInfo? watchedItem)
    {
        if (watchedItem is null)
        {
            return 0.0; // not started — neutral for candidates
        }

        // Jellyfin resets PlaybackPositionTicks to 0 when an item is marked as played,
        // so rely on the Played flag rather than tick math for fully-watched items.
        if (watchedItem.Played)
        {
            return 1.0;
        }

        if (watchedItem.RuntimeTicks <= 0)
        {
            return 0.0; // no runtime info — neutral for candidates
        }

        return Math.Clamp((double)watchedItem.PlaybackPositionTicks / watchedItem.RuntimeTicks, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the average production year from the user's watched and favorited items.
    ///     Favorites are included because they represent explicit interest in content
    ///     from a particular era, even if not yet played.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>The average production year, or 0 if no years are available.</returns>
    internal static double ComputeAverageYear(UserWatchProfile profile)
    {
        long sum = 0;
        var count = 0;

        foreach (var w in profile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.Year.HasValue))
        {
            sum += w.Year!.Value;
            count++;
        }

        return count > 0 ? (double)sum / count : 0;
    }
}