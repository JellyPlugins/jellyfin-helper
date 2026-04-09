namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
/// Represents a single Radarr or Sonarr instance configuration.
/// </summary>
public class ArrInstanceConfig
{
    /// <summary>
    /// Gets or sets the display name for this instance (e.g. "Radarr 4K", "Sonarr Anime").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL (e.g., http://localhost:7878).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}