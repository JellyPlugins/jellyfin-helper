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
    ///     Pre-computes the batch-scoped <see cref="CollaborativeContext"/> in one pass over
    ///     the provided user sets. Currently this materialises only the item-popularity map
    ///     — the popularity IDF prior is a pure deployment-wide quantity (how many users
    ///     have watched item X, deployment-wide) so folding it out of the per-user loop
    ///     saves an O(U×M) pass on every downstream <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext)"/>
    ///     invocation.
    ///     <para>
    ///         The trust-gate decision is deliberately NOT precomputed here: it is a per-target-user
    ///         question ("does the target's NEIGHBOURHOOD contain at least one rich profile?"),
    ///         not a deployment-wide one. Excluding the target from the scan is essential —
    ///         otherwise a 28-watch anchor with a 4-watch neighbour would flip its own trust
    ///         gate on and dampen the sparse neighbour's contribution, which is the exact
    ///         opposite of the "cold-start release" behaviour the gate exists for. The
    ///         batch-overload of <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext)"/> therefore keeps its own
    ///         O(U) trust-gate scan, but that scan is proportional to the number of profiles,
    ///         not to the candidate-set size, so it is a cheap constant per user.
    ///     </para>
    /// </summary>
    /// <param name="userSets">
    ///     User watch sets from <see cref="PrecomputeUserWatchSets"/>. Reused directly — the
    ///     caller retains ownership so no defensive copy is made.
    /// </param>
    /// <returns>A batch-scoped aggregate context bundle.</returns>
    internal static CollaborativeContext PrecomputeCollaborativeContext(
        Dictionary<Guid, HashSet<Guid>> userSets)
    {
        // Item popularity: how many users have watched each item. Feeds the IDF factor in
        // BuildCollaborativeMap (log2(1 + count)^-1) so shared niche watches contribute more
        // than shared mainstream watches. Iterating userSets.Values is the same O(U×M) pass
        // the previous per-user path performed — done once here, reused N times downstream.
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
    ///     Delegates to <see cref="PrecomputeUserWatchSets"/> so any future change to the
    ///     materialisation logic only has to happen once; the separate name is retained so
    ///     the intent at the call site is obvious (batch mode receives its dictionary from the
    ///     caller, single-user mode builds a private one so that the neighbour-set lookup inside
    ///     the co-occurrence loop is O(1) instead of rebuilding a neighbour's watch set twice).
    /// </summary>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <returns>A dictionary mapping user ID to their combined watched-item set.</returns>
    private static Dictionary<Guid, HashSet<Guid>> BuildAllWatchSetsForSingleUserPath(
        Collection<UserWatchProfile> allProfiles)
    {
        return PrecomputeUserWatchSets(allProfiles);
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
    ///     <para>
    ///         In hot batch loops prefer the <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext)"/>
    ///         overload which additionally reuses the popularity map and trust-gate decision so
    ///         those O(U×M) / O(U) aggregates are computed exactly once per batch instead of
    ///         once per user.
    ///     </para>
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
    ///     Batch-mode overload of <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, Dictionary{Guid, HashSet{Guid}}?)"/>
    ///     that takes a fully precomputed <see cref="CollaborativeContext"/> so the O(U×M)
    ///     item-popularity scan and the O(U) trust-gate scan are shared across every user in
    ///     the batch. This is the entry point the scheduled recommendation job should use
    ///     after calling <see cref="PrecomputeCollaborativeContext"/> once at batch-init time.
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

        // Resolve the current user's combined watch set. Falls back to on-the-fly build if the
        // user is not present in the shared context (e.g. a freshly created user not yet in the
        // batch snapshot).
        var userCombinedIds = userSets.TryGetValue(userProfile.UserId, out var precomputed)
            ? precomputed
            : BuildCombinedWatchSet(userProfile);

        if (userCombinedIds.Count == 0)
        {
            return coOccurrence;
        }

        var itemPopularity = context.ItemPopularity;

        // Trust-gate is per-target-user (excludes the target from the scan). We intentionally
        // do NOT precompute this in CollaborativeContext because the target's own watch count
        // would flip its own gate on — the sparse-neighbour attenuation and cold-start release
        // behaviours both hinge on the "NEIGHBOURS ONLY" restriction. The scan is O(profiles),
        // dwarfed by the co-occurrence loop below, so keeping it per-user is essentially free.
        var trustGateActive = false;
        foreach (var profile in allProfiles)
        {
            if (profile.UserId == userProfile.UserId)
            {
                continue;
            }

            // Fall back to BuildCombinedWatchSet when a profile is missing from userSets so
            // this scan cannot silently treat an "unindexed" neighbour as size-zero (which
            // would make the trust-gate under-count and the later co-occurrence loop
            // over-count for the same profile). In practice userSets and allProfiles are
            // built off the same list, so this fallback is defensive; the O(M) build cost
            // fires at most once per stale profile.
            var otherCount = userSets.TryGetValue(profile.UserId, out var otherSet)
                ? otherSet.Count
                : BuildCombinedWatchSet(profile).Count;
            if (otherCount >= EngineConstants.CollaborativeTrustWatchCeiling)
            {
                trustGateActive = true;
                break;
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
            int overlap = 0;
            foreach (var id in smaller)
            {
                if (larger.Contains(id))
                {
                    overlap++;
                }
            }

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
                if (itemPopularity.TryGetValue(itemId, out var userCount) && userCount > 1)
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

    /// <summary>
    ///     Batch-mode aggregate state shared across every
    ///     <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext)"/>
    ///     invocation in a single scheduled run. Bundles the per-user watch sets with the
    ///     precomputed item-popularity map (for IDF weighting) so the O(U×M) popularity scan
    ///     is performed exactly once — not N times per user as it would be if it were rebuilt
    ///     on every call.
    ///     <para>
    ///         The trust-gate decision is intentionally NOT part of this record because it is
    ///         a per-target-user question (see the block comment inside
    ///         <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, CollaborativeContext)"/>
    ///         for the full rationale). Precomputing it deployment-wide would incorrectly fold
    ///         the current user's own watch count into the "do we have a rich neighbour?" answer
    ///         and break the sparse-neighbour attenuation the gate exists for.
    ///     </para>
    ///     <para>
    ///         Single-user (live) callers never construct one of these and let the
    ///         <see cref="BuildCollaborativeMap(UserWatchProfile, Collection{UserWatchProfile}, Dictionary{Guid, HashSet{Guid}}?)"/>
    ///         overload derive itemPopularity on demand from the local <c>userSets</c>
    ///         materialisation.
    ///     </para>
    /// </summary>
    /// <param name="UserSets">Per-user combined watched-item sets (item IDs + series IDs).</param>
    /// <param name="ItemPopularity">Item ID → number of users who have watched it (IDF prior).</param>
    internal sealed record CollaborativeContext(
        Dictionary<Guid, HashSet<Guid>> UserSets,
        Dictionary<Guid, int> ItemPopularity);
}
