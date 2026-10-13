namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     Represents a single Radarr or Sonarr instance configuration.
/// </summary>
public class ArrInstanceConfig
{
    /// <summary>
    ///     Gets or sets the display name for this instance (e.g. "Radarr 4K", "Sonarr Anime").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the base URL (e.g., http://localhost:7878).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the comma-separated Jellyfin library names this instance manages, used to scope the Arr-tab comparison.
    ///     An empty value means the libraries are matched automatically from the instance's Radarr/Sonarr root folders; a non-empty
    ///     value is a manual override. Stored as a comma-separated string (not a list) to keep XML/JSON round-trip parity with the
    ///     existing ExcludedLibraries field.
    /// </summary>
    public string Libraries { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether TLS certificate validation is skipped for this instance.
    ///     Needed for servers behind a reverse proxy with a private CA, self-signed, or IP certificate.
    ///     Only enable on networks you trust: without validation anyone intercepting the connection can read
    ///     the API key. Defaults to false (validated).
    /// </summary>
    public bool SkipCertificateValidation { get; set; }
}