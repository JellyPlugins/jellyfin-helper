using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Extension methods for evaluating <see cref="SeerrPermissions"/> on a <see cref="SeerrUser"/>.
/// </summary>
public static class SeerrPermissionExtensions
{
    /// <summary>
    ///     Determines whether the user holds the specified permission flag.
    ///     Implicitly returns <c>true</c> if the user is an admin (admins have all permissions).
    /// </summary>
    /// <param name="user">The Seerr user.</param>
    /// <param name="flag">The permission flag to check.</param>
    /// <returns><c>true</c> if the user has the permission; otherwise <c>false</c>.</returns>
    public static bool HasPermission(this SeerrUser user, SeerrPermissions flag)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Zero-flag guard: HasFlag(0) always returns true, which would incorrectly authorize.
        if (flag == SeerrPermissions.None)
        {
            return false;
        }

        var permissions = (SeerrPermissions)user.Permissions;

        // Admins implicitly hold every permission
        if (permissions.HasFlag(SeerrPermissions.Admin))
        {
            return true;
        }

        return permissions.HasFlag(flag);
    }

    /// <summary>
    ///     Determines whether the user can submit a standard (non-4K) request for the given media type.
    ///     Checks the general <see cref="SeerrPermissions.Request"/> flag first, then
    ///     the granular <see cref="SeerrPermissions.RequestMovie"/> / <see cref="SeerrPermissions.RequestTv"/> flags.
    /// </summary>
    /// <param name="user">The Seerr user.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <returns><c>true</c> if the user can request the specified media type.</returns>
    public static bool CanRequest(this SeerrUser user, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Validate mediaType first — unknown types are always denied regardless of permission level.
        // This prevents garbage mediaType values from being authorized even for admins (defense in depth).
        var isMovie = string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase);
        var isTv = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase);
        if (!isMovie && !isTv)
        {
            return false;
        }

        // Admins can do everything
        if (user.HasPermission(SeerrPermissions.Admin))
        {
            return true;
        }

        // General request permission covers both types
        if (user.HasPermission(SeerrPermissions.Request))
        {
            return true;
        }

        // Granular per-type permissions
        if (isMovie)
        {
            return user.HasPermission(SeerrPermissions.RequestMovie);
        }

        return user.HasPermission(SeerrPermissions.RequestTv);
    }

    /// <summary>
    ///     Determines whether the user can select a custom quality profile (advanced request flow).
    ///     This is true for admins, users with MANAGE_REQUESTS, or users with REQUEST_ADVANCED.
    /// </summary>
    /// <param name="user">The Seerr user.</param>
    /// <returns><c>true</c> if the user may choose quality profiles; otherwise <c>false</c>.</returns>
    public static bool CanSelectQualityProfile(this SeerrUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.HasPermission(SeerrPermissions.Admin)
            || user.HasPermission(SeerrPermissions.ManageRequests)
            || user.HasPermission(SeerrPermissions.RequestAdvanced);
    }
}