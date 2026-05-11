namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents the interaction status of a user with a discovery recommendation.
///     Ordered by signal strength (weakest to strongest).
/// </summary>
public enum DiscoveryInteractionStatus
{
    /// <summary>
    ///     The item was shown to the user but no action was taken.
    ///     Weak negative signal (exposure without engagement).
    /// </summary>
    Shown = 0,

    /// <summary>
    ///     The user explicitly dismissed/ignored the item.
    ///     Stronger negative signal than mere exposure.
    /// </summary>
    Dismissed = 1,

    /// <summary>
    ///     The user requested the item via Seerr.
    ///     Strong positive signal (explicit interest).
    /// </summary>
    Requested = 2,

    /// <summary>
    ///     The user requested the item AND later watched it after it was added to the library.
    ///     Strongest positive signal (interest confirmed by consumption).
    /// </summary>
    RequestedAndWatched = 3
}