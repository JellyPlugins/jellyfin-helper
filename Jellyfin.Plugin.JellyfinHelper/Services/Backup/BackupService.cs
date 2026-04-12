using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
/// Service for creating and restoring plugin backups with comprehensive data validation.
/// Handles export of configuration, historical data, and Arr settings,
/// and validates imported data to prevent malicious or corrupt payloads.
/// </summary>
public class BackupService
{
    /// <summary>
    /// Maximum allowed backup version for import.
    /// </summary>
    internal const int MaxBackupVersion = 1;

    /// <summary>
    /// Maximum allowed size of a backup JSON payload in bytes (50 MB).
    /// </summary>
    internal const long MaxBackupSizeBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Threshold at which backup payload size should be logged as unusually large.
    /// </summary>
    internal const long LargeBackupWarningThresholdBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum number of statistics history snapshots allowed in a backup.
    /// </summary>
    internal const int MaxHistorySnapshots = 400;

    /// <summary>
    /// Maximum number of growth timeline data points allowed in a backup.
    /// </summary>
    internal const int MaxTimelineDataPoints = 5000;

    /// <summary>
    /// Maximum number of baseline directories allowed in a backup.
    /// </summary>
    internal const int MaxBaselineDirectories = 100_000;

    /// <summary>
    /// Maximum number of Arr instances per type (Radarr/Sonarr).
    /// </summary>
    internal const int MaxArrInstances = 3;

    /// <summary>
    /// Maximum string length for general text fields (library names, paths, etc.).
    /// </summary>
    internal const int MaxStringLength = 1000;

    /// <summary>
    /// Maximum string length for URL fields.
    /// </summary>
    internal const int MaxUrlLength = 500;

    /// <summary>
    /// Maximum string length for API key fields.
    /// </summary>
    internal const int MaxApiKeyLength = 200;

    /// <summary>
    /// Maximum string length for instance name fields.
    /// </summary>
    internal const int MaxInstanceNameLength = 100;

