using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Helper that applies plugin configuration rules to cleanup operations.
///     Provides library filtering, orphan age checking, trash/delete resolution, and task mode queries.
///     Registered as a singleton via DI; reads configuration from <see cref="IPluginConfigurationService" />.
/// </summary>
public class CleanupConfigHelper : ICleanupConfigHelper
{
    private readonly IPluginConfigurationService _configService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanupConfigHelper" /> class.
    /// </summary>
    /// <param name="configService">The plugin configuration service.</param>
    public CleanupConfigHelper(IPluginConfigurationService configService)
    {
        _configService = configService;
    }

    // ===== Instance members (config access via IPluginConfigurationService) =====

    /// <inheritdoc />
    public PluginConfiguration GetConfig()
    {
        var config = _configService.GetConfiguration();
        return config;
    }

    /// <inheritdoc />
    public TaskMode GetTrickplayTaskMode()
    {
        return GetConfig().TrickplayTaskMode;
    }

    /// <inheritdoc />
    public TaskMode GetEmptyMediaFolderTaskMode()
    {
        return GetConfig().EmptyMediaFolderTaskMode;
    }

    /// <inheritdoc />
    public TaskMode GetOrphanedSubtitleTaskMode()
    {
        return GetConfig().OrphanedSubtitleTaskMode;
    }

    /// <inheritdoc />
    public TaskMode GetStrmRepairTaskMode()
    {
        return GetConfig().StrmRepairTaskMode;
    }

    /// <inheritdoc />
    public bool IsDryRunTrickplay()
    {
        return IsDryRun(GetConfig().TrickplayTaskMode);
    }

    /// <inheritdoc />
    public bool IsDryRunEmptyMediaFolders()
    {
        return IsDryRun(GetConfig().EmptyMediaFolderTaskMode);
    }

    /// <inheritdoc />
    public bool IsDryRunOrphanedSubtitles()
    {
        return IsDryRun(GetConfig().OrphanedSubtitleTaskMode);
    }

    /// <inheritdoc />
    public bool IsDryRunStrmRepair()
    {
        return IsDryRun(GetConfig().StrmRepairTaskMode);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetFilteredLibraryLocations(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var config = GetConfig();
        var virtualFolders = libraryManager.GetVirtualFolders();

        var includedSet = ParseCommaSeparated(config.IncludedLibraries);
        var excludedSet = ParseCommaSeparated(config.ExcludedLibraries);

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

    /// <inheritdoc />
    public bool IsOldEnoughForDeletion(string directoryPath)
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
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsFileOldEnoughForDeletion(string filePath)
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
            return false;
        }
    }

    /// <inheritdoc />
    public string GetTrashPath(string libraryRootPath)
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

        return Path.Join(libraryRootPath, trashPath);
    }

    // ===== Pure static helpers (no state, no config access) =====

    /// <summary>
    ///     Determines whether a task should run in dry-run mode based on its <see cref="TaskMode" />.
    ///     Returns true for <see cref="TaskMode.DryRun" />, false for <see cref="TaskMode.Activate" />.
    ///     Should NOT be called if the task mode is <see cref="TaskMode.Deactivate" /> (check first).
    /// </summary>
    /// <param name="mode">The task mode.</param>
    /// <returns>True if the task should run in dry-run mode.</returns>
    public static bool IsDryRun(TaskMode mode)
    {
        return mode != TaskMode.Activate;
    }

    /// <summary>
    ///     Parses a comma-separated string into a case-insensitive hash set of trimmed, non-empty values.
    /// </summary>
    /// <param name="value">The comma-separated input string.</param>
    /// <returns>A hash set of parsed values.</returns>
    public static HashSet<string> ParseCommaSeparated(string? value)
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