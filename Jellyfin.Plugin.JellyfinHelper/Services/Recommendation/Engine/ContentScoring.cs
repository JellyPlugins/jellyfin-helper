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
    ///     Normalizes a Rotten Tomatoes critic rating (0–100%) to a 0–1 score.
    ///     Returns 0.5 (neutral) when the value is null, zero, negative, or NaN.
    ///     Jellyfin stores CriticRating as a float? representing the "Tomatometer" percentage.
    /// </summary>
    /// <param name="criticRating">The critic rating value (0–100).</param>
    /// <returns>A normalized score between 0 and 1, or 0.5 if unavailable.</returns>
    internal static double NormalizeCriticRating(float? criticRating)
    {
        if (!criticRating.HasValue || float.IsNaN(criticRating.Value) || criticRating.Value <= 0)
        {
            return 0.5; // Neutral fallback — does not penalize items without critic data
        }

        return Math.Clamp(criticRating.Value / 100.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Normalizes a community rating (typically 0–10) to a 0–1 score.
    /// </summary>
    /// <param name="communityRating">The community rating value.</param>
    /// <returns>A normalized rating between 0 and 1.</returns>
    internal static double NormalizeRating(float? communityRating)
    {
        if (!communityRating.HasValue || float.IsNaN(communityRating.Value) || communityRating.Value <= 0)
        {
            return 0.5; // neutral default for unrated or NaN items
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
        if (watchedItem?.UserRating is null or <= 0 || double.IsNaN(watchedItem.UserRating.Value))
        {
            return 0.5; // neutral default — no user rating available or NaN
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

        foreach (var w in profile.WatchedItems.Where(w => (w.Played || w.IsFavorite) && w.Year is > 0))
        {
            sum += w.Year!.Value;
            count++;
        }

        return count > 0 ? (double)sum / count : 0;
    }

    /// <summary>
    ///     Computes the content-based nearest-neighbor score for a candidate item.
    ///     For each watched item, calculates a composite similarity using:
    ///     - Genre Jaccard similarity (50% weight)
    ///     - People/cast Jaccard similarity (30% weight)
    ///     - Studio overlap (20% weight, binary: 1 if any shared studio, 0 otherwise)
    ///     Returns the MAX composite similarity across all watched items, measuring how similar
    ///     this candidate is to the user's most similar watched item.
    /// </summary>
    /// <remarks>
    ///     Unlike GenreSimilarity (which compares against the aggregated user profile), this
    ///     captures item-to-item affinity: a niche anime in a mostly-action user's library
    ///     will still boost similar anime candidates because of the specific item-level match.
    ///     Performance: O(W × G) where W = watched items, G = max genres per item (~5).
    ///     Typically &lt;10ms for 200 watched items × 1000 candidates when called per-candidate.
    /// </remarks>
    /// <param name="candidateGenres">The candidate's genre set (case-insensitive).</param>
    /// <param name="candidatePeople">The candidate's people/cast set (case-insensitive), or null if unavailable.</param>
    /// <param name="candidateStudios">The candidate's studios array, or null/empty if unavailable.</param>
    /// <param name="watchedGenreSets">Pre-computed genre sets for each watched item.</param>
    /// <param name="watchedPeopleSets">Pre-computed people sets for each watched item (parallel to genre sets).</param>
    /// <param name="watchedStudioSets">Pre-computed studio sets for each watched item (parallel to genre sets).</param>
    /// <returns>A composite similarity score between 0 and 1.</returns>
    internal static double ComputeContentNearestNeighborScore(
        HashSet<string> candidateGenres,
        HashSet<string>? candidatePeople,
        HashSet<string>? candidateStudios,
        IReadOnlyList<HashSet<string>> watchedGenreSets,
        IReadOnlyList<HashSet<string>> watchedPeopleSets,
        IReadOnlyList<HashSet<string>> watchedStudioSets)
    {
        if (watchedGenreSets.Count == 0)
        {
            return 0.0;
        }

        var maxComposite = 0.0;

        for (var i = 0; i < watchedGenreSets.Count; i++)
        {
            // Genre Jaccard (50% of composite)
            var genreJaccard = ComputeJaccard(candidateGenres, watchedGenreSets[i]);

            // People Jaccard (30% of composite)
            var peopleJaccard = candidatePeople is { Count: > 0 } && i < watchedPeopleSets.Count
                ? ComputeJaccard(candidatePeople, watchedPeopleSets[i])
                : 0.0;

            // Studio overlap (20% of composite) — binary: any shared studio = 1.0
            var studioOverlap = 0.0;
            if (candidateStudios is { Count: > 0 } && i < watchedStudioSets.Count && watchedStudioSets[i].Count > 0)
            {
                studioOverlap = candidateStudios.Any(s => watchedStudioSets[i].Contains(s)) ? 1.0 : 0.0;
            }

            var composite = (0.50 * genreJaccard) + (0.30 * peopleJaccard) + (0.20 * studioOverlap);
            if (composite > maxComposite)
            {
                maxComposite = composite;
            }
        }

        return Math.Clamp(maxComposite, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes the Jaccard similarity coefficient between two string sets.
    ///     Jaccard = |intersection| / |union|. Returns 0 when both sets are empty.
    /// </summary>
    private static double ComputeJaccard(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 && setB.Count == 0)
        {
            return 0.0;
        }

        // Iterate the smaller set for efficiency
        var (smaller, larger) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        var intersection = smaller.Count(item => larger.Contains(item));

        var union = setA.Count + setB.Count - intersection;
        return union > 0 ? (double)intersection / union : 0.0;
    }
}
