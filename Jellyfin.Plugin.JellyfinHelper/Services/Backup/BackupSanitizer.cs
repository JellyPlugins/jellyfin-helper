using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

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

        // Clamp negative cumulative values to 0. A cumulative byte size / file count is
        // physically non-negative; the validator only WARNS on negatives, and the timeline
        // is written to the cache VERBATIM on restore, so without this a hostile/corrupt
        // backup could plant a negative point that surfaces on GET GrowthTimeline and even
        // survives a recompute (the append-only path preserves historical points, and
        // TrimLeadingZeros keeps one leading point). Matches the Math.Clamp treatment every
        // other backup numeric already gets and the aggregator's own Math.Max(0, …) output guard.
        if (backup.GrowthTimeline != null)
        {
            foreach (var point in backup.GrowthTimeline.DataPoints)
            {
                point.CumulativeSize = Math.Max(0, point.CumulativeSize);
                point.CumulativeFileCount = Math.Max(0, point.CumulativeFileCount);
            }
        }

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

        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private static void SanitizeArrInstances(List<BackupArrInstance>? instances)
    {
        if (instances == null)
        {
            return;
        }

        // Drop any null entries FIRST — a backup JSON can contain `[null]` in the
        // instance array, which would otherwise NRE below (sanitize runs before
        // validation, so the validator's null guard hasn't executed yet). This must
        // precede the count cap so a leading null can't consume a valid instance's slot
        // (e.g. [null, v1..v10] must keep v1..vN, not drop vN while retaining the null).
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