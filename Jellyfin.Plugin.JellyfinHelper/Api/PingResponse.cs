namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/Ping.</summary>
public sealed class PingResponse
{
    /// <summary>Gets or sets a value indicating whether the plugin is alive.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the plugin identifier.</summary>
    public string Plugin { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin version string.</summary>
    public string Version { get; set; } = string.Empty;
}
