using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Provides comprehensive validation for backup data, checking for malicious content,
///     out-of-range values, and structural integrity.
/// </summary>
public static class BackupValidator
{
    /// <summary>
    ///     Maximum allowed backup version for import.
    /// </summary>
    private const int MaxBackupVersion = 1;

    /// <summary>
    ///     Maximum number of growth timeline data points allowed in a backup.
    /// </summary>
    internal const int MaxTimelineDataPoints = 5000;

    /// <summary>
    ///     Maximum number of baseline directory entries allowed in a backup.
    ///     Each top-level media directory (movie folder, TV show folder, etc.) is one entry.
    ///     50,000 supports very large media servers.
    /// </summary>
    internal const int MaxBaselineDirectories = 50_000;

    /// <summary>
    ///     Maximum number of Arr instances per type (Radarr/Sonarr).
    /// </summary>
    internal const int MaxArrInstances = 3;

    /// <summary>
    ///     Maximum retention or minimum age in days (≈ 10 years).
    /// </summary>
    internal const int MaxRetentionDays = 3650;

    /// <summary>
    ///     Maximum number of recommendations per user allowed in a backup.
    /// </summary>
    internal const int MaxRecommendationCount = 100;

    /// <summary>
    ///     Maximum string length for general text fields (library names, paths, etc.).
    /// </summary>
    internal const int MaxStringLength = 1000;

    /// <summary>
    ///     Maximum string length for URL fields.
    /// </summary>
    internal const int MaxUrlLength = 500;

    /// <summary>
    ///     Maximum string length for API key fields.
    /// </summary>
    internal const int MaxApiKeyLength = 200;

    /// <summary>
    ///     Maximum string length for instance name fields.
    /// </summary>
    internal const int MaxInstanceNameLength = 100;

