using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;

/// <summary>
///     Reads a per-user OAuth token out of the official Jellyfin Trakt plugin's persisted configuration, so the
///     Helper can source Trakt recommendations through that plugin's single registered app rather than registering
///     a competing second app. All knowledge of the foreign plugin's layout is confined to the implementation.
/// </summary>
public interface IOfficialTraktPluginReader
{
    /// <summary>
    ///     Gets a value indicating whether the official Trakt plugin is installed and active. Never throws.
    /// </summary>
    /// <returns><see langword="true"/> when the official plugin is present and active; otherwise <see langword="false"/>.</returns>
    bool IsPresent();

    /// <summary>
    ///     Attempts to read a usable, non-expired access token the official Trakt plugin holds for the given
    ///     Jellyfin user. Strictly read-only: never refreshes or writes anything back.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user id to match against the foreign plugin's linked users.</param>
    /// <param name="now">The current instant (absolute <see cref="DateTimeOffset"/>), used to skip expired tokens.</param>
    /// <returns>
    ///     The token when the user is linked in the official plugin and the token is present and unexpired;
    ///     otherwise <see langword="null"/> (caller falls back to the Helper's own-client-id flow).
    /// </returns>
    OfficialTraktToken? TryGetToken(Guid jellyfinUserId, DateTimeOffset now);

    /// <summary>
    ///     Enumerates the Jellyfin user ids the official Trakt plugin currently holds a usable, non-expired token
    ///     for. Used to pre-warm the Helper's per-user recommendation cache for users who are sourced only through
    ///     the official plugin (and so are absent from the Helper's own token store). Strictly read-only; never
    ///     throws (returns an empty list on any read/parse failure).
    /// </summary>
    /// <param name="now">The current instant (absolute <see cref="DateTimeOffset"/>), used to skip expired tokens.</param>
    /// <returns>The linked, non-expired user ids; empty when the plugin is absent or nothing is usable.</returns>
    IReadOnlyList<Guid> GetLinkedUserIds(DateTimeOffset now);
}
