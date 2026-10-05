namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request body for the Trakt device-poll endpoint.
/// </summary>
public sealed class TraktDevicePollRequest
{
    /// <summary>Gets or sets the device code returned by the device-start endpoint.</summary>
    public string? DeviceCode { get; set; }
}