    /// <summary>
    ///     Valid language codes.
    /// </summary>
    internal static readonly HashSet<string> ValidLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "de", "fr", "es", "pt", "zh", "tr"
    };

    /// <summary>
    ///     Valid task mode values.
    /// </summary>
    internal static readonly HashSet<string> ValidTaskModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deactivate", "DryRun", "Activate"
    };

    /// <summary>
    ///     Valid log levels.
    /// </summary>
    internal static readonly HashSet<string> ValidLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEBUG", "INFO", "WARN", "ERROR"
    };

    /// <summary>
    ///     Valid timeline granularity values.
    /// </summary>
    private static readonly HashSet<string> ValidGranularities = new(StringComparer.OrdinalIgnoreCase)
    {
        "daily", "weekly", "monthly", "quarterly", "yearly"
    };

    // Regex to detect script injection in string fields
    private static readonly Regex ScriptPattern = new(
        @"<\s*script|javascript\s*:|on\w+\s*=|<\s*iframe|<\s*object|<\s*embed|<\s*form|<\s*svg\s+on",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    ///     Validates backup data comprehensively, checking for malicious content,
    ///     out-of-range values, and structural integrity.
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
        if (backup.BackupVersion is < 1 or > MaxBackupVersion)
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
        ValidateStringField(result, backup.LinkRepairTaskMode, "LinkRepairTaskMode", MaxStringLength);
        ValidateStringField(result, backup.SeerrCleanupTaskMode, "SeerrCleanupTaskMode", MaxStringLength);
        ValidateStringField(result, backup.SeerrUrl, "SeerrUrl", MaxUrlLength);
        ValidateStringField(result, backup.SeerrApiKey, "SeerrApiKey", MaxApiKeyLength);
        ValidateStringField(result, backup.RecommendationsTaskMode, "RecommendationsTaskMode", MaxStringLength);

        if (!string.IsNullOrEmpty(backup.SeerrUrl) &&
            (!Uri.TryCreate(backup.SeerrUrl, UriKind.Absolute, out var seerrUri) ||
             (seerrUri.Scheme != Uri.UriSchemeHttp && seerrUri.Scheme != Uri.UriSchemeHttps)))
        {
            result.Errors.Add($"SeerrUrl is not a valid HTTP/HTTPS URL: '{backup.SeerrUrl}'.");
        }

        // Enum validation
        if (!string.IsNullOrEmpty(backup.Language) && !ValidLanguages.Contains(backup.Language))
        {
            result.Warnings.Add($"Unknown language '{backup.Language}'. Will default to 'en'.");
        }

        ValidateTaskMode(result, backup.TrickplayTaskMode, "TrickplayTaskMode");
        ValidateTaskMode(result, backup.EmptyMediaFolderTaskMode, "EmptyMediaFolderTaskMode");
        ValidateTaskMode(result, backup.OrphanedSubtitleTaskMode, "OrphanedSubtitleTaskMode");
        ValidateTaskMode(result, backup.LinkRepairTaskMode, "LinkRepairTaskMode");
        ValidateTaskMode(result, backup.SeerrCleanupTaskMode, "SeerrCleanupTaskMode", "Deactivate");
        ValidateTaskMode(result, backup.RecommendationsTaskMode, "RecommendationsTaskMode");

        if (!string.IsNullOrEmpty(backup.PluginLogLevel) && !ValidLogLevels.Contains(backup.PluginLogLevel))
        {
            result.Warnings.Add($"Unknown log level '{backup.PluginLogLevel}'. Will default to 'INFO'.");
        }

        // Numeric range validation
        if (backup.OrphanMinAgeDays < 0 || backup.OrphanMinAgeDays > MaxRetentionDays)
        {
            result.Errors.Add($"OrphanMinAgeDays out of range: {backup.OrphanMinAgeDays}. Must be 0–{MaxRetentionDays}.");
        }

        if (backup.TrashRetentionDays < 0 || backup.TrashRetentionDays > MaxRetentionDays)
        {
            result.Errors.Add($"TrashRetentionDays out of range: {backup.TrashRetentionDays}. Must be 0–{MaxRetentionDays}.");
        }

        // Older backups do not contain this field and deserialize it as 0 — treat as absent.
        if (backup.SeerrCleanupAgeDays != 0 &&
            (backup.SeerrCleanupAgeDays < 1 || backup.SeerrCleanupAgeDays > MaxRetentionDays))
        {
            result.Errors.Add($"SeerrCleanupAgeDays out of range: {backup.SeerrCleanupAgeDays}. Must be 1–{MaxRetentionDays}.");
        }

        // Smart Recommendations — older backups default to 0 (treat as absent)
        if (backup.RecommendationCount != 0 &&
            (backup.RecommendationCount < 1 || backup.RecommendationCount > MaxRecommendationCount))
        {
            result.Errors.Add($"RecommendationCount out of range: {backup.RecommendationCount}. Must be 1–{MaxRecommendationCount}.");
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

        return result;
    }

    // === Validation helpers ===

    private static void ValidateStringField(
        BackupValidationResult result,
        string? value,
        string fieldName,
        int maxLength)
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

    private static void ValidateTaskMode(BackupValidationResult result, string? value, string fieldName, string fallback = "DryRun")
    {
        if (!string.IsNullOrEmpty(value) && !ValidTaskModes.Contains(value))
        {
            result.Warnings.Add($"Unknown task mode '{value}' for {fieldName}. Will default to '{fallback}'.");
        }
    }

    private static void ValidatePathSafety(BackupValidationResult result, string path, string fieldName)
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
        if (path.Contains('|', StringComparison.Ordinal) || path.Contains('`', StringComparison.Ordinal) ||
            path.Contains(';', StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains potentially dangerous characters (|, `, ;).");
        }

        // Check for shell substitution patterns ($(...) or ${...}) but allow bare '$'
        // since it's valid in Windows UNC paths (e.g. \\server\C$\share)
        if (path.Contains("$(", StringComparison.Ordinal) || path.Contains("${", StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains shell substitution pattern ($( or ${{).");
        }

        // Check for newline characters (potential log/header injection)
        if (path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal))
        {
            result.Errors.Add($"{fieldName} contains newline characters.");
        }
    }

    private static void ValidateArrInstances(
        BackupValidationResult result,
        List<BackupArrInstance>? instances,
        string fieldName)
    {
        if (instances == null)
        {
            return;
        }

        if (instances.Count > MaxArrInstances)
        {
            result.Errors.Add($"{fieldName} has too many instances ({instances.Count} > {MaxArrInstances}).");
        }

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (instance == null)
            {
                result.Errors.Add($"{fieldName}[{i}] is null.");
                continue;
            }

            var prefix = $"{fieldName}[{i}]";

            ValidateStringField(result, instance.Name, $"{prefix}.Name", MaxInstanceNameLength);
            ValidateStringField(result, instance.Url, $"{prefix}.Url", MaxUrlLength);
            ValidateStringField(result, instance.ApiKey, $"{prefix}.ApiKey", MaxApiKeyLength);

            // Validate URL format
            if (string.IsNullOrEmpty(instance.Url))
            {
                continue;
            }

            if (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                result.Errors.Add($"{prefix}.Url is not a valid HTTP/HTTPS URL: '{instance.Url}'.");
            }
        }
    }

    private static void ValidateGrowthTimeline(BackupValidationResult result, GrowthTimelineResult? timeline)
    {
        if (timeline == null)
        {
            return;
        }

        if (timeline.DataPoints.Count > MaxTimelineDataPoints)
        {
            result.Warnings.Add(
                $"GrowthTimeline has {timeline.DataPoints.Count} data points (max {MaxTimelineDataPoints}). Will be trimmed.");
        }

        if (!string.IsNullOrEmpty(timeline.Granularity) && !ValidGranularities.Contains(timeline.Granularity))
        {
            result.Warnings.Add($"Unknown timeline granularity '{timeline.Granularity}'. Will be accepted as-is.");
        }

        // Check for negative cumulative sizes and file counts (sanity check)
        var warnedNegativeSize = false;
        var warnedNegativeCount = false;
        foreach (var point in timeline.DataPoints)
        {
            if (!warnedNegativeSize && point.CumulativeSize < 0)
            {
                result.Warnings.Add(
                    $"Timeline data point at {point.Date:O} has negative cumulative size ({point.CumulativeSize}).");
                warnedNegativeSize = true;
            }

            if (!warnedNegativeCount && point.CumulativeFileCount < 0)
            {
                result.Warnings.Add(
                    $"Timeline data point at {point.Date:O} has negative cumulative file count ({point.CumulativeFileCount}).");
                warnedNegativeCount = true;
            }

            if (warnedNegativeSize && warnedNegativeCount)
            {
                break;
            }
        }
    }

    private static void ValidateGrowthBaseline(BackupValidationResult result, GrowthTimelineBaseline? baseline)
    {
        if (baseline == null)
        {
            return;
        }

        if (baseline.Directories.Count > MaxBaselineDirectories)
        {
            result.Warnings.Add(
                $"GrowthBaseline has {baseline.Directories.Count} directories (max {MaxBaselineDirectories}). Will be trimmed.");
        }

        // Check for suspiciously large sizes and security issues
        var warnedNegativeSize = false;
        var warnedNegativeCount = false;
        foreach (var kvp in baseline.Directories)
        {
            if (!warnedNegativeSize && kvp.Value.Size < 0)
            {
                result.Warnings.Add(
                    $"Baseline directory '{TruncateForLog(kvp.Key)}' has negative size ({kvp.Value.Size}).");
                warnedNegativeSize = true;
            }

            if (!warnedNegativeCount && kvp.Value.Count < 0)
            {
                result.Warnings.Add(
                    $"Baseline directory '{TruncateForLog(kvp.Key)}' has negative count ({kvp.Value.Count}).");
                warnedNegativeCount = true;
            }

            if (kvp.Key.Length > 1000)
            {
                result.Errors.Add("Baseline directory path exceeds 1000 characters.");
                break;
            }

            if (!ContainsScriptInjection(kvp.Key))
            {
                continue;
            }

            result.Errors.Add("Baseline directory path contains potential script injection content.");
            break;
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

    private static string TruncateForLog(string value)
    {
        const int maxLogLength = 80;
        if (value.Length <= maxLogLength)
        {
            return value;
        }

        return value[..maxLogLength] + "...";
    }
}