namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Request model for Seerr connection test.
/// </summary>
public class SeerrTestRequest
{
    /// <summary>
    ///     Gets or sets the Seerr instance URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the Seerr API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether TLS certificate validation is skipped for this test.
    ///     Nullable so an absent field is distinguishable from an explicit false; absent means false.
    /// </summary>
    public bool? SkipCertificateValidation { get; set; }
}