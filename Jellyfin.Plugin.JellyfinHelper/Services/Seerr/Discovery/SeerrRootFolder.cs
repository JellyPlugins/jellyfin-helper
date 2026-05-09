using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a root folder on a Radarr/Sonarr server.
/// </summary>
public sealed class SeerrRootFolder
{
    /// <summary>
    ///     Gets or sets the root folder ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the root folder path.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}