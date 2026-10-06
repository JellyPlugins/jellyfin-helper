namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request model for the Trakt client-id connection test.
/// </summary>
public class TraktTestRequest
{
    /// <summary>
    ///     Gets or sets the Trakt Client ID to validate. The client id is not a secret and is always
    ///     sent and returned as-is; there is no masked sentinel for it.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}
