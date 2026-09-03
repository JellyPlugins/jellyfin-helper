using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Shared helpers for the discovery endpoints. <see cref="DiscoveryController" /> (admin) and
///     <see cref="UserDiscoveryController" /> (per-user) both resolve the caller's Jellyfin id and
///     build the excluded-item set the same way; centralizing that logic keeps the two controllers
///     from diverging.
/// </summary>
internal static class DiscoverySupport
{
    /// <summary>
    ///     Extracts the caller's Jellyfin user id from the authentication claims, preferring the
    ///     <c>Jellyfin-UserId</c> claim and falling back to <see cref="ClaimTypes.NameIdentifier" />.
    /// </summary>
    /// <param name="user">The authenticated principal from the controller's <c>User</c> property.</param>
    /// <returns>The parsed user id, or <c>null</c> when no parseable claim is present.</returns>
    internal static Guid? GetCurrentUserId(ClaimsPrincipal? user)
    {
        var claim = user?.FindFirst("Jellyfin-UserId")
            ?? user?.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var parsedUserId))
        {
            return parsedUserId;
        }

        return null;
    }

    /// <summary>
    ///     Builds the set of item keys excluded from a user's visible discovery pool by unioning the
    ///     dismissed and requested items from the feedback store. A non-fatal store failure is handed to
    ///     the caller-supplied <paramref name="onError" /> callback (so each controller keeps its own
    ///     static log template, satisfying CA2254) and yields whatever was gathered so far.
    /// </summary>
    /// <param name="store">The discovery feedback store.</param>
    /// <param name="userId">The user whose exclusions are built.</param>
    /// <param name="onError">Invoked with the non-fatal exception when the store throws; typically logs a warning.</param>
    /// <returns>The set of excluded <c>(TmdbId, MediaType)</c> keys.</returns>
    internal static HashSet<(int TmdbId, string MediaType)> BuildExcludedItemKeys(
        IDiscoveryFeedbackStore store,
        Guid userId,
        Action<Exception> onError)
    {
        var excluded = new HashSet<(int TmdbId, string MediaType)>();
        try
        {
            foreach (var item in store.GetDismissedItems(userId))
            {
                excluded.Add(item);
            }

            foreach (var item in store.GetRequestedItems(userId))
            {
                excluded.Add(item);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            onError(ex);
        }

        return excluded;
    }
}
