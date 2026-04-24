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
    ///     Items that are favorited (even if not yet played) are also included — they
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
    ///     Builds a combined watch set (item IDs + series IDs) for a single user profile.
    ///     Used as fallback in single-user mode when precomputed sets are not available.
    ///     Includes favorited items for the same reasons as <see cref="PrecomputeUserWatchSets"/>.
    /// </summary>
    /// <param name="profile">The user's watch profile.</param>
    /// <returns>A set of watched item IDs and parent series IDs.</returns>
    private static HashSet<Guid> BuildCombinedWatchSet(UserWatchProfile profile)
    {
        var combined = new HashSet<Guid>();
        foreach (var w in profile.WatchedItems)
        {
            // Include items that are played OR favorited
            if (!w.Played && !w.IsFavorite)
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
    ///     When <paramref name="precomputedUserSets"/> is provided (batch mode),
    ///     uses those sets directly instead of rebuilding them per call — reducing
    ///     total complexity from O(U²×M) to O(U×M).
    /// </summary>
    /// <param name="userProfile">The target user's watch profile.</param>
    /// <param name="allProfiles">All user watch profiles.</param>
    /// <param name="precomputedUserSets">
    ///     Optional pre-computed watch sets from <see cref="PrecomputeUserWatchSets"/>.
    ///     When null, sets are computed on-the-fly (single-user mode).
    /// </param>
    /// <returns>A dictionary mapping item IDs to accumulated Jaccard-weighted scores.</returns>
    internal static Dictionary<Guid, double> BuildCollaborativeMap(
        UserWatchProfile userProfile,
        Collection<UserWatchProfile> allProfiles,
        Dictionary<Guid, HashSet<Guid>>? precomputedUserSets = null)
    {
        var coOccurrence = new Dictionary<Guid, double>();

        // Resolve the current user's combined watch set
        var userCombinedIds =
            precomputedUserSets is not null && precomputedUserSets.TryGetValue(userProfile.UserId, out var precomputed)
                ? precomputed
                : BuildCombinedWatchSet(userProfile); // Fallback: build on-the-fly (single-user mode)

        if (userCombinedIds.Count == 0)
        {
            return coOccurrence;
        }

        // Iterate over all other users and compute Jaccard-weighted co-occurrence
        foreach (var otherProfile in allProfiles)
        {
            if (otherProfile.UserId == userProfile.UserId)
            {
                continue;
            }

            // Resolve the other user's combined watch set
            var otherCombinedIds =
                precomputedUserSets is not null && precomputedUserSets.TryGetValue(otherProfile.UserId, out var otherPrecomputed)
                    ? otherPrecomputed
                    : BuildCombinedWatchSet(otherProfile); // Fallback: build on-the-fly

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

            // Accumulate Jaccard-weighted co-occurrence for items the other user watched but we haven't.
            // This includes both episode IDs AND series IDs, so series candidates get collaborative scores.
            foreach (var itemId in otherCombinedIds.Where(itemId => !userCombinedIds.Contains(itemId)))
            {
                coOccurrence.TryGetValue(itemId, out var current);
                coOccurrence[itemId] = current + jaccardWeight;
            }
        }

        return coOccurrence;
    }
}