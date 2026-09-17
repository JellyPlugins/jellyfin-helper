using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Collects watch history and user profiles from Jellyfin's user data.
/// </summary>
public interface IWatchHistoryService
{
    /// <summary>
    ///     Builds a watch profile for a specific user. Disabled users are treated as if they do not exist.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The user's watch profile, or null if the user was not found or is disabled.</returns>
    UserWatchProfile? GetUserWatchProfile(Guid userId);

    /// <summary>
    ///     Builds watch profiles for all enabled Jellyfin users. Disabled users are skipped.
    /// </summary>
    /// <returns>A list of watch profiles, one per enabled user.</returns>
    Collection<UserWatchProfile> GetAllUserWatchProfiles();

    /// <summary>
    ///     Returns the ids of every enabled Jellyfin user, straight from the user manager without building a
    ///     profile. Reconciliation against the live user set must use this rather than the ids of
    ///     <see cref="GetAllUserWatchProfiles"/>: profile building can throw for an individual user and is
    ///     skipped, so a live user could be absent from that collection and be mistaken for a removed one.
    ///     Disabled users are excluded so their per-user models are pruned as orphans.
    /// </summary>
    /// <returns>The ids of all current enabled users.</returns>
    IReadOnlyCollection<Guid> GetAllUserIds();

    /// <summary>
    ///     Builds the library-wide per-series playable-episode count map (seriesId -> totalEpisodeCount).
    /// </summary>
    /// <returns>
    ///     A map of series ID to playable-episode count. Only series with at least one playable
    ///     episode appear; callers must treat a missing key as "no progression signal available".
    /// </returns>
    IReadOnlyDictionary<Guid, int> GetSeriesEpisodeCounts();
}