using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Response from Trakt POST /oauth/device/code: the codes and polling parameters a user needs to link.
/// </summary>
public sealed class TraktDeviceCodeResponse
{
    /// <summary>Gets or sets the device code the server polls with. Never shown to the user.</summary>
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the short user code the user enters at the verification URL.</summary>
    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL the user visits to enter the user code.</summary>
    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the lifetime of the device code in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>Gets or sets the minimum seconds the client must wait between poll attempts.</summary>
    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}
