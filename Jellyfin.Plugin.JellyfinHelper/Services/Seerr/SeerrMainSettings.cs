using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
///     The main settings response from the Seerr API (used for connection testing).
/// </summary>
internal sealed class SeerrMainSettings
{
    /// <summary>
    ///     Gets or sets the application title.
    /// </summary>
    [JsonPropertyName("applicationTitle")]
    public string ApplicationTitle { get; set; } = string.Empty;
}