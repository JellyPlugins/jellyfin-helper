using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Service for creating and restoring plugin backups. Handles export of configuration, historical data, and Arr settings.
/// </summary>
public sealed class BackupService : IBackupService
{
    private const string LogSource = "Backup";

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
        ArgumentNullException.ThrowIfNull(applicationPaths);

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
    ///     Returns true if any source data file exceeds MaxBackupSizeBytes. Call before CreateBackup to reject oversized exports early.
    /// </summary>
    /// <returns><c>true</c> when at least one source file exceeds the size limit.</returns>
    public bool AnySourceFileOversized()
    {
        var paths = new[]
        {
            Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"),
            Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"),
        };
        return paths.Any(p => File.Exists(p) && new FileInfo(p).Length > MaxBackupSizeBytes);
    }

    /// <summary>
    ///     Creates a backup of all exportable plugin data.
    /// </summary>
    /// <param name="includeSecrets">
    ///     When <c>true</c>, API key values are included in the backup.
    ///     When <c>false</c> (the default), all API key fields are replaced with an empty
    ///     string so that the exported file does not contain plaintext credentials.
    /// </param>
    /// <returns>The backup data object ready for serialization.</returns>
    public BackupData CreateBackup(bool includeSecrets = false)
    {
        _pluginLog.LogInfo(LogSource, "Creating plugin backup...", _logger);

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

        // Arr instances - API keys are included so that credentials survive a full
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
            Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"),
            out _);

        // Growth baseline (required to preserve diff-based trend history after restore)
        backup.GrowthBaseline = LoadJsonFile<GrowthTimelineBaseline>(
            Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"),
            out _);

        // When the caller opts out of secrets, redact all API key values so the exported file cannot be used to harvest plaintext credentials.
        if (!includeSecrets)
        {
            backup.SeerrApiKey = string.Empty;
            foreach (var instance in backup.RadarrInstances)
            {
                instance.ApiKey = string.Empty;
            }

            foreach (var instance in backup.SonarrInstances)
            {
                instance.ApiKey = string.Empty;
            }
        }

