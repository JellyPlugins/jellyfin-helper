namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     DTO for polling a Trakt device-code authorization. A missing code is treated as empty
///     so stale retries fail closed instead of breaking the client's polling loop.
/// </summary>
public sealed class TraktDevicePollRequest
{
    /// <summary>
    ///     Gets or sets the device code returned by the start step.
    /// </summary>
    public string? DeviceCode { get; set; }
}
