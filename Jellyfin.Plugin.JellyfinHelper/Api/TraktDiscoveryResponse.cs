using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Envelope for the personal Trakt endpoint: distinguishes "not linked" (show the connect panel) from a
///     scored result, so the client never confuses an empty link state with an empty recommendation list.
/// </summary>
public sealed class TraktDiscoveryResponse
{
    /// <summary>Gets or sets a value indicating whether the user has linked Trakt.</summary>
    public bool Linked { get; set; }

    /// <summary>Gets or sets the scored result when linked; null otherwise.</summary>
    public DiscoveryResult? Result { get; set; }
}
