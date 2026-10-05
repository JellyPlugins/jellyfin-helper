using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     The persisted Trakt OAuth state for a single Jellyfin user. Access and refresh tokens are encrypted
///     at rest by <see cref="ITraktUserStore"/>; this record carries them in whatever form the store holds
///     (ciphertext on disk, plaintext in memory after a decrypt).
/// </summary>
public sealed class TraktUserToken
{
    /// <summary>
    ///     Gets or sets the OAuth access token used to authorize personal Trakt calls.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the OAuth refresh token used to obtain a new access token when the current one expires.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the absolute UTC time at which the access token expires. A call made after this instant
    ///     refreshes first.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    ///     Gets a value indicating whether a usable token pair is present. A record with either token missing is
    ///     treated as unlinked so the UI shows the connect panel.
    /// </summary>
    [JsonIgnore]
    public bool IsLinked => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);
}
