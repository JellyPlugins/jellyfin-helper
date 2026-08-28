using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Collaborative filtering logic: builds co-occurrence maps from user watch overlap using Jaccard similarity, and pre-computes user watch sets for performance.
/// </summary>
internal static class CollaborativeFilter
{
    /// <summary>
    ///     Pre-computes watched-item HashSets for all users at once. Called once in batch recommendation generation and shared across all per-user calls to avoid rebuilding O(U) HashSets per user (O(U²) total to O(U) total).
    /// </summary>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping user ID to their combined watched-item set.</returns>
    internal static Dictionary<Guid, HashSet<Guid>> PrecomputeUserWatchSets(Collection<UserWatchProfile> allProfiles)
    {
        var result = new Dictionary<Guid, HashSet<Guid>>(allProfiles.Count);

        foreach (var profile in allProfiles)
        {
            result[profile.UserId] = BuildCombinedWatchSet(profile);
        }

        return result;
    }

    /// <summary>
    ///     Pre-computes the batch-scoped CollaborativeContext in one pass over the user sets.
    /// </summary>
    /// <param name="userSets">
    ///     User watch sets from <see cref="PrecomputeUserWatchSets"/>. Reused directly - the
    ///     caller retains ownership so no defensive copy is made.
    /// </param>
    /// <returns>A batch-scoped aggregate context bundle.</returns>
    internal static CollaborativeContext PrecomputeCollaborativeContext(
        Dictionary<Guid, HashSet<Guid>> userSets)
    {
        // Item popularity: how many users have watched each item. Feeds the IDF factor in BuildCollaborativeMap (log2(1 + count)^-1) so shared niche watches contribute more than shared mainstream watches.
        var itemPopularity = new Dictionary<Guid, int>(userSets.Count);
        foreach (var userSet in userSets.Values)
        {
            foreach (var itemId in userSet)
            {
                itemPopularity.TryGetValue(itemId, out var count);
                itemPopularity[itemId] = count + 1;
            }
        }

        return new CollaborativeContext(userSets, itemPopularity);
    }

    /// <summary>
    ///     Materialises watch sets for every profile on the single-user (on-demand) code path.
    /// </summary>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping user ID to their combined watched-item set.</returns>
    private static Dictionary<Guid, HashSet<Guid>> BuildAllWatchSetsForSingleUserPath(
        Collection<UserWatchProfile> allProfiles)
    {
        return PrecomputeUserWatchSets(allProfiles);
    }

    /// <summary>
    ///     Builds a combined watch set (item IDs + series IDs) for a single user profile. Used as fallback in single-user mode when precomputed sets are not available.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A set of watched item IDs and parent series IDs.</returns>
    private static HashSet<Guid> BuildCombinedWatchSet(UserWatchProfile profile)
    {
        var combined = new HashSet<Guid>();
        foreach (var w in profile.WatchedItems)
        {
            // Include every item the user meaningfully interacted with - played, favorited, re-watched (PlayCount) OR in-progress (PlaybackPositionTicks).
            if (!w.HasMeaningfulInteraction())
            {
                continue;
            }

            combined.Add(w.ItemId);
            if (w.SeriesId.HasValue)
            {
                combined.Add(w.SeriesId.Value);
            }
        }

        return combined;
    }

    /// <summary>
    ///     Builds a collaborative co-occurrence map: for each unwatched item, accumulates Jaccard-weighted similarity from OTHER users who share watch overlap with this user.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="precomputedUserSets">
    ///     Optional pre-computed watch sets from <see cref="PrecomputeUserWatchSets" />.
    ///     When null, sets are computed on-the-fly (single-user mode).
    /// </param>
    /// <returns>A dictionary mapping item IDs to accumulated Jaccard-weighted scores.</returns>
    internal static Dictionary<Guid, double> BuildCollaborativeMap(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, HashSet<Guid>>? precomputedUserSets = null)
    {
        // Single-user path (live requests + tests that construct only user sets): derive the
        // aggregates locally so callers do not have to know about CollaborativeContext.
        var userSets = precomputedUserSets ?? BuildAllWatchSetsForSingleUserPath(allProfiles);
        var context = PrecomputeCollaborativeContext(userSets);
        return BuildCollaborativeMap(userProfile, allProfiles, context);
    }

    /// <summary>
    ///     Batch-mode overload of BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, Dictionary{Guid, HashSet{Guid}}?) that takes a fully precomputed CollaborativeContext so the O(U×M) item-popularity scan and the O(U) trust-gate scan are shared.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="context">Batch-scoped aggregate state built once per batch.</param>
    /// <returns>A dictionary mapping item IDs to accumulated Jaccard-weighted scores.</returns>
    internal static Dictionary<Guid, double> BuildCollaborativeMap(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        CollaborativeContext context)
    {
        var coOccurrence = new Dictionary<Guid, double>();
        var userSets = context.UserSets;

        // Resolve the current user's combined watch set. Falls back to on-the-fly build if the user is not present in the shared context (e.g.
        var userCombinedIds = userSets.TryGetValue(userProfile.UserId, out var precomputed)
            ? precomputed
            : BuildCombinedWatchSet(userProfile);

        if (userCombinedIds.Count == 0)
        {
            return coOccurrence;
        }

        var itemPopularity = context.ItemPopularity;
        var trustGateActive = ComputeTrustGateActive(userProfile, allProfiles, userSets);

        // Iterate over all other users and compute Jaccard-weighted co-occurrence
        foreach (var otherProfile in allProfiles)
        {
            if (otherProfile.UserId == userProfile.UserId)
            {
                continue;
            }

            // Resolve the other user's combined watch set (uses the shared local materialisation
            // in single-user mode so we do not rebuild this set on every iteration).
            var otherCombinedIds = userSets.TryGetValue(otherProfile.UserId, out var otherPrecomputed)
                ? otherPrecomputed
                : BuildCombinedWatchSet(otherProfile);

            if (otherCombinedIds.Count == 0)
            {
                continue;
            }

            var jaccardWeight = ComputeJaccardWeight(userCombinedIds, otherCombinedIds);
            if (jaccardWeight <= 0.0)
            {
                continue;
            }

            // Trust weight: down-weight neighbours whose overall history is very small so sparse users cannot dominate via a trivially high Jaccard on a handful of items.
            var neighbourTrust = trustGateActive
                ? 1.0 - Math.Exp(-otherCombinedIds.Count / EngineConstants.CollaborativeTrustScale)
                : 1.0;

            if (neighbourTrust <= 0.0)
            {
                continue;
            }

            AccumulateCoOccurrence(coOccurrence, userCombinedIds, otherCombinedIds, itemPopularity, jaccardWeight, neighbourTrust);
        }

        return coOccurrence;
    }

