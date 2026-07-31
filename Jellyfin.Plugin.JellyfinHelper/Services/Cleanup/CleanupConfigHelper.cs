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
    public TaskMode GetLinkRepairTaskMode()
    {
        return GetConfig().LinkRepairTaskMode;
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
    public bool IsDryRunLinkRepair()
    {
        return IsDryRun(GetConfig().LinkRepairTaskMode);
    }

    // NOTE: each IsDryRun* method above fetches config independently via GetConfig().
    // If a caller needs to check multiple modes in one task execution, call GetConfig()
    // once at the call site and read the TaskMode properties directly to avoid
    // redundant configuration fetches.

    /// <inheritdoc />
    public IReadOnlyList<string> GetFilteredLibraryLocations(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var config = GetConfig();
        var virtualFolders = libraryManager.GetVirtualFolders();

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

            // If exclude list is set, exclude listed libraries
            if (excludedSet.Count > 0 && excludedSet.Contains(name))
            {
                return false;
            }

            return true;
        });

        // Filter out any locations that point to Jellyfin's internal
        // collections directory (typically /config/data/collections or similar).
        return filteredFolders
            .SelectMany(f => f.Locations ?? [])
            .Where(loc => !IsCollectionsPath(loc))
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

            var created = dirInfo.CreationTimeUtc < dirInfo.LastWriteTimeUtc
                ? dirInfo.CreationTimeUtc
                : dirInfo.LastWriteTimeUtc;
            if (created.Year < 1980)
            {
                return false;
            }

            var age = DateTime.UtcNow - created;
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

            var created = fileInfo.CreationTimeUtc < fileInfo.LastWriteTimeUtc
                ? fileInfo.CreationTimeUtc
                : fileInfo.LastWriteTimeUtc;
            if (created.Year < 1980)
            {
                return false;
            }

            var age = DateTime.UtcNow - created;
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

        if (Path.IsPathFullyQualified(trashPath))
        {
            // Absolute trash path must not be the library root itself.
            // If it is, TrashService would treat every source file as "already in trash" and skip all moves.
            try
            {
                var absTrashNormalized = Path.GetFullPath(trashPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var absRootNormalized = Path.GetFullPath(libraryRootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var absPathComparison = GetOsPathComparison();

                if (string.Equals(absTrashNormalized, absRootNormalized, absPathComparison))
                {
                    return Path.GetFullPath(Path.Join(libraryRootPath, ".jellyfin-trash"));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Path.GetFullPath(Path.Join(libraryRootPath, ".jellyfin-trash"));
            }

            return trashPath;
        }

        // Resolve relative path against the library root and verify it does not escape
        // via ".." sequences. Path.Join does not resolve ".." - only GetFullPath does.
        // Guard against malformed or excessively long paths that would otherwise throw.
        string resolved;
        string normalizedRoot;
        try
        {
            resolved = Path.GetFullPath(Path.Join(libraryRootPath, trashPath));
            normalizedRoot = Path.GetFullPath(libraryRootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.GetFullPath(Path.Join(libraryRootPath, ".jellyfin-trash"));
        }

        var pathComparison = GetOsPathComparison();

        // Compare without trailing separators so that "/" == "/" and "C:\" == "C:\" work correctly
        var resolvedTrimmed = resolved
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootTrimmed = normalizedRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(resolvedTrimmed, rootTrimmed, pathComparison))
        {
            // TrashFolderPath resolves to the library root itself (e.g. ".") - not safe.
            return Path.GetFullPath(Path.Join(libraryRootPath, ".jellyfin-trash"));
        }

        var rootPrefix = rootTrimmed + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, pathComparison))
        {
            // Relative path escapes the library root - fall back to the safe default.
            // Note: admins who intend a path outside the library root should use an absolute path.
            return Path.GetFullPath(Path.Join(libraryRootPath, ".jellyfin-trash"));
        }

        return resolved;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetExistingTrashFoldersForPath(ILibraryManager libraryManager, string trashFolderPath)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        var existingPaths = new List<string>();
        var queryPath = (trashFolderPath ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(queryPath))
        {
            return existingPaths;
        }

        // Fetch virtual folders once; derive both collections from the same snapshot.
        var virtualFolders = libraryManager.GetVirtualFolders();

        // Use ALL library roots (unfiltered) for the safety guard so that music, boxset,
        // and user-excluded libraries are still protected from accidental deletion/relocation.
        var allLibraryRoots = virtualFolders
            .SelectMany(f => f.Locations ?? [])
            .Where(loc => !string.IsNullOrWhiteSpace(loc))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pre-normalize all roots once so the inner Any check is O(n) rather than O(n*m).
        var comparison = GetOsPathComparison();
        var normalizedAllRoots = new List<string>(allLibraryRoots.Count);
        foreach (var root in allLibraryRoots)
        {
            try
            {
                normalizedAllRoots.Add(
                    Path.GetFullPath(root)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip roots whose paths cannot be resolved
            }
        }

        if (Path.IsPathFullyQualified(queryPath))
        {
            // Absolute path: single trash folder
            try
            {
                var fullPath = Path.GetFullPath(queryPath);

                // Never report a library root as an existing trash folder.
                // If it were reported, the relocate/delete flow could target the entire library.
                var fullPathTrimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var isLibraryRoot = normalizedAllRoots.Any(root =>
                    string.Equals(root, fullPathTrimmed, comparison));

                if (isLibraryRoot)
                {
                    return existingPaths;
                }

                if (Directory.Exists(fullPath))
                {
                    existingPaths.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Skip invalid paths silently
            }
        }
        else
        {
            // Relative path: resolve per filtered library (only managed libraries have trash).
            // Derive the filtered set from the already-fetched virtualFolders snapshot.
            var config = GetConfig();
            var excludedSet = ParseCommaSeparated(config.ExcludedLibraries);
            var libraryFolders = virtualFolders
                .Where(f =>
                {
                    var name = f.Name ?? string.Empty;

                    // Always exclude non-video library types
                    if (f.CollectionType is CollectionTypeOptions.music or CollectionTypeOptions.boxsets)
                    {
                        return false;
                    }

                    // Fallback: also exclude by name pattern in case CollectionType is null/unknown
                    if (name.Contains("collection", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("boxset", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (excludedSet.Count > 0 && excludedSet.Contains(name))
                    {
                        return false;
                    }

                    return true;
                })
                .SelectMany(f => f.Locations ?? [])
                .Where(loc => !IsCollectionsPath(loc))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var folder in libraryFolders)
            {
                try
                {
                    var resolved = Path.GetFullPath(Path.Join(folder, queryPath));
                    var normalizedRoot = Path.GetFullPath(folder)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

                    // Must stay within the library root
                    if (!resolved.StartsWith(rootPrefix, comparison))
                    {
                        continue;
                    }

                    // Never report ANY library root as an existing trash folder.
                    // Check against all roots (not just filtered) to protect music/boxset libraries too.
                    var resolvedTrimmed = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var isAnyLibraryRoot = normalizedAllRoots.Any(root =>
                        string.Equals(root, resolvedTrimmed, comparison));

                    if (isAnyLibraryRoot)
                    {
                        continue;
                    }

                    if (Directory.Exists(resolved))
                    {
                        existingPaths.Add(resolved);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Skip invalid paths silently
                }
            }
        }

        return existingPaths;
    }

    // ===== Private helpers =====

    // ===== Pure static helpers (no state, no config access) =====

    /// <summary>
    ///     Returns the OS-appropriate <see cref="StringComparison" /> for file-system path comparisons.
    ///     Note: macOS is treated as case-insensitive because the overwhelming majority of installations
    ///     use case-insensitive APFS/HFS+. While case-sensitive APFS volumes exist, using OrdinalIgnoreCase
    ///     is the safer default for a cleanup/trash tool - it may produce false positives (treating two
    ///     case-differing paths as identical) but never false negatives (missing a match that could lead
    ///     to operating on a library root). If case-sensitive macOS volumes become a reported issue,
    ///     a volume-aware probe can be added here.
    /// </summary>
    private static StringComparison GetOsPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>
    ///     Determines whether a task should run in dry-run mode based on its <see cref="TaskMode" />.
    ///     Returns true only for <see cref="TaskMode.DryRun" />; false for all other modes.
    ///     Callers must handle <see cref="TaskMode.Deactivate" /> separately before calling this.
    /// </summary>
    /// <param name="mode">The task mode.</param>
    /// <returns>True if the task should run in dry-run mode.</returns>
    public static bool IsDryRun(TaskMode mode)
    {
        return mode == TaskMode.DryRun;
    }

    /// <summary>
    ///     Returns true when any path segment of <paramref name="location" /> is exactly "collections"
    ///     (case-insensitive). Handles both forward-slash and backslash separators.
    ///     Used to exclude Jellyfin's internal collections directory from all library-location filters.
    /// </summary>
    /// <param name="location">The filesystem path to test.</param>
    /// <returns>
    ///     <see langword="true" /> when a segment of the path equals "collections" (case-insensitive);
    ///     otherwise <see langword="false" />.
    /// </returns>
    internal static bool IsCollectionsPath(string location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return false;
        }

        // Split on all three possible separators so the check works correctly on both
        // Linux (separator='/') and Windows (separator='\'), including Windows-style paths
        // stored in config on a Linux host.
        var segments = location.Split('/', '\\');
        return segments.Any(s => string.Equals(s, "collections", StringComparison.OrdinalIgnoreCase));
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