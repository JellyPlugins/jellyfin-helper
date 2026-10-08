namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     The outcome of a single device-token poll, mapping Trakt's documented status codes to a small set the
///     controller and client can act on.
/// </summary>
public enum TraktDevicePollStatus
{
    /// <summary>The user has not yet approved the code; keep polling (Trakt 400).</summary>
    Pending,

    /// <summary>The client is polling too fast; keep polling but slow down (Trakt 429).</summary>
    SlowDown,

    /// <summary>The user approved the code; a token was issued and stored (Trakt 200).</summary>
    Linked,

    /// <summary>The device code expired before approval; the client must restart (Trakt 410).</summary>
    Expired,

    /// <summary>The user explicitly denied the request (Trakt 418).</summary>
    Denied,

    /// <summary>The poll failed for a transient or unexpected reason; the client may retry or restart.</summary>
    Error,
}
