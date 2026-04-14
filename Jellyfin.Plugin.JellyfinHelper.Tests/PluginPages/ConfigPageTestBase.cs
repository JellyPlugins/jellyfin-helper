using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Shared base class for all configPage.html tests.
/// Loads the composed embedded HTML resource once and makes it available to subclasses.
/// </summary>
public abstract class ConfigPageTestBase
{
    /// <summary>
    /// Gets the full HTML content of the composed configPage.html embedded resource.
    /// </summary>
    protected static readonly string HtmlContent = LoadConfigPageHtml();

    private static string LoadConfigPageHtml()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("configPage.html", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            throw new InvalidOperationException("configPage.html embedded resource not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Gets the content of the README.md file from the repository root.
    /// </summary>
    protected static readonly string ReadmeContent = LoadReadme();

    private static string LoadReadme()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "README.md");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return string.Empty;
    }
}
