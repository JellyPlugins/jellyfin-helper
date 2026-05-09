using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a quality profile available on a Radarr/Sonarr server.
/// </summary>
public sealed class SeerrQualityProfile
{
    /// <summary>
    ///     Gets or sets the profile ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the profile name (e.g. "4K", "1080p", "Anime").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}