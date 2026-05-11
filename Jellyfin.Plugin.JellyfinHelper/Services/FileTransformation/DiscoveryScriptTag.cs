using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Single source of truth for the Discovery sidebar script tag construction and removal.
///     Used by both <see cref="TransformationPatches"/> (on-the-fly serving) and
///     <see cref="Plugin"/> (direct index.html write fallback).
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
    ///     Compiled regex for removing any existing script tags injected by this plugin.
    ///     Matches tags with plugin="Jellyfin Helper" regardless of version, URL, or attributes.
    /// </summary>
    public static readonly Regex RemovalRegex = new(
        "<script[^>]*plugin=[\"']" + Regex.Escape(PluginName) + "[\"'][^>]*>\\s*</script>\\n?",
        RegexOptions.Compiled);

    /// <summary>
    ///     Builds the full HTML script tag for injection into index.html.
    /// </summary>
    /// <param name="version">The plugin version string (used for cache-busting).</param>
    /// <returns>A complete HTML script tag string.</returns>
    public static string Build(string version)
        => $"<script plugin=\"{PluginName}\" version=\"{version}\" src=\"{ScriptBaseUrl}?v={version}\" defer></script>";
}