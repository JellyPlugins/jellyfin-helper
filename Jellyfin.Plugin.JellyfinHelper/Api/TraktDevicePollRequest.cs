namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for polling a Trakt device-code authorization. A missing code is treated as empty by the
///     controller (the Trakt poll then fails closed) rather than rejected, so retries with a stale body
///     cannot break the client out of its polling loop.
/// </summary>
public sealed class TraktDevicePollRequest
{
    /// <summary>
    ///     Gets or sets the device code returned by the start step.
    /// </summary>
    public string? DeviceCode { get; set; }
}
