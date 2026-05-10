using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
///     The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="Plugin" /> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths" /> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer" /> interface.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        _logger = logger;
        InjectScript();
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

    /// <summary>
    ///     Gets the path to Jellyfin's web UI index.html file.
    /// </summary>
    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        UpdateIndexHtml(false);
        CleanupDataFiles();
        CleanupRecommendationPlaylists();
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
            },
        ];
    }

    /// <summary>
    ///     Injects the discovery sidebar script tag into Jellyfin's index.html.
    ///     Tries the File Transformation plugin first (on-the-fly, no filesystem write needed).
    ///     Falls back to direct index.html modification if File Transformation is not installed.
    /// </summary>
    internal void InjectScript()
    {
        if (RegisterFileTransformation())
        {
            _logger.LogDebug("[Discovery Sidebar] Registered with File Transformation plugin — no direct file write needed");
        }
        else
        {
            _logger.LogDebug("[Discovery Sidebar] File Transformation plugin not found, using fallback (direct index.html write)");
            UpdateIndexHtml(true);
        }
    }

    /// <summary>
    ///     Attempts to register the script injection with the File Transformation plugin.
    ///     This plugin intercepts file serving and transforms content on-the-fly,
    ///     avoiding the need to write to the read-only filesystem in Docker containers.
    /// </summary>
    /// <returns>True if registration succeeded, false if the plugin is not available.</returns>
    private bool RegisterFileTransformation()
    {
        try
        {
            var fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            if (fileTransformationAssembly == null)
            {
                return false;
            }

            var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType == null)
            {
                _logger.LogWarning("[Discovery Sidebar] FileTransformation assembly found but PluginInterface type missing");
                return false;
            }

            // The File Transformation plugin expects a Newtonsoft.Json JObject payload.
            // We construct it via reflection to avoid adding a Newtonsoft.Json package dependency
            // (it's available at runtime as a transitive dependency of Jellyfin).
            var newtonsoftAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.GetName().Name == "Newtonsoft.Json");

            if (newtonsoftAssembly == null)
            {
                _logger.LogWarning("[Discovery Sidebar] Newtonsoft.Json not found at runtime");
                return false;
            }

            var jObjectType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JObject");
            if (jObjectType == null)
            {
                return false;
            }

            var payload = Activator.CreateInstance(jObjectType);
            var jTokenType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JToken")!;
            var addMethod = jObjectType.GetMethod("Add", new Type[] { typeof(string), jTokenType });
            var jValueType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JValue");

            if (payload == null || addMethod == null || jValueType == null)
            {
                return false;
            }

            object CreateJValue(string val) => Activator.CreateInstance(jValueType, new object[] { val })!;

            addMethod.Invoke(payload, new[] { "id", CreateJValue(Id.ToString()) });
            addMethod.Invoke(payload, new[] { "fileNamePattern", CreateJValue("index.html") });
            addMethod.Invoke(payload, new[] { "callbackAssembly", CreateJValue(GetType().Assembly.FullName ?? string.Empty) });
            addMethod.Invoke(payload, new[] { "callbackClass", CreateJValue(typeof(TransformationPatches).FullName ?? string.Empty) });
            addMethod.Invoke(payload, new[] { "callbackMethod", CreateJValue(nameof(TransformationPatches.IndexHtml)) });

            pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new[] { payload });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Discovery Sidebar] Failed to register with File Transformation plugin");
            return false;
        }
    }

    /// <summary>
    ///     Adds or removes the discovery sidebar script tag from Jellyfin's index.html.
    ///     When <paramref name="inject"/> is true, any old version of the tag is replaced with the current one.
    ///     When false, the tag is removed entirely (used during uninstall).
    /// </summary>
    /// <param name="inject">Whether to inject (true) or remove (false) the script tag.</param>
    internal void UpdateIndexHtml(bool inject)
    {
        try
        {
            var indexPath = IndexHtmlPath;
            _logger.LogDebug("[Discovery Sidebar] WebPath = {WebPath}", _applicationPaths.WebPath);
            _logger.LogDebug("[Discovery Sidebar] IndexHtmlPath = {IndexPath}", indexPath);

            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("[Discovery Sidebar] index.html NOT FOUND at {IndexPath}", indexPath);
                return;
            }

            _logger.LogDebug("[Discovery Sidebar] index.html found, reading content...");
            var originalContent = File.ReadAllText(indexPath);
            var content = originalContent;
            const string scriptUrl = "../JellyfinHelper/Discovery/My/script";
            var scriptTag = $"<script plugin=\"{Name}\" version=\"{Version}\" src=\"{scriptUrl}\" defer></script>";
            var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(Name)}[\"'][^>]*>\\s*</script>\\n?");

            // Remove any old versions of the script tag first
            content = regex.Replace(content, string.Empty);

            if (inject)
            {
                var closingBodyTag = "</body>";
                if (content.Contains(closingBodyTag, StringComparison.Ordinal))
                {
                    content = content.Replace(closingBodyTag, $"{scriptTag}\n{closingBodyTag}", StringComparison.Ordinal);
                    _logger.LogDebug("[Discovery Sidebar] Script tag injected successfully before </body>");
                }
                else
                {
                    _logger.LogWarning("[Discovery Sidebar] Could not find </body> tag in index.html");
                    return;
                }
            }
            else
            {
                _logger.LogDebug("[Discovery Sidebar] Removing script tag from index.html");
            }

            if (!string.Equals(content, originalContent, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, content);
                _logger.LogDebug("[Discovery Sidebar] index.html written successfully");
            }
            else
            {
                _logger.LogDebug("[Discovery Sidebar] index.html already up to date; skipping write");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogError(ex, "[Discovery Sidebar] Failed to update index.html");
        }
    }

    /// <summary>
    ///     Deletes all persistent data files created by this plugin from the Jellyfin data directory.
    ///     All plugin data files follow the naming convention <c>jellyfin-helper-*.json</c>:
    ///     <list type="bullet">
    ///         <item><c>jellyfin-helper-statistics-latest.json</c> - media statistics cache</item>
    ///         <item><c>jellyfin-helper-recommendations-latest.json</c> - recommendation results cache</item>
    ///         <item><c>jellyfin-helper-useractivity-latest.json</c> - user activity insights cache</item>
    ///         <item><c>jellyfin-helper-growth-timeline.json</c> - library growth timeline data</item>
    ///         <item><c>jellyfin-helper-growth-baseline.json</c> - library growth baseline snapshot</item>
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
                    // Best effort - file may be locked or permission-restricted.
                    // Skip and continue with the next file.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - if the data directory is inaccessible, nothing we can do.
        }
    }

    /// <summary>
    ///     Removes all recommendation playlist folders created by this plugin.
    ///     Jellyfin stores playlists as subdirectories under <c>{DataPath}/playlists/</c>.
    ///     Managed playlists are identified by the
    ///     <see cref="RecommendationPlaylistService.PlaylistNamePrefix"/> folder name prefix.
    ///     This is a best-effort filesystem cleanup - the Jellyfin library database may still
    ///     reference these playlists until the next library scan, at which point the stale
    ///     entries will be removed automatically.
    /// </summary>
    private void CleanupRecommendationPlaylists()
    {
        try
        {
            var playlistsPath = Path.Combine(_applicationPaths.DataPath, "playlists");
            if (!Directory.Exists(playlistsPath))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(playlistsPath))
            {
                var folderName = Path.GetFileName(dir);
                if (!folderName.StartsWith(
                        RecommendationPlaylistService.PlaylistNamePrefix + " for ",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort - folder may be locked or permission-restricted.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - if the playlists directory is inaccessible, nothing we can do.
        }
    }
}