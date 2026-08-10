namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/UserDiscovery/SeerrUrl.</summary>
public sealed class SeerrUrlResponse
{
    /// <summary>Gets or sets the configured Seerr base URL.</summary>
    public string SeerrUrl { get; set; } = string.Empty;
}