    /// <summary>
    /// Valid language codes.
    /// </summary>
    internal static readonly HashSet<string> ValidLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "de", "fr", "es", "pt", "zh", "tr",
    };

    /// <summary>
    /// Valid task mode values.
    /// </summary>
    internal static readonly HashSet<string> ValidTaskModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deactivate", "DryRun", "Activate",
    };

    /// <summary>
    /// Valid log levels.
    /// </summary>
    internal static readonly HashSet<string> ValidLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEBUG", "INFO", "WARN", "ERROR",
    };

    /// <summary>
    /// Valid timeline granularity values.
    /// </summary>
    internal static readonly HashSet<string> ValidGranularities = new(StringComparer.OrdinalIgnoreCase)
    {
        "daily", "weekly", "monthly", "quarterly", "yearly",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // Regex to detect script injection in string fields
    private static readonly Regex ScriptPattern = new(
        @"<\s*script|javascript\s*:|on\w+\s*=|<\s*iframe|<\s*object|<\s*embed|<\s*form|<\s*svg\s+on",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private readonly string _dataPath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public BackupService(IApplicationPaths applicationPaths, ILogger logger)
    {
        _dataPath = applicationPaths.DataPath;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupService"/> class for testing.
    /// </summary>
    /// <param name="dataPath">The data path.</param>
    /// <param name="logger">The logger.</param>
    internal BackupService(string dataPath, ILogger logger)
    {
        _dataPath = dataPath;
        _logger = logger;
    }

    /// <summary>
    /// Creates a backup of all exportable plugin data.
    /// </summary>
    /// <returns>The backup data object ready for serialization.</returns>
    public BackupData CreateBackup()
    {
        PluginLogService.LogInfo("Backup", "Creating plugin backup...", _logger);

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var backup = new BackupData
        {
            BackupVersion = 1,
            CreatedAt = DateTime.UtcNow,
            PluginVersion = Plugin.Instance?.Version?.ToString() ?? "unknown",

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
            StrmRepairTaskMode = config.StrmRepairTaskMode.ToString(),

            // Trash settings
            UseTrash = config.UseTrash,
            TrashFolderPath = config.TrashFolderPath,
            TrashRetentionDays = config.TrashRetentionDays,
        };

        // Arr instances
        foreach (var instance in config.RadarrInstances)
        {
            backup.RadarrInstances.Add(new BackupArrInstance
            {
                Name = instance.Name,
                Url = instance.Url,
                ApiKey = instance.ApiKey,
            });
        }

        foreach (var instance in config.SonarrInstances)
        {
            backup.SonarrInstances.Add(new BackupArrInstance
            {
                Name = instance.Name,
                Url = instance.Url,
                ApiKey = instance.ApiKey,
            });
        }

        // Growth timeline
        backup.GrowthTimeline = LoadJsonFile<GrowthTimelineResult>(
            Path.Combine(_dataPath, "jellyfin-helper-growth-timeline.json"));

        // Growth baseline (required to preserve diff-based trend history after restore)
        backup.GrowthBaseline = LoadJsonFile<GrowthTimelineBaseline>(
            Path.Combine(_dataPath, "jellyfin-helper-growth-baseline.json"));

        // Statistics history
        var history = LoadJsonFile<List<StatisticsSnapshot>>(
            Path.Combine(_dataPath, "jellyfin-helper-statistics-history.json"));
        if (history != null)
        {
            foreach (var snap in history)
            {
                backup.StatisticsHistory.Add(snap);
            }
        }

        PluginLogService.LogInfo("Backup", $"Backup created: {backup.StatisticsHistory.Count} history snapshots, timeline={backup.GrowthTimeline != null}, baseline={backup.GrowthBaseline != null}", _logger);
        return backup;
    }

    /// <summary>
    /// Serializes backup data to a JSON string.
    /// </summary>
    /// <param name="backup">The backup data.</param>
    /// <returns>The JSON string.</returns>
    public static string SerializeBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return JsonSerializer.Serialize(backup, JsonOptions);
    }

    /// <summary>
    /// Deserializes a JSON string to back up data.
    /// Returns null if the JSON is invalid.
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

    /// <summary>
    /// Validates backup data comprehensively, checking for malicious content,
    /// out-of-range values, and structural integrity.
    /// </summary>
    /// <param name="backup">The backup data to validate.</param>
    /// <returns>The validation result with errors and warnings.</returns>
    public static BackupValidationResult Validate(BackupData? backup)
    {
        var result = new BackupValidationResult();

        if (backup == null)
        {
            result.Errors.Add("Backup data is null or could not be deserialized.");
            return result;
        }

        // Version check
        if (backup.BackupVersion < 1 || backup.BackupVersion > MaxBackupVersion)
        {
            result.Errors.Add($"Unsupported backup version: {backup.BackupVersion}. Expected 1–{MaxBackupVersion}.");
        }

        // Timestamp sanity
        if (backup.CreatedAt < new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        {
            result.Warnings.Add($"Backup timestamp is suspiciously old: {backup.CreatedAt:O}");
        }

        if (backup.CreatedAt > DateTime.UtcNow.AddDays(1))
        {
            result.Warnings.Add($"Backup timestamp is in the future: {backup.CreatedAt:O}");
        }

        // String field validation (XSS / injection prevention)
        ValidateStringField(result, backup.Language, "Language", MaxStringLength);
        ValidateStringField(result, backup.IncludedLibraries, "IncludedLibraries", MaxStringLength);
        ValidateStringField(result, backup.ExcludedLibraries, "ExcludedLibraries", MaxStringLength);
        ValidateStringField(result, backup.PluginLogLevel, "PluginLogLevel", MaxStringLength);
        ValidateStringField(result, backup.TrashFolderPath, "TrashFolderPath", MaxStringLength);
        ValidateStringField(result, backup.PluginVersion, "PluginVersion", MaxStringLength);
        ValidateStringField(result, backup.TrickplayTaskMode, "TrickplayTaskMode", MaxStringLength);
        ValidateStringField(result, backup.EmptyMediaFolderTaskMode, "EmptyMediaFolderTaskMode", MaxStringLength);
        ValidateStringField(result, backup.OrphanedSubtitleTaskMode, "OrphanedSubtitleTaskMode", MaxStringLength);
        ValidateStringField(result, backup.StrmRepairTaskMode, "StrmRepairTaskMode", MaxStringLength);

        // Enum validation
        if (!string.IsNullOrEmpty(backup.Language) && !ValidLanguages.Contains(backup.Language))
        {
            result.Warnings.Add($"Unknown language '{backup.Language}'. Will default to 'en'.");
        }

        ValidateTaskMode(result, backup.TrickplayTaskMode, "TrickplayTaskMode");
        ValidateTaskMode(result, backup.EmptyMediaFolderTaskMode, "EmptyMediaFolderTaskMode");
        ValidateTaskMode(result, backup.OrphanedSubtitleTaskMode, "OrphanedSubtitleTaskMode");
        ValidateTaskMode(result, backup.StrmRepairTaskMode, "StrmRepairTaskMode");

        if (!string.IsNullOrEmpty(backup.PluginLogLevel) && !ValidLogLevels.Contains(backup.PluginLogLevel))
        {
            result.Warnings.Add($"Unknown log level '{backup.PluginLogLevel}'. Will default to 'INFO'.");
        }

        // Numeric range validation
        if (backup.OrphanMinAgeDays < 0 || backup.OrphanMinAgeDays > 3650)
        {
            result.Errors.Add($"OrphanMinAgeDays out of range: {backup.OrphanMinAgeDays}. Must be 0–3650.");
        }

        if (backup.TrashRetentionDays < 0 || backup.TrashRetentionDays > 3650)
        {
            result.Errors.Add($"TrashRetentionDays out of range: {backup.TrashRetentionDays}. Must be 0–3650.");
        }

        // Path traversal check for trash folder
        if (!string.IsNullOrEmpty(backup.TrashFolderPath))
        {
            ValidatePathSafety(result, backup.TrashFolderPath, "TrashFolderPath");
        }

        // Arr instances validation
        ValidateArrInstances(result, backup.RadarrInstances, "RadarrInstances");
        ValidateArrInstances(result, backup.SonarrInstances, "SonarrInstances");

        // Historical data validation
        ValidateGrowthTimeline(result, backup.GrowthTimeline);
        ValidateGrowthBaseline(result, backup.GrowthBaseline);
        ValidateStatisticsHistory(result, backup.StatisticsHistory);

        return result;
    }

    /// <summary>
    /// Restores backup data into the plugin configuration and data files.
    /// Must be called only after <see cref="Validate"/> returns a valid result.
    /// </summary>
    /// <param name="backup">The validated backup data.</param>
    /// <returns>A summary of what was restored.</returns>
    public BackupRestoreSummary RestoreBackup(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        var summary = new BackupRestoreSummary();

        PluginLogService.LogInfo("Backup", "Starting backup restore...", _logger);

        // Restore configuration
        RestoreConfiguration(backup, summary);

        // Restore growth timeline
        if (backup.GrowthTimeline != null &&
            SaveJsonFile(
                Path.Combine(_dataPath, "jellyfin-helper-growth-timeline.json"),
                backup.GrowthTimeline))
        {
            summary.TimelineRestored = true;
            PluginLogService.LogInfo("Backup", $"Restored growth timeline ({backup.GrowthTimeline.DataPoints.Count} data points)", _logger);
        }

        // Restore growth baseline
        if (backup.GrowthBaseline != null &&
            SaveJsonFile(
                Path.Combine(_dataPath, "jellyfin-helper-growth-baseline.json"),
                backup.GrowthBaseline))
        {
            summary.BaselineRestored = true;
            PluginLogService.LogInfo("Backup", $"Restored growth baseline ({backup.GrowthBaseline.Directories.Count} directories)", _logger);
        }

        // Restore statistics history
        if (backup.StatisticsHistory.Count > 0 &&
            SaveJsonFile(
                Path.Combine(_dataPath, "jellyfin-helper-statistics-history.json"),
                backup.StatisticsHistory))
        {
            summary.HistorySnapshotsRestored = backup.StatisticsHistory.Count;
            PluginLogService.LogInfo("Backup", $"Restored {backup.StatisticsHistory.Count} statistics history snapshots", _logger);
        }

        PluginLogService.LogInfo("Backup", $"Backup restore complete. Config={summary.ConfigurationRestored}, Timeline={summary.TimelineRestored}, Baseline={summary.BaselineRestored}, History={summary.HistorySnapshotsRestored}", _logger);
        return summary;
    }

    /// <summary>
    /// Sanitizes a backup by clamping values to valid ranges and replacing
    /// invalid enum values with defaults. This makes the backup safe to import
    /// even if some fields had warning-level issues.
    /// </summary>
    /// <param name="backup">The backup to sanitize (modified in place).</param>
    public static void Sanitize(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        // Normalize nullable collections (JSON deserialization can set them to null
        // even though the model has default initializers, e.g. "radarrInstances": null)
        backup.RadarrInstances ??= new List<BackupArrInstance>();
        backup.SonarrInstances ??= new List<BackupArrInstance>();
        backup.StatisticsHistory ??= new List<Statistics.StatisticsSnapshot>();

        // Language
        if (string.IsNullOrEmpty(backup.Language) || !ValidLanguages.Contains(backup.Language))
        {
            backup.Language = "en";
        }

        // Log level
        if (string.IsNullOrEmpty(backup.PluginLogLevel) || !ValidLogLevels.Contains(backup.PluginLogLevel))
        {
            backup.PluginLogLevel = "INFO";
        }

        // Task modes
        backup.TrickplayTaskMode = SanitizeTaskMode(backup.TrickplayTaskMode);
        backup.EmptyMediaFolderTaskMode = SanitizeTaskMode(backup.EmptyMediaFolderTaskMode);
        backup.OrphanedSubtitleTaskMode = SanitizeTaskMode(backup.OrphanedSubtitleTaskMode);
        backup.StrmRepairTaskMode = SanitizeTaskMode(backup.StrmRepairTaskMode);

        // Numeric clamping
        backup.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, 3650);
        backup.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, 3650);

        // String truncation
        backup.IncludedLibraries = TruncateString(backup.IncludedLibraries, MaxStringLength);
        backup.ExcludedLibraries = TruncateString(backup.ExcludedLibraries, MaxStringLength);
        backup.TrashFolderPath = TruncateString(backup.TrashFolderPath, MaxStringLength);

        // Arr instances
        SanitizeArrInstances(backup.RadarrInstances);
        SanitizeArrInstances(backup.SonarrInstances);

        // Timeline data points limit
        if (backup.GrowthTimeline != null && backup.GrowthTimeline.DataPoints.Count > MaxTimelineDataPoints)
        {
            var trimmed = backup.GrowthTimeline.DataPoints
                .Skip(backup.GrowthTimeline.DataPoints.Count - MaxTimelineDataPoints)
                .ToList();
            backup.GrowthTimeline.DataPoints.Clear();
            foreach (var point in trimmed)
            {
                backup.GrowthTimeline.DataPoints.Add(point);
            }
        }

        // History snapshots limit
        if (backup.StatisticsHistory.Count > MaxHistorySnapshots)
        {
            var trimmed = backup.StatisticsHistory
                .Skip(backup.StatisticsHistory.Count - MaxHistorySnapshots)
                .ToList();
            backup.StatisticsHistory.Clear();
            foreach (var snap in trimmed)
            {
                backup.StatisticsHistory.Add(snap);
            }
        }

        // Baseline directories limit
        if (backup.GrowthBaseline != null && backup.GrowthBaseline.Directories.Count > MaxBaselineDirectories)
        {
            var keysToRemove = backup.GrowthBaseline.Directories.Keys
                .Skip(MaxBaselineDirectories)
                .ToList();
            foreach (var key in keysToRemove)
            {
                backup.GrowthBaseline.Directories.Remove(key);
            }
        }
    }

    // === Validation helpers ===

    internal static void ValidateStringField(BackupValidationResult result, string? value, string fieldName, int maxLength)
    {
        if (value == null)
        {
            return;
        }

        if (value.Length > maxLength)
        {
            result.Errors.Add($"{fieldName} exceeds maximum length ({value.Length} > {maxLength}).");
        }

        if (ContainsNullBytes(value))
        {
            result.Errors.Add($"{fieldName} contains null bytes (potential binary injection).");
        }

        if (ContainsScriptInjection(value))
        {
            result.Errors.Add($"{fieldName} contains potential script injection content.");
        }
    }

    internal static void ValidateTaskMode(BackupValidationResult result, string? value, string fieldName)
    {
        if (!string.IsNullOrEmpty(value) && !ValidTaskModes.Contains(value))
        {
            result.Warnings.Add($"Unknown task mode '{value}' for {fieldName}. Will default to 'DryRun'.");
        }
    }

    internal static void ValidatePathSafety(BackupValidationResult result, string path, string fieldName)
    {
        // Check for path traversal attempts
        if (path.Contains("..", StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains path traversal characters '..'.");
        }

        // Check for absolute paths trying to escape (but allow absolute paths as they're a valid config)
        // Just reject clearly dangerous patterns
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains null bytes.");
        }

        // Check for pipe/command injection
        if (path.Contains('|', StringComparison.Ordinal) || path.Contains('`', StringComparison.Ordinal) || path.Contains('$', StringComparison.Ordinal) || path.Contains(';', StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains potentially dangerous characters (|, `, $, ;).");
        }

        // Check for newline characters (potential log/header injection)
        if (path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains newline characters.");
        }
    }

    internal static void ValidateArrInstances(BackupValidationResult result, List<BackupArrInstance>? instances, string fieldName)
    {
        if (instances == null)
        {
            return;
        }

        if (instances.Count > MaxArrInstances)
        {
            result.Errors.Add($"{fieldName} has too many instances ({instances.Count} > {MaxArrInstances}).");
        }

        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var prefix = $"{fieldName}[{i}]";

            ValidateStringField(result, instance.Name, $"{prefix}.Name", MaxInstanceNameLength);
            ValidateStringField(result, instance.Url, $"{prefix}.Url", MaxUrlLength);
            ValidateStringField(result, instance.ApiKey, $"{prefix}.ApiKey", MaxApiKeyLength);

            // Validate URL format
            if (!string.IsNullOrEmpty(instance.Url))
            {
                if (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    result.Errors.Add($"{prefix}.Url is not a valid HTTP/HTTPS URL: '{instance.Url}'.");
                }
            }
        }
    }

    internal static void ValidateGrowthTimeline(BackupValidationResult result, GrowthTimelineResult? timeline)
    {
        if (timeline == null)
        {
            return;
        }

        if (timeline.DataPoints.Count > MaxTimelineDataPoints)
        {
            result.Warnings.Add($"GrowthTimeline has {timeline.DataPoints.Count} data points (max {MaxTimelineDataPoints}). Will be trimmed.");
        }

        if (!string.IsNullOrEmpty(timeline.Granularity) && !ValidGranularities.Contains(timeline.Granularity))
        {
            result.Warnings.Add($"Unknown timeline granularity '{timeline.Granularity}'. Will be accepted as-is.");
        }

        // Check for negative cumulative sizes (sanity check)
        foreach (var point in timeline.DataPoints)
        {
            if (point.CumulativeSize < 0)
            {
                result.Warnings.Add($"Timeline data point at {point.Date:O} has negative cumulative size ({point.CumulativeSize}).");
                break; // Only warn once
            }
        }
    }

    internal static void ValidateGrowthBaseline(BackupValidationResult result, GrowthTimelineBaseline? baseline)
    {
        if (baseline == null)
        {
            return;
        }

        if (baseline.Directories.Count > MaxBaselineDirectories)
        {
            result.Warnings.Add($"GrowthBaseline has {baseline.Directories.Count} directories (max {MaxBaselineDirectories}). Will be trimmed.");
        }

        // Check for suspiciously large sizes
        foreach (var kvp in baseline.Directories)
        {
            if (kvp.Value.Size < 0)
            {
                result.Warnings.Add($"Baseline directory '{TruncateForLog(kvp.Key)}' has negative size ({kvp.Value.Size}).");
                break; // Only warn once
            }

            if (kvp.Key.Length > 1000)
            {
                result.Errors.Add($"Baseline directory path exceeds 1000 characters.");
                break;
            }

            if (ContainsScriptInjection(kvp.Key))
            {
                result.Errors.Add("Baseline directory path contains potential script injection content.");
                break;
            }
        }
    }

    internal static void ValidateStatisticsHistory(BackupValidationResult result, List<StatisticsSnapshot>? history)
    {
        if (history == null)
        {
            return;
        }

        if (history.Count > MaxHistorySnapshots)
        {
            result.Warnings.Add($"StatisticsHistory has {history.Count} snapshots (max {MaxHistorySnapshots}). Will be trimmed.");
        }

        foreach (var snapshot in history)
        {
            // Check for negative values
            if (snapshot.TotalSize < 0 || snapshot.TotalVideoFileCount < 0 || snapshot.TotalAudioFileCount < 0)
            {
                result.Warnings.Add($"Statistics snapshot at {snapshot.Timestamp:O} has negative values.");
                break; // Only warn once
            }

            // Check library sizes keys for injection
            foreach (var key in snapshot.LibrarySizes.Keys)
            {
                if (key.Length > MaxStringLength)
                {
                    result.Errors.Add("Statistics history contains library name exceeding max length.");
                    return;
                }

                if (ContainsScriptInjection(key))
                {
                    result.Errors.Add("Statistics history contains library name with potential script injection.");
                    return;
                }
            }
        }
    }

    // === Security helpers ===

    internal static bool ContainsNullBytes(string value)
    {
        return value.Contains('\0', StringComparison.Ordinal);
    }

    internal static bool ContainsScriptInjection(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            return ScriptPattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // If the regex times out, treat as suspicious
            return true;
        }
    }

    // === Private helpers ===

    private void RestoreConfiguration(BackupData backup, BackupRestoreSummary summary)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            PluginLogService.LogWarning("Backup", "Plugin instance not available, skipping configuration restore.", logger: _logger);
            return;
        }

        var config = plugin.Configuration;

        // Restore preferences
        config.Language = ValidLanguages.Contains(backup.Language) ? backup.Language : "en";
        config.IncludedLibraries = backup.IncludedLibraries ?? string.Empty;
        config.ExcludedLibraries = backup.ExcludedLibraries ?? string.Empty;
        config.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, 3650);
        config.PluginLogLevel = ValidLogLevels.Contains(backup.PluginLogLevel) ? backup.PluginLogLevel : "INFO";

        // Task modes
        config.TrickplayTaskMode = ParseTaskMode(backup.TrickplayTaskMode);
        config.EmptyMediaFolderTaskMode = ParseTaskMode(backup.EmptyMediaFolderTaskMode);
        config.OrphanedSubtitleTaskMode = ParseTaskMode(backup.OrphanedSubtitleTaskMode);
        config.StrmRepairTaskMode = ParseTaskMode(backup.StrmRepairTaskMode);

        // Trash settings
        config.UseTrash = backup.UseTrash;
        config.TrashFolderPath = backup.TrashFolderPath ?? ".jellyfin-trash";
        config.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, 3650);

        // Arr instances
        config.RadarrInstances.Clear();
        foreach (var instance in backup.RadarrInstances.Take(MaxArrInstances))
        {
            config.RadarrInstances.Add(new ArrInstanceConfig
            {
                Name = TruncateString(instance.Name, MaxInstanceNameLength),
                Url = TruncateString(instance.Url, MaxUrlLength),
                ApiKey = TruncateString(instance.ApiKey, MaxApiKeyLength),
            });
        }

        config.SonarrInstances.Clear();
        foreach (var instance in backup.SonarrInstances.Take(MaxArrInstances))
        {
            config.SonarrInstances.Add(new ArrInstanceConfig
            {
                Name = TruncateString(instance.Name, MaxInstanceNameLength),
                Url = TruncateString(instance.Url, MaxUrlLength),
                ApiKey = TruncateString(instance.ApiKey, MaxApiKeyLength),
            });
        }

        // Update legacy fields for backwards compatibility
        if (config.RadarrInstances.Count > 0)
        {
            config.RadarrUrl = config.RadarrInstances[0].Url;
            config.RadarrApiKey = config.RadarrInstances[0].ApiKey;
        }
        else
        {
            config.RadarrUrl = string.Empty;
            config.RadarrApiKey = string.Empty;
        }

        if (config.SonarrInstances.Count > 0)
        {
            config.SonarrUrl = config.SonarrInstances[0].Url;
            config.SonarrApiKey = config.SonarrInstances[0].ApiKey;
        }
        else
        {
            config.SonarrUrl = string.Empty;
            config.SonarrApiKey = string.Empty;
        }

        plugin.SaveConfiguration();
        summary.ConfigurationRestored = true;
        PluginLogService.LogInfo("Backup", "Configuration restored from backup.", _logger);
    }

    private static TaskMode ParseTaskMode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return TaskMode.DryRun;
        }

        if (Enum.TryParse<TaskMode>(value, true, out var mode))
        {
            return mode;
        }

        return TaskMode.DryRun;
    }

    private static string SanitizeTaskMode(string? value)
    {
        if (string.IsNullOrEmpty(value) || !ValidTaskModes.Contains(value))
        {
            return "DryRun";
        }

        // Normalize casing
        return value switch
        {
            var v when v.Equals("Activate", StringComparison.OrdinalIgnoreCase) => "Activate",
            var v when v.Equals("DryRun", StringComparison.OrdinalIgnoreCase) => "DryRun",
            var v when v.Equals("Deactivate", StringComparison.OrdinalIgnoreCase) => "Deactivate",
            _ => "DryRun",
        };
    }

    private static string TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static string TruncateForLog(string value)
    {
        const int maxLogLength = 80;
        if (value.Length <= maxLogLength)
        {
            return value;
        }

        return value[..maxLogLength] + "...";
    }

    private static void SanitizeArrInstances(List<BackupArrInstance>? instances)
    {
        if (instances == null)
        {
            return;
        }

        // Limit count
        while (instances.Count > MaxArrInstances)
        {
            instances.RemoveAt(instances.Count - 1);
        }

        foreach (var instance in instances)
        {
            instance.Name = TruncateString(instance.Name, MaxInstanceNameLength);
            instance.Url = TruncateString(instance.Url, MaxUrlLength);
            instance.ApiKey = TruncateString(instance.ApiKey, MaxApiKeyLength);
        }
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
            PluginLogService.LogWarning("Backup", $"Could not load {filePath} for backup", ex, _logger);
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
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PluginLogService.LogError("Backup", $"Could not save {filePath} during restore", ex, _logger);
            return false;
        }
    }
}
