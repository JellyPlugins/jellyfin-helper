using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

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
}