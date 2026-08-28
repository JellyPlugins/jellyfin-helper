namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Arr instance view model used inside ConfigurationResponse. The ApiKey field contains ApiKeyMask whenever a real key is stored; empty string when no key has been configured.
/// </summary>
public sealed class MaskedArrInstanceConfig
{
    /// <summary>Gets the display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the base URL.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets the masked API key placeholder.</summary>
    public string ApiKey { get; init; } = string.Empty;
}
