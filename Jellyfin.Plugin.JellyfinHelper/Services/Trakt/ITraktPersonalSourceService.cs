using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Owns the per-user Trakt personal-source lifecycle: the user's own device-flow link first, the official
///     Trakt plugin's token as fallback. Extracted from the discovery orchestration so that service stays within
///     its constructor budget and every own-vs-official precedence rule lives in exactly one place.
/// </summary>
public interface ITraktPersonalSourceService
{
    /// <summary>
    ///     Returns whether Trakt is sourceable at all on this server: either own client-id credentials are
    ///     stored (the derived <c>TraktEnabled</c> flag), or the official Trakt plugin is present and can supply
    ///     per-user tokens. Gates the trending list and the refresh pass, which need no per-user link.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns><see langword="true"/> when either source population can be served.</returns>
    bool IsAvailable(PluginConfiguration config);

    /// <summary>
    ///     Picks the per-user personal source for the fetch path, own-link first then the official plugin.
    ///     Own-precedence is strict: an own-linked user whose own token is currently unavailable (transient
    ///     refresh failure, revoked grant) resolves to null rather than falling back to the official token, so
    ///     a transient own-token outage never silently switches the user's source. For the own path the token
    ///     is read via the auth service (a mid-flight rotation is picked up on the next call); for the official
    ///     path the token is read-only from the foreign config and never refreshed.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="config">The current plugin configuration (own client id).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The source to fetch with, or null when neither source is usable for this user.</returns>
    Task<TraktPersonalSource?> ResolveAsync(Guid userId, PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    ///     Answers "is this user linked?" without forcing a token refresh. A link-status check runs on every tab
    ///     load and status poll; calling the auth service here would hit the network and rotate the single-use
    ///     refresh token just to answer a boolean. Reads link state cheaply instead: the own store plus the
    ///     official plugin's usable token (a pure file read).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="config">The current plugin configuration (own client id).</param>
    /// <returns><see langword="true"/> when a personal source is usable for the user.</returns>
    bool IsLinked(Guid userId, PluginConfiguration config);

    /// <summary>
    ///     Enumerates every user id warmable through either source: users linked via the own device flow plus
    ///     users the official plugin holds a usable token for (absent from the own store). Deduped so a user
    ///     linked both ways is warmed once.
    /// </summary>
    /// <returns>The warmable user ids (possibly empty).</returns>
    IReadOnlyCollection<Guid> GetLinkedUserIds();

    /// <summary>
    ///     Forces an own-flow refresh from the stored refresh token, bypassing the expiry check. Used when Trakt
    ///     rejects the current own access token mid-flight. Never touches the official plugin's token.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="rejectedAccessToken">The access token Trakt rejected, or null when unknown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new access token, or null when no refresh was possible.</returns>
    Task<string?> RefreshOwnTokenAsync(Guid userId, string? rejectedAccessToken, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes a user's own-flow token (dead grant). Never touches the official plugin's token.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the token has been removed.</returns>
    Task UnlinkOwnAsync(Guid userId, CancellationToken cancellationToken);
}