        // Flag the backup when it contains plaintext credentials so the UI/caller can
        // warn the user to store the exported file securely.
        backup.ContainsSecrets =
            !string.IsNullOrEmpty(backup.SeerrApiKey)
            || backup.RadarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey))
            || backup.SonarrInstances.Any(i => !string.IsNullOrEmpty(i.ApiKey));

        _pluginLog.LogInfo(
            LogSource,
            $"Backup created: timeline={backup.GrowthTimeline != null}, baseline={backup.GrowthBaseline != null}",
            _logger);
        return backup;
    }

    /// <summary>
    ///     Restores backup data into the plugin configuration and data files. Must be called only after Validate returns a valid result.
    /// </summary>
    /// <param name="backup">The validated backup data.</param>
    /// <returns>A summary of what was restored.</returns>
    public BackupRestoreSummary RestoreBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var summary = new BackupRestoreSummary();

        _pluginLog.LogInfo(LogSource, "Starting backup restore...", _logger);

        // Write data files FIRST so that if a file-write fails,
        // the live configuration has not yet been replaced.  Only after all
        // I/O completes do we commit the new configuration to disk.

        // Snapshot the paths of files that will be overwritten so that, if the restore only partially succeeds, an operator can identify which files may be in an inconsistent state.
        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");

        var timelineWriteOk = false;
        var baselineWriteOk = false;
        try
        {
            // Restore growth timeline
            if (backup.GrowthTimeline != null)
            {
                if (SaveJsonFile(timelinePath, backup.GrowthTimeline))
                {
                    timelineWriteOk = true;
                    summary.TimelineRestored = true;
                    _pluginLog.LogInfo(
                        LogSource,
                        $"Restored growth timeline ({backup.GrowthTimeline.DataPoints.Count} data points)",
                        _logger);
                }
                else
                {
                    // Stay failsafe: the backup carried timeline data but the write failed. Do not abort the
                    // restore. The next scheduled task run regenerates it. Log distinctly so the skipped
                    // write is not mistaken for "no timeline in the backup" (TimelineRestored stays false).
                    _pluginLog.LogWarning(
                        LogSource,
                        $"Backup restore: growth timeline was present but could not be written to [{timelinePath}]. It will be regenerated on the next scheduled run.",
                        logger: _logger);
                }
            }

            // Restore growth baseline
            if (backup.GrowthBaseline != null)
            {
                if (SaveJsonFile(baselinePath, backup.GrowthBaseline))
                {
                    baselineWriteOk = true;
                    summary.BaselineRestored = true;
                    _pluginLog.LogInfo(
                        LogSource,
                        $"Restored growth baseline ({backup.GrowthBaseline.Directories.Count} directories)",
                        _logger);
                }
                else
                {
                    _pluginLog.LogWarning(
                        LogSource,
                        $"Backup restore: growth baseline was present but could not be written to [{baselinePath}]. It will be regenerated on the next scheduled run.",
                        logger: _logger);
                }
            }

            // Restore configuration last - uses ReadAndMutate so the entire
            // read-mutate-save sequence is atomic with respect to concurrent callers.
            RestoreConfiguration(backup, summary);
        }
        catch (Exception ex)
        {
            var anyWriteSucceeded = timelineWriteOk || baselineWriteOk;
            if (anyWriteSucceeded)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Restore partially applied. Manual recovery may be required. Check [{timelinePath}] and [{baselinePath}] files.",
                    ex,
                    _logger);
            }

            throw;
        }

        _pluginLog.LogInfo(
            LogSource,
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
    /// <param name="logger">Optional logger for diagnostic output on parse failure.</param>
    /// <returns>The backup data, or null if deserialization fails.</returns>
    public static BackupData? DeserializeBackup(string json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupData>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize backup JSON.");
            return null;
        }
    }

    private void RestoreConfiguration(BackupData backup, BackupRestoreSummary summary)
    {
        if (!_configService.IsInitialized)
        {
            _pluginLog.LogWarning(
                LogSource,
                "Plugin instance not available, skipping configuration restore.",
                logger: _logger);
            return;
        }

        // Use ReadAndMutate so the entire read-mutate-save sequence runs under a lock, preventing a concurrent UpdateConfigurationAsync or UpdateLogLevel call from interleaving mutations on the same config object.
        _configService.ReadAndMutate(config =>
        {
            // Restore preferences
            config.Language = BackupValidator.ValidLanguages.Contains(backup.Language) ? backup.Language : "en";
            config.ExcludedLibraries = backup.ExcludedLibraries;
            config.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, BackupValidator.MaxRetentionDays);
            config.PluginLogLevel = BackupValidator.ValidLogLevels.Contains(backup.PluginLogLevel)
                ? backup.PluginLogLevel
                : "INFO";

            // Task modes
            config.TrickplayTaskMode = ParseTaskMode(backup.TrickplayTaskMode, nameof(config.TrickplayTaskMode));
            config.EmptyMediaFolderTaskMode = ParseTaskMode(backup.EmptyMediaFolderTaskMode, nameof(config.EmptyMediaFolderTaskMode));
            config.OrphanedSubtitleTaskMode = ParseTaskMode(backup.OrphanedSubtitleTaskMode, nameof(config.OrphanedSubtitleTaskMode));
            config.LinkRepairTaskMode = ParseTaskMode(backup.LinkRepairTaskMode, nameof(config.LinkRepairTaskMode));
            config.SeerrCleanupTaskMode = ParseTaskMode(backup.SeerrCleanupTaskMode, nameof(config.SeerrCleanupTaskMode), TaskMode.Deactivate);

            // Seerr settings
            RestoreSeerrSettings(config, backup, summary);

            // Trash settings
            RestoreTrashSettings(config, backup);

            // Smart Recommendations (only task mode - count and strategy use sensible defaults).
            // Default to DryRun so importing an older backup enables the Discover UI in read-only mode.
            config.RecommendationsTaskMode = ParseTaskMode(backup.RecommendationsTaskMode, nameof(config.RecommendationsTaskMode));

            // Playlist sync toggle - defaults to false for older backups without this field
            config.SyncRecommendationsToPlaylist = backup.SyncRecommendationsToPlaylist;

            // Discovery user access - defaults to false for older backups without this field
            config.DiscoveryUserAccessEnabled = backup.DiscoveryUserAccessEnabled;

            // Arr instances - preserve live keys when the backup omitted them, and flag any
            // credential replacements via CredentialsChanged on the summary.
            RestoreArrInstances(backup.RadarrInstances, config.RadarrInstances, "Radarr", summary);
            RestoreArrInstances(backup.SonarrInstances, config.SonarrInstances, "Sonarr", summary);

            // Set only after the entire mutation has been applied; if ReadAndMutate throws,
            // this flag stays false so the caller does not falsely report success.
            summary.ConfigurationRestored = true;
        });

        _pluginLog.LogInfo(LogSource, "Configuration restored from backup.", _logger);
    }

    /// <summary>
    ///     Restores the Seerr URL and API key from the backup into the live config.
    /// </summary>
    /// <param name="config">The live configuration being mutated.</param>
    /// <param name="backup">The backup data being restored.</param>
    /// <param name="summary">The restore summary to flag credential changes on.</param>
    private void RestoreSeerrSettings(PluginConfiguration config, BackupData backup, BackupRestoreSummary summary)
    {
        // An empty backup URL means "leave the existing URL in place", mirroring the API key guard below - a backup created without Seerr must not silently wipe a working URL.
        if (!string.IsNullOrEmpty(backup.SeerrUrl))
        {
            var truncatedUrl = BackupSanitizer.TruncateString(backup.SeerrUrl, BackupValidator.MaxUrlLength);
            if (Uri.TryCreate(truncatedUrl, UriKind.Absolute, out var parsedUrl)
                && (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps))
            {
                config.SeerrUrl = truncatedUrl;
            }
            else
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Backup SeerrUrl '{truncatedUrl}' is not a valid http/https URL - skipping to avoid persisting an unsafe scheme.",
                    logger: _logger);
            }
        }

        // API keys: an empty backup value means "leave the existing key in place"; a non-empty value is applied after the same length-truncation as other fields.
        if (!string.IsNullOrEmpty(backup.SeerrApiKey))
        {
            var truncatedSeerrKey = BackupSanitizer.TruncateString(backup.SeerrApiKey, BackupValidator.MaxApiKeyLength);
            var truncatedStoredKey = BackupSanitizer.TruncateString(config.SeerrApiKey, BackupValidator.MaxApiKeyLength);
            if (truncatedSeerrKey != truncatedStoredKey)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    "Backup restore is replacing credentials: Seerr API key changed.",
                    logger: _logger);
                summary.CredentialsChanged = true;
            }

            config.SeerrApiKey = truncatedSeerrKey;
        }

        // Null means "absent in backup" (older plugin version or field omitted), so leave the live value unchanged.
        if (backup.SeerrCleanupAgeDays.HasValue)
        {
            config.SeerrCleanupAgeDays = Math.Clamp(
                backup.SeerrCleanupAgeDays.Value,
                0,
                BackupValidator.MaxRetentionDays);
        }
    }

    /// <summary>
    ///     Restores the trash toggle, folder path, and retention days from the backup into the live config, defanging an unsafe trash path (traversal or sensitive system path) to the default.
    /// </summary>
    /// <param name="config">The live configuration being mutated.</param>
    /// <param name="backup">The backup data being restored.</param>
    private static void RestoreTrashSettings(PluginConfiguration config, BackupData backup)
    {
        config.UseTrash = backup.UseTrash;
        // Defang unsafe trash path to default instead of failing restore.
        var rawTrashPath = backup.TrashFolderPath;
        var hasTraversal = PathValidator.HasTraversalSegment(rawTrashPath);
        var isSensitive = !string.IsNullOrWhiteSpace(rawTrashPath) &&
            PathValidator.IsSensitiveSystemPath(rawTrashPath);
        config.TrashFolderPath = string.IsNullOrWhiteSpace(rawTrashPath) || hasTraversal || isSensitive
            ? ".jellyfin-trash"
            : rawTrashPath;
        config.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, BackupValidator.MaxRetentionDays);
    }

    /// <summary>
    ///     Restores a single Arr instance list (Radarr or Sonarr) from backup into the live config list.
    /// </summary>
    private void RestoreArrInstances(
        IReadOnlyList<BackupArrInstance> backupInstances,
        List<ArrInstanceConfig> liveInstances,
        string label,
        BackupRestoreSummary summary)
    {
        // Snapshot existing keys (truncated to MaxApiKeyLength for apples-to-apples comparison). Case-INSENSITIVE name lookup: the "empty backup key means preserve the live key" rule keys off the instance Name, so a case-only rename between export and import (e.g.
        var previousKeys = liveInstances
            .ToLookup(
                i => i.Name,
                i => BackupSanitizer.TruncateString(i.ApiKey, BackupValidator.MaxApiKeyLength),
                StringComparer.OrdinalIgnoreCase);

        var newList = new List<ArrInstanceConfig>();
        var keysChanged = 0;
        var silentWipes = 0;

        foreach (var instance in backupInstances.Take(BackupValidator.MaxArrInstances))
        {
            // An empty backup key means "preserve the live key" - fall back to the previously
            // stored key for this instance name, consistent with the SeerrApiKey guard above.
            var backupKeyEmpty = string.IsNullOrEmpty(instance.ApiKey);
            var apiKey = backupKeyEmpty
                ? (previousKeys[instance.Name].FirstOrDefault() ?? string.Empty)
                : BackupSanitizer.TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength);

            // "Preserve the live key" that matched nothing -> the instance ends up with an empty key. Surface it instead of letting the wipe pass silently (the keysChanged audit below is gated on a NON-empty key, so it never reports this case).
            if (backupKeyEmpty && string.IsNullOrEmpty(apiKey))
            {
                silentWipes++;
            }

            // Detect credential change: non-empty incoming key not found in any prior entry for this name. Both sides are truncated to the same length so a key that was stored full-length but backed up at MaxApiKeyLength is not a false positive.
            if (!string.IsNullOrEmpty(apiKey) && !previousKeys[instance.Name].Any(k => k == apiKey))
            {
                keysChanged++;
            }

            newList.Add(
                new ArrInstanceConfig
                {
                    Name = BackupSanitizer.TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength),
                    Url = BackupSanitizer.TruncateString(instance.Url, BackupValidator.MaxUrlLength),
                    ApiKey = apiKey
                });
        }

        liveInstances.Clear();
        liveInstances.AddRange(newList);

        if (silentWipes > 0)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"{label}: {silentWipes} instance(s) had an empty backup API key with no matching live key to preserve. "
                + "Their API key is now empty and must be re-entered.",
                logger: _logger);
        }

        if (keysChanged > 0)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Backup restore is replacing credentials: {keysChanged} {label} instance API key(s) changed.",
                logger: _logger);
            summary.CredentialsChanged = true;
        }
    }

    private TaskMode ParseTaskMode(string? value, string fieldName, TaskMode fallback = TaskMode.DryRun)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TaskMode>(value, true, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        // A non-empty value that fails to parse is malformed backup data, not a legitimate default;
        // surface it so the admin knows the restored mode differs from what the file claimed.
        _pluginLog.LogWarning(
            LogSource,
            $"Backup restore: {fieldName} value '{value}' is not a valid task mode. Falling back to {fallback}.",
            logger: _logger);
        return fallback;
    }

    private T? LoadJsonFile<T>(string filePath, out bool oversized)
        where T : class
    {
        oversized = false;
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxBackupSizeBytes)
            {
                oversized = true;
                _pluginLog.LogWarning(
                    LogSource,
                    $"Skipping {filePath} for backup: file size {fileInfo.Length} bytes exceeds {MaxBackupSizeBytes} byte limit.",
                    logger: _logger);
                return null;
            }

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning(LogSource, $"Could not load {filePath} for backup", ex, _logger);
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

            // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
            AtomicFile.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _pluginLog.LogError(LogSource, $"Could not save {filePath} during restore", ex, _logger);
            return false;
        }
    }
}
