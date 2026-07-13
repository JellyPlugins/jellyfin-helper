using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Collaborative filtering logic: builds co-occurrence maps from user watch overlap
///     using Jaccard similarity, and pre-computes user watch sets for performance.
/// </summary>
internal static class CollaborativeFilter
{
    /// <summary>
    ///     Pre-computes watched-item HashSets for all users at once.
    ///     Called once in batch recommendation generation and shared across all per-user calls
    ///     to avoid rebuilding O(U) HashSets per user (O(U²) total → O(U) total).
    ///     Each set includes both direct item IDs and parent series IDs from episode watches.
    ///     Items that are favorited (even if not yet played) are also included - they
    ///     represent explicit interest and improve user-similarity calculation.
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
    ///     Materialises watch sets for every profile on the single-user (on-demand) code path.
    ///     Structurally identical to <see cref="PrecomputeUserWatchSets"/> but named separately
    ///     so the intent is obvious at the call site: batch mode receives its dictionary from
    ///     the caller, single-user mode builds a private one so that the two loops inside
    ///     <see cref="BuildCollaborativeMap"/> both hit an O(1) lookup instead of rebuilding a
    ///     neighbour's watch set twice.
    /// </summary>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping user ID to their combined watched-item set.</returns>
    private static Dictionary<Guid, HashSet<Guid>> BuildAllWatchSetsForSingleUserPath(
        Collection<UserWatchProfile> allProfiles)
    {
        var result = new Dictionary<Guid, HashSet<Guid>>(allProfiles.Count);
        foreach (var profile in allProfiles)
        {
            result[profile.UserId] = BuildCombinedWatchSet(profile);
        }

        return result;
    }

