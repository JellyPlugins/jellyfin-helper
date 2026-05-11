using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a configured Radarr/Sonarr server in Seerr with its quality profiles and root folders.
/// </summary>
public sealed class SeerrServiceInfo
{
    /// <summary>
    ///     Gets or sets the Seerr-internal server ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the server name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether this is the default server.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this is the default 4K server.
    /// </summary>
    [JsonPropertyName("is4k")]
    public bool Is4k { get; set; }

    /// <summary>
    ///     Gets or sets the active profile ID configured for this server.
    /// </summary>
    [JsonPropertyName("activeProfileId")]
    public int ActiveProfileId { get; set; }

    /// <summary>
    ///     Gets or sets the active root folder configured for this server.
    /// </summary>
    [JsonPropertyName("activeDirectory")]
    public string ActiveDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the available quality profiles for this server.
    /// </summary>
    [JsonPropertyName("profiles")]
    public Collection<SeerrQualityProfile> Profiles { get; set; } = [];

    /// <summary>
    ///     Gets or sets the available root folders for this server.
    /// </summary>
    [JsonPropertyName("rootFolders")]
    public Collection<SeerrRootFolder> RootFolders { get; set; } = [];
}