using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Owns the per-user Trakt personal-source lifecycle, sourced from the official Jellyfin Trakt plugin's
///     token (strictly read-only). Extracted from the discovery orchestration so that service stays within its
///     constructor budget and all source resolution lives in one place.
/// </summary>
public interface ITraktPersonalSourceService
{
    /// <summary>
    ///     Returns whether Trakt is sourceable at all on this server: the official Trakt plugin is present and can
    ///     supply per-user tokens. Gates the trending list and the refresh pass, which need no per-user link.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns><see langword="true"/> when the source population can be served.</returns>
    bool IsAvailable(PluginConfiguration config);

    /// <summary>
    ///     Picks the per-user personal source for the fetch path from the official plugin's token. The token is
    ///     read-only from the foreign config and never refreshed.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The source to fetch with, or null when no source is usable for this user.</returns>
    Task<TraktPersonalSource?> ResolveAsync(Guid userId, PluginConfiguration config, CancellationToken cancellationToken);

    /// <summary>
    ///     Answers "is this user linked?" without a network call: the official plugin holds a usable token (a pure
    ///     file read).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns><see langword="true"/> when a personal source is usable for the user.</returns>
    bool IsLinked(Guid userId, PluginConfiguration config);

    /// <summary>
    ///     Enumerates every user id the official plugin holds a usable token for. Deduped.
    /// </summary>
    /// <returns>The warmable user ids (possibly empty).</returns>
    IReadOnlyCollection<Guid> GetLinkedUserIds();
}