    /// <summary>
    ///     Builds a combined watch set (item IDs + series IDs) for a single user profile.
    ///     Used as fallback in single-user mode when precomputed sets are not available.
    ///     Includes favorited items for the same reasons as <see cref="PrecomputeUserWatchSets" />.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A set of watched item IDs and parent series IDs.</returns>
    private static HashSet<Guid> BuildCombinedWatchSet(UserWatchProfile profile)
    {
        var combined = new HashSet<Guid>();
        foreach (var w in profile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (w is { Played: false, IsFavorite: false })
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
    ///     Builds a collaborative co-occurrence map: for each unwatched item,
    ///     accumulates Jaccard-weighted similarity from OTHER users who share watch
    ///     overlap with this user. Uses true Jaccard similarity (0–1) instead of
    ///     discretized integer weights for better precision.
    ///     When <paramref name="precomputedUserSets" /> is provided (batch mode),
    ///     uses those sets directly instead of rebuilding them per call - reducing
    ///     total complexity from O(U²×M) to O(U×M).
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
        var coOccurrence = new Dictionary<Guid, double>();

        // When precomputedUserSets is null (single-user on-demand path) we would otherwise call
        // BuildCombinedWatchSet(profile) both in the cold-start gate loop and again in the main
        // co-occurrence loop below — the same allocation-heavy work twice per neighbour. Build a
        // local materialisation once here so both loops share it. In batch mode the caller already
        // supplied a precomputed dictionary and this fast-path is a no-op assignment.
        var userSets = precomputedUserSets ?? BuildAllWatchSetsForSingleUserPath(allProfiles);

        // Resolve the current user's combined watch set
        var userCombinedIds = userSets.TryGetValue(userProfile.UserId, out var precomputed)
            ? precomputed
            : BuildCombinedWatchSet(userProfile);

        if (userCombinedIds.Count == 0)
        {
            return coOccurrence;
        }

        // Cold-start guard: when every neighbour in the deployment is below the trust ceiling,
        // the neighbour trust factor would collapse the entire collaborative signal to a tiny
        // fraction (stacked with IDF that can be < 0.2 for 50-user mainstream items). Detect the
        // uniformly-sparse case and disable trust scaling so that early deployments still get
        // meaningful collaborative recommendations. Once at least one user crosses the ceiling
        // the trust factor kicks back in and protects against sparse-history over-weighting.
        var trustGateActive = false;
        foreach (var profile in allProfiles)
        {
            if (profile.UserId == userProfile.UserId)
            {
                continue;
            }

            var otherCount = userSets.TryGetValue(profile.UserId, out var otherSet)
                ? otherSet.Count
                : 0;
            if (otherCount >= EngineConstants.CollaborativeTrustWatchCeiling)
            {
                trustGateActive = true;
                break;
            }
        }

        // Compute item popularity (how many users watched each item) for IDF weighting.
        // Items watched by many users contribute less to co-occurrence - a shared niche taste
        // is a stronger signal of real similarity than both watching a mainstream blockbuster.
        // Only computed when precomputedUserSets is available (batch mode); in single-user mode
        // the overhead of a full scan isn't justified.
        Dictionary<Guid, int>? itemPopularity = null;
        if (precomputedUserSets is not null)
        {
            itemPopularity = new Dictionary<Guid, int>();
            foreach (var userSet in precomputedUserSets.Values)
            {
                foreach (var itemId in userSet)
                {
                    itemPopularity.TryGetValue(itemId, out var count);
                    itemPopularity[itemId] = count + 1;
                }
            }
        }

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

            // Compute overlap count by enumerating the smaller set for efficiency
            var (smaller, larger) = userCombinedIds.Count <= otherCombinedIds.Count
                ? (userCombinedIds, otherCombinedIds)
                : (otherCombinedIds, userCombinedIds);
            var overlap = smaller.Count(larger.Contains);

            if (overlap < EngineConstants.MinCollaborativeOverlap)
            {
                continue;
            }

            // Jaccard similarity: |A ∩ B| / |A ∪ B|
            var union = userCombinedIds.Count + otherCombinedIds.Count - overlap;
            var jaccardWeight = union > 0 ? (double)overlap / union : 0.0;

            // Trust weight: down-weight neighbours whose overall history is very small so
            // sparse users cannot dominate through a trivially high Jaccard on a handful of
            // items. Uses a saturating exponential curve so the ramp is gentle at the low end
            // (5 watches → ~0.39) and reaches near-full trust well before the ceiling
            // (20 watches → ~0.86, 30 → ~0.95), avoiding the linear cliff of the previous
            // formula that quartered a 5-watch neighbour to 25%.
            //
            // Cold-start gate: when the whole deployment is below the ceiling (early rollout
            // with a couple of low-history users), trust would still collapse the signal to a
            // few percent even against the least-sparse neighbour. In that case we release
            // the trust factor entirely so the collaborative branch produces meaningful
            // recommendations from day one.
            var neighbourTrust = trustGateActive
                ? 1.0 - Math.Exp(-otherCombinedIds.Count / EngineConstants.CollaborativeTrustScale)
                : 1.0;

            if (neighbourTrust <= 0.0)
            {
                continue;
            }

            // Accumulate Jaccard-weighted co-occurrence for items the other user watched but we haven't.
            // This includes both episode IDs AND series IDs, so series candidates get collaborative scores.
            // The geometric mean lives in <c>[min(a,b), max(a,b)]</c>, so it cannot fall below the
            // smaller of the two factors — a mathematically cleaner "combined damping" that
            // preserves the ordering guarantees of both factors:
            //   • trust=1.0, idf=1.0 → modifier=1.0                       (rich neighbour, niche item)
            //   • trust=0.86, idf=0.18 → modifier=0.394 (was 0.155)       (~2.5× stronger signal)
            //   • trust=0.39, idf=1.0 → modifier=0.628 (was 0.39)         (sparse neighbour keeps its unique-item boost)
            //
            // Ordering-preserving properties verified via the existing IDF and cold-start-gate tests:
            //   niche > mainstream                     ← IDF direction preserved: √(t·1) > √(t·<1)
            //   coldStartScore > controlScore          ← Trust direction preserved: √(1·i) > √(<1·i)
            foreach (var itemId in otherCombinedIds.Where(itemId => !userCombinedIds.Contains(itemId)))
            {
                var idfFactor = 1.0;

                // IDF boost: 1 / log2(1 + userCount)
                // log2(1+1)=1.0 (unique), log2(1+5)=2.58, log2(1+50)=5.67
                if (itemPopularity is not null && itemPopularity.TryGetValue(itemId, out var userCount) &&
                    userCount > 1)
                {
                    idfFactor = 1.0 / Math.Log2(1.0 + userCount);
                }

                // Geometric mean of trust and IDF — one combined damping instead of stacking two.
                var combinedModifier = Math.Sqrt(neighbourTrust * idfFactor);
                var weight = jaccardWeight * combinedModifier;

                coOccurrence.TryGetValue(itemId, out var current);
                coOccurrence[itemId] = current + weight;
            }
        }

        return coOccurrence;
    }
}