using System;
using System.Text.RegularExpressions;

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
        if (string.IsNullOrEmpty(content.Contents))
        {
            return content.Contents ?? string.Empty;
        }

        var pluginName = "Jellyfin Helper";
        var pluginVersion = Plugin.Instance?.Version.ToString() ?? "unknown";

        var scriptUrl = $"../JellyfinHelper/Discovery/My/script?v={pluginVersion}";
        var scriptTag = $"<script plugin=\"{pluginName}\" version=\"{pluginVersion}\" src=\"{scriptUrl}\" defer></script>";

        // Remove any old versions of the script tag first
        var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(pluginName)}[\"'][^>]*>\\s*</script>\\n?");
        var updatedContent = regex.Replace(content.Contents, string.Empty);

        // Inject the new script tag before </body>
        if (updatedContent.Contains("</body>", StringComparison.Ordinal))
        {
            return updatedContent.Replace("</body>", $"{scriptTag}\n</body>", StringComparison.Ordinal);
        }

        return updatedContent;
    }
}
