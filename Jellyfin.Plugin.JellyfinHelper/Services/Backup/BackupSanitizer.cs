using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Backup;

/// <summary>
///     Sanitizes backup data by clamping values to valid ranges and replacing
///     invalid enum values with defaults. Makes backup data safe to import
///     even if some fields had warning-level issues.
/// </summary>
public static class BackupSanitizer
{
    /// <summary>
    ///     Sanitizes a backup by clamping values to valid ranges and replacing
    ///     invalid enum values with defaults. This makes the backup safe to import
    ///     even if some fields had warning-level issues.
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
        if (backup.SeerrCleanupAgeDays.HasValue && backup.SeerrCleanupAgeDays.Value != 0)
        {
            backup.SeerrCleanupAgeDays = Math.Clamp(backup.SeerrCleanupAgeDays.Value, 1, BackupValidator.MaxRetentionDays);
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

        // Timeline data points limit - keep only the newest MaxTimelineDataPoints entries
        if (backup.GrowthTimeline is { DataPoints.Count: > BackupValidator.MaxTimelineDataPoints })
        {
            // Sort ascending, drop the oldest (front), keep only the newest MaxTimelineDataPoints.
            // Collection<T> has no Sort/RemoveRange, so we rebuild in two passes without an
            // extra intermediate List: sort all in place via index-swap, then trim the front.
            var pts = backup.GrowthTimeline.DataPoints;
            var count = pts.Count;

            // Insertion sort — the list is nearly sorted in practice, so this is O(n) typical.
            for (var i = 1; i < count; i++)
            {
                var key = pts[i];
                var j = i - 1;
                while (j >= 0 && pts[j].Date > key.Date)
                {
                    pts[j + 1] = pts[j];
                    j--;
                }

                pts[j + 1] = key;
            }

            var excess = count - BackupValidator.MaxTimelineDataPoints;
            for (var i = 0; i < excess; i++)
            {
                pts.RemoveAt(0);
            }
        }

        // Baseline directories limit
        // Oldest entries lexicographically are trimmed when over MaxBaselineDirectories
        if (backup.GrowthBaseline == null || backup.GrowthBaseline.Directories.Count <= BackupValidator.MaxBaselineDirectories)
        {
            return;
        }

        var keysToRemove = backup.GrowthBaseline.Directories
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
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

        // Normalize casing. Every value that reaches this point is guaranteed to be a
        // case-insensitive member of ValidTaskModes, so the exhaustive arms always match.
        // The throw arm is intentionally unreachable today; it will surface a compile-time
        // gap if a new mode is added to ValidTaskModes without updating this switch.
        return value switch
        {
            _ when value.Equals("Activate", StringComparison.OrdinalIgnoreCase) => "Activate",
            _ when value.Equals("DryRun", StringComparison.OrdinalIgnoreCase) => "DryRun",
            _ when value.Equals("Deactivate", StringComparison.OrdinalIgnoreCase) => "Deactivate",
            _ => throw new InvalidOperationException($"ValidTaskModes contains '{value}' but SanitizeTaskMode has no normalization arm for it.")
        };
    }

    internal static string TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static void SanitizeArrInstances(List<BackupArrInstance>? instances)
    {
        if (instances == null)
        {
            return;
        }

        // Limit count
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