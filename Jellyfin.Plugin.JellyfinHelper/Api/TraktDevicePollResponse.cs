namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Status envelope for a Trakt device-code poll. The client polls while pending
///     and restarts with a fresh code once expired.
/// </summary>
public sealed class TraktDevicePollResponse
{
    /// <summary>
    ///     Gets or sets the poll status (a <see cref="Services.Trakt.TraktDevicePollStatus"/> name).
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
