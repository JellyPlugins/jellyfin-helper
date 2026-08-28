using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Single source of truth for the Discovery sidebar script tag construction and removal.
/// </summary>
internal static class DiscoveryScriptTag
{
    /// <summary>
    ///     The plugin display name used in the script tag's plugin attribute.
    /// </summary>
    public const string PluginName = "Jellyfin Helper";

    /// <summary>
    ///     The relative URL to the discovery sidebar script endpoint.
    /// </summary>
    public const string ScriptBaseUrl = "../JellyfinHelper/Discovery/My/script";

    /// <summary>
    ///     Compiled regex for removing any existing script tags injected by this plugin. Matches tags with plugin="Jellyfin Helper" regardless of version, URL, or attributes.
    /// </summary>
    public static readonly Regex RemovalRegex = new(
        "<script[^>]*plugin=[\"']" + Regex.Escape(PluginName) + "[\"'][^>]*>\\s*</script>\\r?\\n?",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>
    ///     Builds the full HTML script tag for injection into index.html.
    /// </summary>
    /// <param name="version">The plugin version string (used for cache-busting).</param>
    /// <returns>A complete HTML script tag string.</returns>
    public static string Build(string version)
    {
        var safeVersion = Uri.EscapeDataString(version ?? string.Empty);
        return $"<script plugin=\"{PluginName}\" version=\"{safeVersion}\" src=\"{ScriptBaseUrl}?v={safeVersion}\" defer></script>";
    }
}