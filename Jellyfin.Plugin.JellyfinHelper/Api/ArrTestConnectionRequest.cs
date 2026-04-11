namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// Request model for testing an Arr connection.
/// </summary>
public class ArrTestConnectionRequest
{
    /// <summary>
    /// Gets or sets the base URL of the Arr instance.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string? ApiKey { get; set; }
}