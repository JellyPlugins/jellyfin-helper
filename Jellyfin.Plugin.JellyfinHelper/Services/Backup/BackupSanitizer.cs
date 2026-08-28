using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Sanitizes backup data by clamping values to valid ranges and replacing invalid enum values with defaults.
/// </summary>
public static class BackupSanitizer
{
    /// <summary>
    ///     Sanitizes a backup by clamping values to valid ranges and replacing invalid enum values with defaults.
    /// </summary>
    /// <param name="backup">The backup to sanitize (modified in place).</param>
    public static void Sanitize(BackupData backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        // Language
        if (string.IsNullOrEmpty(backup.Language) || !BackupValidator.ValidLanguages.Contains(backup.Language))
        {
            backup.Language = "en";
        }

        // Log level
        if (string.IsNullOrEmpty(backup.PluginLogLevel) || !BackupValidator.ValidLogLevels.Contains(backup.PluginLogLevel))
        {
            backup.PluginLogLevel = "INFO";
        }

        // Task modes
        backup.TrickplayTaskMode = SanitizeTaskMode(backup.TrickplayTaskMode);
        backup.EmptyMediaFolderTaskMode = SanitizeTaskMode(backup.EmptyMediaFolderTaskMode);
        backup.OrphanedSubtitleTaskMode = SanitizeTaskMode(backup.OrphanedSubtitleTaskMode);
        backup.LinkRepairTaskMode = SanitizeTaskMode(backup.LinkRepairTaskMode);

        // Numeric clamping
        backup.OrphanMinAgeDays = Math.Clamp(backup.OrphanMinAgeDays, 0, BackupValidator.MaxRetentionDays);
        backup.TrashRetentionDays = Math.Clamp(backup.TrashRetentionDays, 0, BackupValidator.MaxRetentionDays);
        if (backup.SeerrCleanupAgeDays.HasValue)
        {
            backup.SeerrCleanupAgeDays = Math.Clamp(backup.SeerrCleanupAgeDays.Value, 0, BackupValidator.MaxRetentionDays);
        }

        // String truncation
        backup.ExcludedLibraries = TruncateString(backup.ExcludedLibraries, BackupValidator.MaxStringLength);
        backup.TrashFolderPath = TruncateString(backup.TrashFolderPath, BackupValidator.MaxStringLength);

        // Seerr task mode (default is Deactivate, not DryRun - Seerr deletes data)
        backup.SeerrCleanupTaskMode = SanitizeTaskMode(backup.SeerrCleanupTaskMode, "Deactivate");

        // Smart Recommendations (only task mode - count and strategy are not backed up)
        backup.RecommendationsTaskMode = SanitizeTaskMode(backup.RecommendationsTaskMode);

        // Arr instances
        SanitizeArrInstances(backup.RadarrInstances);
        SanitizeArrInstances(backup.SonarrInstances);

        SanitizeGrowthTimeline(backup);
        SanitizeGrowthBaseline(backup);
    }

    /// <summary>
    ///     Trims the growth timeline to the newest MaxTimelineDataPoints entries and clamps each data point's cumulative size / file count to a non-negative value.
    /// </summary>
    /// <param name="backup">The backup whose growth timeline is sanitized in place.</param>
    private static void SanitizeGrowthTimeline(BackupData backup)
    {
        // Timeline data points limit - keep only the newest MaxTimelineDataPoints entries
        if (backup.GrowthTimeline is { DataPoints.Count: > BackupValidator.MaxTimelineDataPoints })
        {
            var kept = backup.GrowthTimeline.DataPoints
                .OrderByDescending(p => p.Date)
                .Take(BackupValidator.MaxTimelineDataPoints)
                .OrderBy(p => p.Date)
                .ToList();

            backup.GrowthTimeline.DataPoints.Clear();
            foreach (var point in kept)
            {
                backup.GrowthTimeline.DataPoints.Add(point);
            }
        }

        // Clamp negative cumulative values to 0.
        if (backup.GrowthTimeline != null)
        {
            foreach (var point in backup.GrowthTimeline.DataPoints)
            {
                point.CumulativeSize = Math.Max(0, point.CumulativeSize);
                point.CumulativeFileCount = Math.Max(0, point.CumulativeFileCount);
            }
        }
    }

    /// <summary>
    ///     Clamps the growth baseline's per-directory size / count to non-negative values and trims the oldest entries by CreatedUtc when over MaxBaselineDirectories.
    /// </summary>
    /// <param name="backup">The backup whose growth baseline is sanitized in place.</param>
    private static void SanitizeGrowthBaseline(BackupData backup)
    {
        // Same non-negativity guarantee for the growth baseline's per-directory size/count,
        // which is likewise warn-only in the validator and written verbatim on restore.
        if (backup.GrowthBaseline != null)
        {
            foreach (var entry in backup.GrowthBaseline.Directories.Values)
            {
                entry.Size = Math.Max(0, entry.Size);
                entry.Count = Math.Max(0, entry.Count);
            }
        }

        // Baseline directories limit
        // Oldest entries by CreatedUtc are trimmed when over MaxBaselineDirectories
        if (backup.GrowthBaseline == null || backup.GrowthBaseline.Directories.Count <= BackupValidator.MaxBaselineDirectories)
        {
            return;
        }

        var keysToRemove = backup.GrowthBaseline.Directories
            .OrderByDescending(kvp => kvp.Value.CreatedUtc)
            .Skip(BackupValidator.MaxBaselineDirectories)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in keysToRemove)
        {
            backup.GrowthBaseline.Directories.Remove(key);
        }
    }

    private static string SanitizeTaskMode(string? value, string fallback = "DryRun")
    {
        if (string.IsNullOrEmpty(value) || !BackupValidator.ValidTaskModes.Contains(value))
        {
            return fallback;
        }

        // Normalize casing
        return value switch
        {
            _ when value.Equals("Activate", StringComparison.OrdinalIgnoreCase) => "Activate",
            _ when value.Equals("DryRun", StringComparison.OrdinalIgnoreCase) => "DryRun",
            _ when value.Equals("Deactivate", StringComparison.OrdinalIgnoreCase) => "Deactivate",
            _ => fallback
        };
    }

    internal static string TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        // Avoid splitting a UTF-16 surrogate pair (emoji, astral-plane CJK): if the last retained code unit is a high surrogate with no low surrogate following it, drop it so the result is never ill-formed UTF-16 that could corrupt on re-serialization.
        var end = maxLength;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    private static void SanitizeArrInstances(List<BackupArrInstance>? instances)
    {
        if (instances == null)
        {
            return;
        }

        // Drop any null entries FIRST - a backup JSON can contain `[null]` in the instance array, which would otherwise NRE below (sanitize runs before validation, so the validator's null guard hasn't executed yet).
        instances.RemoveAll(i => i is null);

        // Limit count (after null removal, so only real instances count toward the cap).
        while (instances.Count > BackupValidator.MaxArrInstances)
        {
            instances.RemoveAt(instances.Count - 1);
        }

        foreach (var instance in instances)
        {
            instance.Name = TruncateString(instance.Name, BackupValidator.MaxInstanceNameLength);
            instance.Url = TruncateString(instance.Url, BackupValidator.MaxUrlLength);
            instance.ApiKey = TruncateString(instance.ApiKey, BackupValidator.MaxApiKeyLength);
        }
    }
}