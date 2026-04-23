using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
///     The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;

    /// <summary>
    ///     Initializes a new instance of the <see cref="Plugin" /> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths" /> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer" /> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Helper";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("0c737645-5cbb-4bd8-80c7-d377b560aaa4");

    /// <inheritdoc />
    public override string Description =>
        "Automated cleanup (trickplay, empty folders, subtitles, link repair), media statistics, ML-powered smart recommendations, user activity insights, trash bin, Arr/Seerr integration.";

    /// <summary>
    ///     Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        CleanupDataFiles();
        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Jellyfin Helper",
                EnableInMainMenu = true,
                MenuIcon = "handyman",
                EmbeddedResourcePath = GetType().Namespace + ".PluginPages.configPage.html"
            }
        ];
    }

    /// <summary>
    ///     Deletes all persistent data files created by this plugin from the Jellyfin data directory.
    ///     All plugin data files follow the naming convention <c>jellyfin-helper-*.json</c>:
    ///     <list type="bullet">
    ///         <item><c>jellyfin-helper-statistics-latest.json</c> — media statistics cache</item>
    ///         <item><c>jellyfin-helper-recommendations-latest.json</c> — recommendation results cache</item>
    ///         <item><c>jellyfin-helper-useractivity-latest.json</c> — user activity insights cache</item>
    ///         <item><c>jellyfin-helper-growth-timeline.json</c> — library growth timeline data</item>
    ///         <item><c>jellyfin-helper-growth-baseline.json</c> — library growth baseline snapshot</item>
    ///     </list>
    ///     Also removes any leftover <c>.tmp</c> files from atomic write operations.
    /// </summary>
    private void CleanupDataFiles()
    {
        try
        {
            var dataPath = _applicationPaths.DataPath;
            if (!Directory.Exists(dataPath))
            {
                return;
            }

            // Match all files created by this plugin: jellyfin-helper-*
            // Only delete known extensions (.json data files and .tmp atomic-write leftovers)
            // to avoid accidental deletion of unrelated files sharing the prefix.
            foreach (var file in Directory.GetFiles(dataPath, "jellyfin-helper-*"))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort — file may be locked or permission-restricted.
                    // Skip and continue with the next file.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — if the data directory is inaccessible, nothing we can do.
        }
    }
}