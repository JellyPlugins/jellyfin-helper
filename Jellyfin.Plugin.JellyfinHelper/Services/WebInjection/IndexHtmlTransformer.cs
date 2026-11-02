using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;

/// <summary>
///     Pure index.html transformation used by the request-time injector and the legacy on-disk cleanup.
/// </summary>
internal static class IndexHtmlTransformer
{
    private const string BodyCloseTag = "</body>";

    /// <summary>
    ///     Determines whether the document already references the Discovery script, in any version.
    /// </summary>
    /// <param name="html">The current index.html content.</param>
    /// <returns><c>true</c> when our script tag is present.</returns>
    public static bool ContainsScriptTag(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        return DiscoveryScriptTag.RemovalRegex.IsMatch(html)
            || html.Contains(DiscoveryScriptTag.ScriptBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Inserts the script tag before the closing body tag, replacing any older tag of ours first.
    /// </summary>
    /// <param name="html">The current index.html content.</param>
    /// <param name="scriptTag">The tag to insert.</param>
    /// <param name="result">The transformed document, or the input when the method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the document has no closing body tag to anchor to.</returns>
    public static bool TryInject(string html, string scriptTag, out string result)
    {
        var withoutOldTag = Remove(html);
        var bodyCloseIndex = withoutOldTag.LastIndexOf(BodyCloseTag, StringComparison.OrdinalIgnoreCase);
        if (bodyCloseIndex < 0)
        {
            result = html;
            return false;
        }

        result = withoutOldTag.Insert(bodyCloseIndex, scriptTag + "\n");
        return true;
    }

    /// <summary>
    ///     Removes every script tag this plugin injected, including tags written by older versions.
    /// </summary>
    /// <param name="html">The current index.html content.</param>
    /// <returns>The document without our script tag.</returns>
    public static string Remove(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? string.Empty;
        }

        return DiscoveryScriptTag.RemovalRegex.Replace(html, string.Empty);
    }
}
