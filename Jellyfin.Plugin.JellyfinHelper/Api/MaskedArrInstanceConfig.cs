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

    /// <summary>Gets the comma-separated assigned library names (empty means automatic root-folder matching).</summary>
    public string Libraries { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether TLS certificate validation is skipped for this instance.</summary>
    public bool SkipCertificateValidation { get; init; }
}
