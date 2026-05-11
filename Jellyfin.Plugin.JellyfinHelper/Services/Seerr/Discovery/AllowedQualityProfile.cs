namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a single quality profile that the user is allowed to select.
/// </summary>
public sealed class AllowedQualityProfile
{
    /// <summary>
    ///     Gets or sets the Seerr-internal server ID (Radarr/Sonarr).
    /// </summary>
    public int ServerId { get; set; }

    /// <summary>
    ///     Gets or sets the display name of the server.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the quality profile ID on the server.
    /// </summary>
    public int ProfileId { get; set; }

    /// <summary>
    ///     Gets or sets the display name of the quality profile.
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether this is the server's default/active profile.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    ///     Gets or sets the root folder path associated with this server configuration.
    /// </summary>
    public string RootFolder { get; set; } = string.Empty;
}