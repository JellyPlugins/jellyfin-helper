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
    ///     Builds the library-wide per-series playable-episode count map
    ///     (<c>seriesId -> totalEpisodeCount</c>). This is the same signal the recommendation
    ///     engine derives in its candidate load, exposed here so subsystems that build genre
    ///     preferences outside the engine (e.g. Seerr discovery) can feed the identical
    ///     progression-weighting into <c>PreferenceBuilder.BuildGenrePreferenceVector</c> and
    ///     thereby avoid a train/serve skew against the engine's training pipeline.
    /// </summary>
    /// <returns>
    ///     A map of series ID to playable-episode count. Only series with at least one playable
    ///     episode appear; callers must treat a missing key as "no progression signal available".
    /// </returns>
    IReadOnlyDictionary<Guid, int> GetSeriesEpisodeCounts();
}