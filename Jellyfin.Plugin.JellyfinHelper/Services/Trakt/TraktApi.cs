using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Shared Trakt API constants. The base URL is normally https://api.trakt.tv and is only overridable via
///     an environment variable so the end-to-end tests can point the plugin at a local mock; production never
///     sets it, so the default is used.
/// </summary>
internal static class TraktApi
{
    private const string DefaultBaseUrl = "https://api.trakt.tv";

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
