using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Represents a single Arr instance in the backup data. Mirrors ArrInstanceConfig but as a plain DTO for safe deserialization and validation.
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

    /// <summary>
    /// Gets or sets the comma-separated assigned library names.
    /// </summary>
    [JsonPropertyName("libraries")]
    public string Libraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether TLS certificate validation is skipped for this instance.
    /// Nullable so an older backup that omits the field is distinguishable from an explicit false and
    /// can fall back to the live instance value on restore instead of silently re-enabling validation.
    /// </summary>
    [JsonPropertyName("skipCertificateValidation")]
    public bool? SkipCertificateValidation { get; set; }
}
