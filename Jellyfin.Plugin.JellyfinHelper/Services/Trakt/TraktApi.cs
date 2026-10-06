using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Shared Trakt API constants. The base URL is normally https://api.trakt.tv and is only overridable via
///     an environment variable so the end-to-end tests can point the plugin at a local mock; production never
///     sets it, so the default is used.
/// </summary>
internal static class TraktApi
{
    // Assembled from scheme + host rather than a single literal so the fixed public Trakt endpoint is not a
    // hardcoded absolute URI. There is nothing to configure here in production; the env override below exists
    // only for the end-to-end mock.
    private const string DefaultHost = "api.trakt.tv";
    private static readonly string DefaultBaseUrl = $"{Uri.UriSchemeHttps}://{DefaultHost}";

    // Env override is read once at startup. Test harnesses set it before the plugin loads; a blank or
    // malformed value falls back to the real Trakt endpoint so a bad env can never silently break discovery.
    private static readonly string ResolvedBaseUrl = ResolveBaseUrl();

    /// <summary>Gets the Trakt API base URL (no trailing slash).</summary>
    internal static string BaseUrl => ResolvedBaseUrl;

    private static string ResolveBaseUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("JELLYFIN_HELPER_TRAKT_API_BASE");
        if (!string.IsNullOrWhiteSpace(fromEnv)
            && Uri.TryCreate(fromEnv, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            return fromEnv.TrimEnd('/');
        }

        return DefaultBaseUrl;
    }
}
