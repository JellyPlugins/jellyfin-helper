using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Static transformation callbacks invoked by the File Transformation plugin.
///     These methods receive the current file content and return the transformed version.
/// </summary>
public static class TransformationPatches
{
    /// <summary>
    ///     Transforms Jellyfin's index.html to include the Discovery sidebar script tag.
    ///     Called by the File Transformation plugin whenever index.html is served.
    /// </summary>
    /// <param name="content">The patch request payload containing the current index.html content.</param>
    /// <returns>The transformed content with the script tag injected before &lt;/body&gt;.</returns>
    public static string IndexHtml(PatchRequestPayload content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(content.Contents))
        {
            return content.Contents ?? string.Empty;
        }

        var pluginVersion = Plugin.Instance?.Version.ToString() ?? "unknown";
        var scriptTag = DiscoveryScriptTag.Build(pluginVersion);

        // Remove any old versions of the script tag first
        var updatedContent = DiscoveryScriptTag.RemovalRegex.Replace(content.Contents, string.Empty);

        // Inject the new script tag before the LAST </body> (case-insensitive for robustness).
        // Search is bounded to content before </html> so a literal </body> inside a <template>
        // or framework fragment (Vue, Lit, Svelte) does not cause injection into a non-executing
        // DOM region.
        var htmlClose = updatedContent.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        var searchEnd = htmlClose >= 0 ? htmlClose : updatedContent.Length;
        var bodyIndex = updatedContent.LastIndexOf("</body>", searchEnd, StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
        {
            return updatedContent.Insert(bodyIndex, $"{scriptTag}\n");
        }

        return updatedContent;
    }
}
