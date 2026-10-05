namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Response body for the Trakt device-poll endpoint, carrying the poll outcome as a string the client
///     switches on (Pending/Linked/Expired/Denied/Error).
/// </summary>
public sealed class TraktDevicePollResponse
{
    /// <summary>Gets or sets the poll status name.</summary>
    public string Status { get; set; } = string.Empty;
}
