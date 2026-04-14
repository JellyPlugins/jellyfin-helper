using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Represents a single Arr instance in the backup data.
/// Mirrors <see cref="Configuration.ArrInstanceConfig"/> but as a plain DTO
/// for safe deserialization and validation.
/// </summary>
public class BackupArrInstance
{
    /// <summary>
    /// Gets or sets the display name for this instance.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;
}
