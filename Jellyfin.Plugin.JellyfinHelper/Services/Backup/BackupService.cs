using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
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
            IncludedLibraries = config.IncludedLibraries,
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

            // Smart Recommendations (only task mode — count and strategy use sensible defaults)
            RecommendationsTaskMode = config.RecommendationsTaskMode.ToString(),
            SyncRecommendationsToPlaylist = config.SyncRecommendationsToPlaylist
        };

        // Arr instances
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
        config.IncludedLibraries = backup.IncludedLibraries;
        config.ExcludedLibraries = backup.ExcludedLibraries;
        config.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, BackupValidator.MaxRetentionDays);
        config.PluginLogLevel = BackupValidator.ValidLogLevels.Contains(backup.PluginLogLevel) ? backup.PluginLogLevel : "INFO";

        // Task modes
        config.TrickplayTaskMode = ParseTaskMode(backup.TrickplayTaskMode);
        config.EmptyMediaFolderTaskMode = ParseTaskMode(backup.EmptyMediaFolderTaskMode);
        config.OrphanedSubtitleTaskMode = ParseTaskMode(backup.OrphanedSubtitleTaskMode);
        config.LinkRepairTaskMode = ParseTaskMode(backup.LinkRepairTaskMode);
        config.SeerrCleanupTaskMode = ParseTaskMode(backup.SeerrCleanupTaskMode, TaskMode.Deactivate);

        // Seerr settings
        config.SeerrUrl = BackupSanitizer.TruncateString(backup.SeerrUrl ?? string.Empty, BackupValidator.MaxUrlLength);
        config.SeerrApiKey = BackupSanitizer.TruncateString(backup.SeerrApiKey ?? string.Empty, BackupValidator.MaxApiKeyLength);
        if (backup.SeerrCleanupAgeDays != 0)
        {
            config.SeerrCleanupAgeDays = Math.Clamp(
                backup.SeerrCleanupAgeDays, 1, BackupValidator.MaxRetentionDays);
        }

        // Trash settings
        config.UseTrash = backup.UseTrash;
        config.TrashFolderPath = string.IsNullOrWhiteSpace(backup.TrashFolderPath)
            ? ".jellyfin-trash"
            : backup.TrashFolderPath;
        config.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, BackupValidator.MaxRetentionDays);

        // Smart Recommendations (only task mode — count and strategy use sensible defaults).
        // Default to DryRun so importing an older backup enables the Discover UI in read-only mode.
        config.RecommendationsTaskMode = ParseTaskMode(backup.RecommendationsTaskMode, TaskMode.DryRun);

        // Playlist sync toggle — defaults to false for older backups without this field
        config.SyncRecommendationsToPlaylist = backup.SyncRecommendationsToPlaylist;

        // Arr instances
        config.RadarrInstances.Clear();
        foreach (var instance in backup.RadarrInstances.Take(BackupValidator.MaxArrInstances))
        {
            config.RadarrInstances.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),
                    ApiKey = BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength)
                });
        }

        config.SonarrInstances.Clear();
        foreach (var instance in backup.SonarrInstances.Take(BackupValidator.MaxArrInstances))
        {
            config.SonarrInstances.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),
                    ApiKey = BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength)
                });
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
        var tempPath = filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, JsonOptions);

            // Atomic write: write to a temporary file first, then rename.
            // This prevents data corruption if the process crashes mid-write.
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Backup", $"Could not save {filePath} during restore", ex, _logger);
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // best-effort cleanup
            }

            return false;
        }
    }
}
