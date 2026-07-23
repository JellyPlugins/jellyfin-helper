namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for connection-test endpoints (Arr, Seerr).</summary>
public sealed class ConnectionTestResponse
{
    /// <summary>Gets or sets a value indicating whether the connection test succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the human-readable result message.</summary>
    public string Message { get; set; } = string.Empty;
}
