namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request model for testing an Arr connection.
/// </summary>
public class ArrTestConnectionRequest
{
    /// <summary>
    ///     Gets the display name of the instance being tested. Optional; used only to disambiguate
    ///     the stored key when the API key is the masked sentinel and two stored instances share the
    ///     same URL. Ignored when a real (non-mask) key is supplied.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Gets the base URL of the Arr instance.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    ///     Gets the API key. May be the masked sentinel (<see cref="ConfigurationResponse.ApiKeyMask"/>)
    ///     when the client is testing an already-stored key without changing it; in that case the real
    ///     key is resolved server-side from the persisted configuration and the mask is never forwarded.
    /// </summary>
    public string? ApiKey { get; init; }
}