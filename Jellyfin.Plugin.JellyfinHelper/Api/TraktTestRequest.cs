namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request model for the Trakt client-id connection test.
/// </summary>
public class TraktTestRequest
{
    /// <summary>
    ///     Gets or sets the Trakt Client ID to validate. The masked sentinel is resolved to the stored value.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}
