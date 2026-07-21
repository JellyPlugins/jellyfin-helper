using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Service for creating and restoring plugin backups.
///     Handles export of configuration, historical data, and Arr settings.
///     Validation is provided by <see cref="BackupValidator" /> and
///     sanitization by <see cref="BackupSanitizer" />.
/// </summary>
public class BackupService : IBackupService
{
    /// <summary>
    ///     Maximum allowed size of a backup JSON payload in bytes (10 MB).
    ///     Per-directory baselines can be larger for media servers with many items.
    /// </summary>
    internal const long MaxBackupSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    ///     Threshold at which backup payload size should be logged as unusually large.
    /// </summary>
    internal const long LargeBackupWarningThresholdBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    private readonly IPluginConfigurationService _configService;

    private readonly string _dataPath;
    private readonly ILogger<BackupService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackupService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="configService">The plugin configuration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public BackupService(
        IApplicationPaths applicationPaths,
        IPluginConfigurationService configService,
        IPluginLogService pluginLog,
        ILogger<BackupService> logger)
    {
        _dataPath = applicationPaths.DataPath;
        _configService = configService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="BackupService" /> class for testing.
    /// </summary>
    /// <param name="dataPath">The data path.</param>
    /// <param name="configService">The plugin configuration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    internal BackupService(
        string dataPath,
        IPluginConfigurationService configService,
        IPluginLogService pluginLog,
        ILogger<BackupService> logger)
    {
        _dataPath = dataPath;
        _configService = configService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Creates a backup of all exportable plugin data.
    /// </summary>
    /// <returns>The backup data object ready for serialization.</returns>
    public BackupData CreateBackup()
    {
        _pluginLog.LogInfo("Backup", "Creating plugin backup...", _logger);

        var config = _configService.GetConfiguration();
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = _configService.PluginVersion,

            // Configuration preferences
            Language = config.Language,
            ExcludedLibraries = config.ExcludedLibraries,
            OrphanMinAgeDays = config.OrphanMinAgeDays,
            PluginLogLevel = config.PluginLogLevel,

            // Task modes
            TrickplayTaskMode = config.TrickplayTaskMode.ToString(),
            EmptyMediaFolderTaskMode = config.EmptyMediaFolderTaskMode.ToString(),
            OrphanedSubtitleTaskMode = config.OrphanedSubtitleTaskMode.ToString(),
            LinkRepairTaskMode = config.LinkRepairTaskMode.ToString(),
            SeerrCleanupTaskMode = config.SeerrCleanupTaskMode.ToString(),

            // Seerr settings
            SeerrUrl = config.SeerrUrl,
            SeerrApiKey = config.SeerrApiKey,
            SeerrCleanupAgeDays = config.SeerrCleanupAgeDays,

            // Trash settings
            UseTrash = config.UseTrash,
            TrashFolderPath = config.TrashFolderPath,
            TrashRetentionDays = config.TrashRetentionDays,

            // Smart Recommendations (only task mode - count and strategy use sensible defaults)
            RecommendationsTaskMode = config.RecommendationsTaskMode.ToString(),
            SyncRecommendationsToPlaylist = config.SyncRecommendationsToPlaylist,

            // Discovery user access
            DiscoveryUserAccessEnabled = config.DiscoveryUserAccessEnabled
        };

        // Arr instances — API keys are included so that credentials survive a full
        // backup/restore cycle. ContainsSecrets is set below when any key is non-empty.
        foreach (var instance in config.RadarrInstances)
        {
            backup.RadarrInstances.Add(
                new BackupArrInstance
                {
                    Name = instance.Name,
                    Url = instance.Url,
                    ApiKey = instance.ApiKey
                });
        }

        foreach (var instance in config.SonarrInstances)
        {
            backup.SonarrInstances.Add(
                new BackupArrInstance
                {
                    Name = instance.Name,
                    Url = instance.Url,
                    ApiKey = instance.ApiKey
                });
        }

        // Growth timeline
        backup.GrowthTimeline = LoadJsonFile<GrowthTimelineResult>(
            Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"));

        // Growth baseline (required to preserve diff-based trend history after restore)
        backup.GrowthBaseline = LoadJsonFile<GrowthTimelineBaseline>(
            Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"));

        // Flag the backup when it contains plaintext credentials so the UI/caller can
        // warn the user to store the exported file securely.
        backup.ContainsSecrets =
            !string.IsNullOrEmpty(backup.SeerrApiKey)
            || backup.RadarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey))
            || backup.SonarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey));

        _pluginLog.LogInfo(
            "Backup",
            $"Backup created: timeline={backup.GrowthTimeline != null}, baseline={backup.GrowthBaseline != null}",
            _logger);
        return backup;
    }

    /// <summary>
    ///     Restores backup data into the plugin configuration and data files.
    ///     Must be called only after <see cref="BackupValidator.Validate" /> returns a valid result.
    /// </summary>
    /// <param name="backup">The validated backup data.</param>
    /// <returns>A summary of what was restored.</returns>
    public BackupRestoreSummary RestoreBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var summary = new BackupRestoreSummary();

        _pluginLog.LogInfo("Backup", "Starting backup restore...", _logger);

        // Restore configuration
        RestoreConfiguration(backup, summary);

        // Restore growth timeline
        if (backup.GrowthTimeline != null &&
            SaveJsonFile(
                Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"),
                backup.GrowthTimeline))
        {
            summary.TimelineRestored = true;
            _pluginLog.LogInfo(
                "Backup",
                $"Restored growth timeline ({backup.GrowthTimeline.DataPoints.Count} data points)",
                _logger);
        }

        // Restore growth baseline
        if (backup.GrowthBaseline != null &&
            SaveJsonFile(
                Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"),
                backup.GrowthBaseline))
        {
            summary.BaselineRestored = true;
            _pluginLog.LogInfo(
                "Backup",
                $"Restored growth baseline ({backup.GrowthBaseline.Directories.Count} directories)",
                _logger);
        }

        _pluginLog.LogInfo(
            "Backup",
            $"Backup restore complete. Config={summary.ConfigurationRestored}, Timeline={summary.TimelineRestored}, Baseline={summary.BaselineRestored}",
            _logger);
        return summary;
    }

    /// <summary>
    ///     Serializes backup data to a JSON string.
    /// </summary>
    /// <param name="backup">The backup data.</param>
    /// <returns>The JSON string.</returns>
    public static string SerializeBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return JsonSerializer.Serialize(backup, JsonOptions);
    }

    /// <summary>
    ///     Deserializes a JSON string to backup data.
    ///     Returns null if the JSON is invalid.
    /// </summary>
    /// <param name="json">The JSON string.</param>
    /// <returns>The backup data, or null if deserialization fails.</returns>
    public static BackupData? DeserializeBackup(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupData>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // === Private helpers ===

    private void RestoreConfiguration(BackupData backup, BackupRestoreSummary summary)
    {
        if (!_configService.IsInitialized)
        {
            _pluginLog.LogWarning(
                "Backup",
                "Plugin instance not available, skipping configuration restore.",
                logger: _logger);
            return;
        }

        var config = _configService.GetConfiguration();

        // Restore preferences
        config.Language = BackupValidator.ValidLanguages.Contains(backup.Language) ? backup.Language : "en";
        config.ExcludedLibraries = backup.ExcludedLibraries;
        config.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, BackupValidator.MaxRetentionDays);
        config.PluginLogLevel = BackupValidator.ValidLogLevels.Contains(backup.PluginLogLevel)
            ? backup.PluginLogLevel
            : "INFO";

        // Task modes
        config.TrickplayTaskMode = ParseTaskMode(backup.TrickplayTaskMode);
        config.EmptyMediaFolderTaskMode = ParseTaskMode(backup.EmptyMediaFolderTaskMode);
        config.OrphanedSubtitleTaskMode = ParseTaskMode(backup.OrphanedSubtitleTaskMode);
        config.LinkRepairTaskMode = ParseTaskMode(backup.LinkRepairTaskMode);
        config.SeerrCleanupTaskMode = ParseTaskMode(backup.SeerrCleanupTaskMode, TaskMode.Deactivate);

        // Seerr settings
        config.SeerrUrl = BackupSanitizer.TruncateString(backup.SeerrUrl, BackupValidator.MaxUrlLength);
        // API keys: an empty backup value means "leave the existing key in place"; a
        // non-empty value is applied after the same length-truncation as other fields.
        // When the incoming value actually differs from the current stored value, emit an
        // audit warning and set the CredentialsChanged flag so callers can surface a
        // notification to the operator.
        if (!string.IsNullOrEmpty(backup.SeerrApiKey))
        {
            var truncatedSeerrKey = BackupSanitizer.TruncateString(backup.SeerrApiKey, BackupValidator.MaxApiKeyLength);
            var truncatedStoredKey = BackupSanitizer.TruncateString(config.SeerrApiKey, BackupValidator.MaxApiKeyLength);
            if (truncatedSeerrKey != truncatedStoredKey)
            {
                _pluginLog.LogWarning(
                    "Backup",
                    "Backup restore is replacing credentials: Seerr API key changed.",
                    logger: _logger);
                summary.CredentialsChanged = true;
            }

            config.SeerrApiKey = truncatedSeerrKey;
        }

        if (backup.SeerrCleanupAgeDays != 0)
        {
            config.SeerrCleanupAgeDays = Math.Clamp(
                backup.SeerrCleanupAgeDays,
                1,
                BackupValidator.MaxRetentionDays);
        }

        // Trash settings
        config.UseTrash = backup.UseTrash;
        config.TrashFolderPath = string.IsNullOrWhiteSpace(backup.TrashFolderPath)
            ? ".jellyfin-trash"
            : backup.TrashFolderPath;
        config.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, BackupValidator.MaxRetentionDays);

        // Smart Recommendations (only task mode - count and strategy use sensible defaults).
        // Default to DryRun so importing an older backup enables the Discover UI in read-only mode.
        config.RecommendationsTaskMode = ParseTaskMode(backup.RecommendationsTaskMode);

        // Playlist sync toggle - defaults to false for older backups without this field
        config.SyncRecommendationsToPlaylist = backup.SyncRecommendationsToPlaylist;

        // Discovery user access - defaults to false for older backups without this field
        config.DiscoveryUserAccessEnabled = backup.DiscoveryUserAccessEnabled;

        // Arr instances — for each instance, preserve the existing API key when the backup
        // omitted it (empty string), so that a restore does not wipe live credentials.
        // When a non-empty key is present and it differs from the current stored value,
        // emit an audit warning and set CredentialsChanged on the summary.
        // Use the last entry when duplicate names exist — ToDictionary would throw in that case.
        var previousRadarr = config.RadarrInstances
            .GroupBy(i => i.Name)
            .ToDictionary(g => g.Key, g => g.Last().ApiKey);
        config.RadarrInstances.Clear();
        var radarrKeysChanged = 0;
        foreach (var instance in backup.RadarrInstances.Take(BackupValidator.MaxArrInstances))
        {
            var apiKey = string.IsNullOrEmpty(instance.ApiKey)
                ? string.Empty
                : BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength);

            // Detect credential change: non-empty incoming key that differs from the live value.
            if (!string.IsNullOrEmpty(apiKey) &&
                (!previousRadarr.TryGetValue(instance.Name, out var prevKey) || prevKey != apiKey))
            {
                radarrKeysChanged++;
            }

            config.RadarrInstances.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),
                    ApiKey = apiKey
                });
        }

        if (radarrKeysChanged > 0)
        {
            _pluginLog.LogWarning(
                "Backup",
                $"Backup restore is replacing credentials: {radarrKeysChanged} Radarr instance API key(s) changed.",
                logger: _logger);
            summary.CredentialsChanged = true;
        }

        // Use the last entry when duplicate names exist — ToDictionary would throw in that case.
        var previousSonarr = config.SonarrInstances
            .GroupBy(i => i.Name)
            .ToDictionary(g => g.Key, g => g.Last().ApiKey);
        config.SonarrInstances.Clear();
        var sonarrKeysChanged = 0;
        foreach (var instance in backup.SonarrInstances.Take(BackupValidator.MaxArrInstances))
        {
            var apiKey = string.IsNullOrEmpty(instance.ApiKey)
                ? string.Empty
                : BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength);

            if (!string.IsNullOrEmpty(apiKey) &&
                (!previousSonarr.TryGetValue(instance.Name, out var prevKey) || prevKey != apiKey))
            {
                sonarrKeysChanged++;
            }

            config.SonarrInstances.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),
                    ApiKey = apiKey
                });
        }

        if (sonarrKeysChanged > 0)
        {
            _pluginLog.LogWarning(
                "Backup",
                $"Backup restore is replacing credentials: {sonarrKeysChanged} Sonarr instance API key(s) changed.",
                logger: _logger);
            summary.CredentialsChanged = true;
        }

        _configService.SaveConfiguration();
        summary.ConfigurationRestored = true;
        _pluginLog.LogInfo("Backup", "Configuration restored from backup.", _logger);
    }

    private static TaskMode ParseTaskMode(string? value, TaskMode fallback = TaskMode.DryRun)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TaskMode>(value, true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        return fallback;
    }

    private T? LoadJsonFile<T>(string filePath)
        where T : class
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("Backup", $"Could not load {filePath} for backup", ex, _logger);
            return null;
        }
    }

    private bool SaveJsonFile<T>(string filePath, T data)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);

            // Use AtomicFile so a transient sharing violation on the final File.Move
            // (typical when an AV scanner or the Search indexer briefly holds the file
            // handle) gets a bounded retry with backoff. AtomicFile also handles
            // temp-file cleanup internally.
            AtomicFile.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Backup", $"Could not save {filePath} during restore", ex, _logger);
            return false;
        }
    }
}