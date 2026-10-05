using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Response envelope for the current user's personal Trakt recommendations. When the user has not linked
///     Trakt the response carries Linked=false (so the UI shows the connect panel); otherwise it carries the
///     scored discovery result.
/// </summary>
public sealed class TraktDiscoveryResponse
{
    /// <summary>
    ///     Gets or sets a value indicating whether the current user has linked Trakt.
    /// </summary>
    public bool Linked { get; set; }

    /// <summary>
    ///     Gets or sets the scored discovery result. Null when the user has not linked Trakt.
    /// </summary>
    public DiscoveryResult? Result { get; set; }
}