    /// <summary>
    ///     Computes the per-target-user trust gate. The gate activates when at least one NEIGHBOUR (any profile other than the target) has a watch count at or above the trust ceiling.
    /// </summary>
    /// <param name="userProfile">The target user's watch profile (excluded from the scan).</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="userSets">Per-user combined watch sets, with a defensive on-the-fly fallback.</param>
    /// <returns><c>true</c> when a rich-enough neighbour exists; otherwise <c>false</c>.</returns>
    private static bool ComputeTrustGateActive(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, HashSet<Guid>> userSets)
    {
        foreach (var profile in allProfiles)
        {
            if (profile.UserId == userProfile.UserId)
            {
                continue;
            }

            // Fall back to BuildCombinedWatchSet when a profile is missing from userSets so this scan cannot silently treat an "unindexed" neighbour as size-zero (which would make the trust-gate under-count and the later co-occurrence loop over-count for the same profile).
            var otherCount = userSets.TryGetValue(profile.UserId, out var otherSet)
                ? otherSet.Count
                : BuildCombinedWatchSet(profile).Count;
            if (otherCount >= EngineConstants.CollaborativeTrustWatchCeiling)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Computes the Jaccard similarity (|A ∩ B| / |A ∪ B|) between the target user's watch set and a neighbour's watch set.
    /// </summary>
    /// <param name="userCombinedIds">The target user's combined watch set.</param>
    /// <param name="otherCombinedIds">The neighbour's combined watch set.</param>
    /// <returns>The Jaccard weight in [0, 1], or <c>0.0</c> when overlap is below the minimum.</returns>
    private static double ComputeJaccardWeight(HashSet<Guid> userCombinedIds, HashSet<Guid> otherCombinedIds)
    {
        // Compute overlap count by enumerating the smaller set for efficiency
        var (smaller, larger) = userCombinedIds.Count <= otherCombinedIds.Count
            ? (userCombinedIds, otherCombinedIds)
            : (otherCombinedIds, userCombinedIds);
        int overlap = smaller.Count(id => larger.Contains(id));

        if (overlap < EngineConstants.MinCollaborativeOverlap)
        {
            return 0.0;
        }

        // Jaccard similarity: |A ∩ B| / |A ∪ B|
        var union = userCombinedIds.Count + otherCombinedIds.Count - overlap;
        return union > 0 ? (double)overlap / union : 0.0;
    }

    /// <summary>
    ///     Accumulates Jaccard-weighted co-occurrence for items the neighbour watched but the target user has not (episode AND series IDs, so series candidates get collaborative scores).
    /// </summary>
    /// <param name="coOccurrence">The co-occurrence map to accumulate into.</param>
    /// <param name="userCombinedIds">The target user's combined watch set (items to skip).</param>
    /// <param name="otherCombinedIds">The neighbour's combined watch set (items to contribute).</param>
    /// <param name="itemPopularity">Item ID to number of users who have watched it (IDF prior).</param>
    /// <param name="jaccardWeight">The Jaccard weight for this neighbour.</param>
    /// <param name="neighbourTrust">The trust factor for this neighbour.</param>
    private static void AccumulateCoOccurrence(
        Dictionary<Guid, double> coOccurrence,
        HashSet<Guid> userCombinedIds,
        HashSet<Guid> otherCombinedIds,
        Dictionary<Guid, int> itemPopularity,
        double jaccardWeight,
        double neighbourTrust)
    {
        foreach (var itemId in otherCombinedIds)
        {
            if (userCombinedIds.Contains(itemId))
            {
                continue;
            }

            var idfFactor = 1.0;

            // IDF boost: 1 / log2(1 + userCount)
            // log2(1+1)=1.0 (unique), log2(1+5)=2.58, log2(1+50)=5.67
            if (itemPopularity.TryGetValue(itemId, out var userCount) && userCount > 1)
            {
                idfFactor = 1.0 / Math.Log2(1.0 + userCount);
            }

            // Geometric mean of trust and IDF - one combined damping instead of stacking two.
            var combinedModifier = Math.Sqrt(neighbourTrust * idfFactor);
            var weight = jaccardWeight * combinedModifier;

            coOccurrence.TryGetValue(itemId, out var current);
            coOccurrence[itemId] = current + weight;
        }
    }

    /// <summary>
    ///     Batch-mode aggregate state shared across every BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext) call in one scheduled run.
    /// </summary>
    /// <param name="UserSets">Per-user combined watched-item sets (item IDs + series IDs).</param>
    /// <param name="ItemPopularity">Item ID to number of users who have watched it (IDF prior).</param>
    internal sealed record CollaborativeContext(
        Dictionary<Guid, HashSet<Guid>> UserSets,
        Dictionary<Guid, int> ItemPopularity);
}
