using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Token payload returned by Trakt POST /oauth/device/token (on approval) and POST /oauth/token (refresh).
/// </summary>
public sealed class TraktTokenResponse
{
    /// <summary>Gets or sets the OAuth access token.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the OAuth refresh token.</summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the access-token lifetime in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>Gets or sets the Unix timestamp (seconds) at which the token was created.</summary>
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}
