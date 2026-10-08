using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;

/// <summary>
///     A read-only snapshot of an OAuth token the official Trakt plugin holds for one Jellyfin user. This is the
///     Helper's OWN type: it never references the foreign plugin's assembly. The token is used to source
///     recommendations through the official plugin's app and is never persisted or refreshed by the Helper.
/// </summary>
/// <param name="AccessToken">The bearer access token. Never empty when this record is produced.</param>
/// <param name="RefreshToken">
///     The refresh token, or an empty string when absent. Retained for completeness only; the Helper never uses
///     it to refresh (the official plugin owns the single-use rotation).
/// </param>
/// <param name="AccessTokenExpiration">The token's UTC expiry. The reader only returns unexpired tokens.</param>
public sealed record OfficialTraktToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiration);
