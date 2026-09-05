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
    ///     Builds a watch profile for a specific user.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The user's watch profile, or null if the user was not found.</returns>
    UserWatchProfile? GetUserWatchProfile(Guid userId);

    /// <summary>
    ///     Builds watch profiles for all Jellyfin users.
    /// </summary>
    /// <returns>A list of watch profiles, one per user.</returns>
    Collection<UserWatchProfile> GetAllUserWatchProfiles();

    /// <summary>
    ///     Returns the ids of every live Jellyfin user, straight from the user manager without building a
    ///     profile. Reconciliation against the live user set must use this rather than the ids of
    ///     <see cref="GetAllUserWatchProfiles"/>: profile building can throw for an individual user and is
    ///     skipped, so a live user could be absent from that collection and be mistaken for a removed one.
    /// </summary>
    /// <returns>The ids of all current users.</returns>
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