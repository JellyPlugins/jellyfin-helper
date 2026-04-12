using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Helper methods that apply plugin configuration rules to cleanup operations.
/// Provides library filtering, orphan age checking, trash/delete resolution, and task mode queries.
/// </summary>
public static class CleanupConfigHelper
{
    /// <summary>
    /// Gets or sets a configuration override for testing purposes.
    /// When set, <see cref="GetConfig"/> returns this instead of the plugin configuration.
    /// </summary>
    internal static PluginConfiguration? ConfigOverride { get; set; }

    /// <summary>
    /// Gets the current plugin configuration, or a default configuration if the plugin is not available.
    /// Automatically triggers migration from legacy booleans to <see cref="TaskMode"/> on first access.
    /// </summary>
    /// <returns>The plugin configuration.</returns>
    public static PluginConfiguration GetConfig()
    {
        if (ConfigOverride != null)
        {
            return ConfigOverride;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // One-time migration from legacy booleans to TaskMode
        if (config.ConfigVersion < 1)
        {
            PluginLogService.LogInfo("Config", "Migrating configuration from legacy booleans to TaskMode (ConfigVersion < 1).");
            config.MigrateFromLegacyBooleans();
            Plugin.Instance?.SaveConfiguration();
            PluginLogService.LogInfo("Config", "Configuration migration completed.");
        }

        return config;
    }

    // ===== TaskMode queries =====

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Trickplay Folder Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    public static TaskMode GetTrickplayTaskMode() => GetConfig().TrickplayTaskMode;

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Empty Media Folder Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    public static TaskMode GetEmptyMediaFolderTaskMode() => GetConfig().EmptyMediaFolderTaskMode;

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Orphaned Subtitle Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    public static TaskMode GetOrphanedSubtitleTaskMode() => GetConfig().OrphanedSubtitleTaskMode;

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the .strm File Repair task.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    public static TaskMode GetStrmRepairTaskMode() => GetConfig().StrmRepairTaskMode;

    /// <summary>
    /// Determines whether a task should run in dry-run mode based on its <see cref="TaskMode"/>.
    /// Returns true for <see cref="TaskMode.DryRun"/>, false for <see cref="TaskMode.Activate"/>.
    /// Should NOT be called if the task mode is <see cref="TaskMode.Deactivate"/> (check first).
    /// </summary>
    /// <param name="mode">The task mode.</param>
    /// <returns>True if the task should run in dry-run mode.</returns>
    public static bool IsDryRun(TaskMode mode) => mode != TaskMode.Activate;

    /// <summary>
    /// Determines whether the Trickplay Folder Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    public static bool IsDryRunTrickplay() => IsDryRun(GetConfig().TrickplayTaskMode);

    /// <summary>
    /// Determines whether the Empty Media Folder Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    public static bool IsDryRunEmptyMediaFolders() => IsDryRun(GetConfig().EmptyMediaFolderTaskMode);

    /// <summary>
    /// Determines whether the Orphaned Subtitle Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    public static bool IsDryRunOrphanedSubtitles() => IsDryRun(GetConfig().OrphanedSubtitleTaskMode);

    /// <summary>
    /// Determines whether the .strm File Repair task should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    public static bool IsDryRunStrmRepair() => IsDryRun(GetConfig().StrmRepairTaskMode);

    /// <summary>
    /// Gets the filtered library locations based on the whitelist/blacklist configuration.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>A filtered, deduplicated list of library root paths.</returns>
    public static IReadOnlyList<string> GetFilteredLibraryLocations(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var config = GetConfig();
        var virtualFolders = libraryManager.GetVirtualFolders();

        var includedSet = ParseCommaSeparated(config.IncludedLibraries);
        var excludedSet = ParseCommaSeparated(config.ExcludedLibraries);

        PluginLogService.LogDebug("Config", $"Library filter: included=[{string.Join(", ", includedSet)}], excluded=[{string.Join(", ", excludedSet)}]");

        var filteredFolders = virtualFolders.Where(f =>
        {
            var name = f.Name ?? string.Empty;

            // Always exclude non-video library types:
            // - Music libraries contain no video files, so every folder would be flagged as orphaned
            // - Boxsets (Collections) are Jellyfin-internal and must never be touched
            if (f.CollectionType is CollectionTypeOptions.music or CollectionTypeOptions.boxsets)
            {
                return false;
            }

            // Fallback: also exclude by name pattern in case CollectionType is null/unknown
            // (e.g. for manually created or migrated libraries)
            if (name.Contains("collection", StringComparison.OrdinalIgnoreCase)
                || name.Contains("boxset", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // If whitelist is set, only include listed libraries
            if (includedSet.Count > 0 && !includedSet.Contains(name))
            {
                return false;
            }

            // If blacklist is set, exclude listed libraries
            if (excludedSet.Count > 0 && excludedSet.Contains(name))
            {
                return false;
            }

            return true;
        });

        // Additional safety: filter out any locations that point to Jellyfin's internal
        // collections directory (typically /config/data/collections or similar).
        return filteredFolders
            .SelectMany(f => f.Locations)
            .Where(loc => !loc.Contains("/collections", StringComparison.OrdinalIgnoreCase)
                       && !loc.Contains("\\collections", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks whether a directory is old enough to be considered an orphan based on the configured minimum age.
    /// </summary>
    /// <param name="directoryPath">The path to the directory.</param>
    /// <returns>True if the directory is old enough (or age check is disabled), false if it's too new.</returns>
    public static bool IsOldEnoughForDeletion(string directoryPath)
    {
        var config = GetConfig();
        if (config.OrphanMinAgeDays <= 0)
        {
            return true;
        }

        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            if (!dirInfo.Exists)
            {
                return false;
            }

            var age = DateTime.UtcNow - dirInfo.CreationTimeUtc;
            return age.TotalDays >= config.OrphanMinAgeDays;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we can't check, err on the safe side and skip cleanup
            PluginLogService.LogWarning("Config", $"Could not check directory age for: {directoryPath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Checks whether a file is old enough to be considered an orphan based on the configured minimum age.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <returns>True if the file is old enough (or age check is disabled), false if it's too new.</returns>
    public static bool IsFileOldEnoughForDeletion(string filePath)
    {
        var config = GetConfig();
        if (config.OrphanMinAgeDays <= 0)
        {
            return true;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            var age = DateTime.UtcNow - fileInfo.CreationTimeUtc;
            return age.TotalDays >= config.OrphanMinAgeDays;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we can't check, err on the safe side and skip cleanup
            PluginLogService.LogWarning("Config", $"Could not check file age for: {filePath}", ex);
            return false;
        }
    }

    /// <summary>
    /// Gets the resolved trash folder path for a given library root.
    /// If the configured path is relative, it is resolved relative to the library root.
    /// </summary>
    /// <param name="libraryRootPath">The library root path.</param>
    /// <returns>The full trash folder path.</returns>
    public static string GetTrashPath(string libraryRootPath)
    {
        var config = GetConfig();
        var trashPath = config.TrashFolderPath;

        if (string.IsNullOrWhiteSpace(trashPath))
        {
            trashPath = ".jellyfin-trash";
        }

        if (Path.IsPathRooted(trashPath))
        {
            return trashPath;
        }

        return Path.Combine(libraryRootPath, trashPath);
    }

    /// <summary>
    /// Parses a comma-separated string into a case-insensitive hash set of trimmed, non-empty values.
    /// </summary>
    /// <param name="value">The comma-separated input string.</param>
    /// <returns>A hash set of parsed values.</returns>
    internal static HashSet<string> ParseCommaSeparated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}