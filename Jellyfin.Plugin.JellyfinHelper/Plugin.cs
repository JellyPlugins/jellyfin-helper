using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Services.WebInjection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
///     The main plugin.
/// </summary>
public partial class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    ///     Data files this plugin persists to DataPath that do <b>not</b> follow the jellyfin-helper-*.json naming convention and therefore would not be matched by the prefix glob in CleanupDataFiles.
    /// </summary>
    private static readonly string[] UnprefixedDataFiles =
    [
        "ml_weights.json",
        "neural_weights.json",
        "ensemble_state.json",
        "jellyfin-helper-batch-generation.txt",
    ];

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="Plugin" /> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths" /> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer" /> interface.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <param name="pluginLog">Plugin log for user-visible startup and cleanup entries.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger, IPluginLogService pluginLog)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _pluginLog = pluginLog;
        Instance = this;
        Api.UserDiscoveryController.ClearRateLimitState();
        ReportClampedConfigValues();
        CleanupLegacyInjection();
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
    ///     Gets the logger for use by internal helpers that share the plugin's logging category.
    /// </summary>
    internal ILogger<Plugin> Logger => _logger;

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            config.NormalizeAlphaRange();
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        CleanupLegacyInjection();
        CleanupDataFiles();
        CleanupRecommendationPlaylists();
        try
        {
            Api.UserDiscoveryController.ClearRateLimitState();
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[OnUninstalling] ClearRateLimitState failed (best-effort)");
            }
        }

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
    ///     Surfaces any config values that were clamped during XML deserialization as a single warning line per affected property.
    /// </summary>
    private void ReportClampedConfigValues()
    {
        // BasePlugin<T>.Configuration is lazily materialised - in the real host it is populated before this ctor runs, but tests spin up a bare Plugin instance without a serializer wiring, so Configuration may still be null here.
        PluginConfiguration? config;
        try
        {
            config = Configuration;
        }
        catch (Exception ex)
        {
            // Guarded like the rest of the LogDebug calls in this class so a future
            // parameterized message does not accidentally regress the CA1873 pattern.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "[Configuration] Configuration unavailable at startup; skipping clamp report");
            }

            return;
        }

        if (config is null)
        {
            return;
        }

        // Normalize alpha range BEFORE draining reports so any Min > Max swap is included in this drain rather than being silently discarded (PluginServiceRegistrator calls NormalizeAlphaRange during DI build, after the constructor drain already ran).
        config.NormalizeAlphaRange();

        var reports = config.DrainClampReports();
        if (reports.Count == 0)
        {
            return;
        }

        foreach (var entry in reports)
        {
            _logger.LogWarning(
                "[Configuration] Value for {Property} was outside its accepted range and was clamped: {Raw} -> {Clamped}",
                entry.PropertyName,
                entry.RawValue,
                entry.ClampedValue);
        }
    }

    /// <summary>
    ///     Deletes all persistent data files created by this plugin from the Jellyfin data directory.
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

            // Match all files created by this plugin: jellyfin-helper-* Only delete known extensions (.json data files and .tmp atomic-write leftovers) to avoid accidental deletion of unrelated files sharing the prefix.
            foreach (var file in Directory.GetFiles(dataPath, "jellyfin-helper-*"))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeleteDataFile(file);
            }

            // Recommendation ML/state artifacts that don't match the prefix+extension glob above (unprefixed .json weights, and the .txt batch-generation counter).
            foreach (var name in UnprefixedDataFiles)
            {
                var file = Path.Combine(dataPath, name);
                if (File.Exists(file))
                {
                    DeleteDataFile(file);
                }
            }

            // Per-user recommendation model/state files (ml_weights_{id}.json / ensemble_state_{id}.json).
            // These share no jellyfin-helper- prefix and are not in the fixed list above, so they need their
            // own sweep or an uninstall would leave one pair per user behind. Matched on the exact id-suffixed
            // shape so only files this plugin writes are removed.
            DeletePerUserDataFiles(dataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best effort - if the data directory is inaccessible, nothing we can do.
        }
    }

    private void DeleteDataFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to clean up data file");
        }
    }

    // Deletes only the per-user recommendation files whose full name matches the id-suffixed shape this plugin
    // writes. A glob like ml_weights_*.json would also sweep up a file that merely shares the stem, so the name
    // is checked against the anchored pattern rather than a wildcard.
    // Deletes only the per-user recommendation files whose full name matches the id-suffixed shape this plugin
    // writes. A glob like ml_weights_*.json would also sweep up a file that merely shares the stem, so the name
    // is checked against the anchored pattern rather than a wildcard.
    private void DeletePerUserDataFiles(string dataPath)
    {
        foreach (var file in Directory.GetFiles(dataPath).Where(f => PerUserDataFilePattern().IsMatch(Path.GetFileName(f))))
        {
            DeleteDataFile(file);
        }
    }

    /// <summary>
    ///     Matches only the per-user recommendation model/state files this plugin writes
    ///     (<c>ml_weights_{id:N}.json</c> and <c>ensemble_state_{id:N}.json</c>, where the id is the 32-hex
    ///     user id). Anchored on the full name so uninstall cleanup deletes exactly these and never an
    ///     unrelated file that merely shares the stem (for example a user-made <c>ml_weights_backup.json</c>)
    ///     nor the unsuffixed global files.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^(?:ml_weights|ensemble_state)_[0-9a-fA-F]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex PerUserDataFilePattern();

    /// <summary>
    ///     Removes all recommendation playlist folders created by this plugin. Jellyfin stores playlists as subdirectories under {DataPath}/playlists/.
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

    /// <summary>
    ///     Removes index.html changes left behind by plugin versions that wrote the script tag to disk.
    /// </summary>
    private void CleanupLegacyInjection()
    {
        LegacyIndexHtmlCleanup.TryDeleteStaleTempFiles(_pluginLog, _logger, _applicationPaths.WebPath);
        LegacyIndexHtmlCleanup.TryRemoveScriptTag(_pluginLog, _logger, _applicationPaths.WebPath);
    }
}
