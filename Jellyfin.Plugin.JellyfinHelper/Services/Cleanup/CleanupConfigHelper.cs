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
///     Helper that applies plugin configuration rules to cleanup operations. Provides library filtering, orphan age checking, trash/delete resolution, and task mode queries.
/// </summary>
public class CleanupConfigHelper : ICleanupConfigHelper
{
    private const string DefaultTrashFolderName = ".jellyfin-trash";

    private readonly IPluginConfigurationService _configService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanupConfigHelper" /> class.
    /// </summary>
    /// <param name="configService">The plugin configuration service.</param>
    public CleanupConfigHelper(IPluginConfigurationService configService)
    {
        _configService = configService;
    }

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

            // Allow-list: only video/audio collection types are cleaned. Books and unknown types are
            // skipped to prevent eBook deletion.
            if (!IsCleanupEligibleCollectionType(f.CollectionType))
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
            trashPath = DefaultTrashFolderName;
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
                    return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
                }

                // Re-check against protected system directories at resolution time. Config-save/backup-restore
                // already reject sensitive absolute paths, but a value persisted by an older build or edited
                // directly in config.xml would otherwise reach TrashService unchecked.
                if (PathValidator.IsSensitiveSystemPath(absTrashNormalized))
                {
                    return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
            }

            return trashPath;
        }

        // Resolve relative path against the library root and verify it does not escape via ".." sequences. Path.Join does not resolve ".." - only GetFullPath does.
        string resolved;
        string normalizedRoot;
        try
        {
            resolved = Path.GetFullPath(Path.Join(libraryRootPath, trashPath));
            normalizedRoot = Path.GetFullPath(libraryRootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
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
            return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
        }

        var rootPrefix = rootTrimmed + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, pathComparison))
        {
            // Relative path escapes the library root - fall back to the safe default.
            // Note: admins who intend a path outside the library root should use an absolute path.
            return Path.GetFullPath(Path.Join(libraryRootPath, DefaultTrashFolderName));
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

        var comparison = GetOsPathComparison();
        var normalizedAllRoots = NormalizeLibraryRoots(allLibraryRoots);

        if (Path.IsPathFullyQualified(queryPath))
        {
            ResolveAbsoluteTrashFolder(queryPath, normalizedAllRoots, comparison, existingPaths);
        }
        else
        {
            ResolveRelativeTrashFolders(queryPath, virtualFolders, normalizedAllRoots, comparison, existingPaths);
        }

        return existingPaths;
    }

    /// <summary>
    ///     Normalizes each library root to a full path with trailing separators trimmed. Roots whose paths cannot be resolved are skipped.
    /// </summary>
    private static List<string> NormalizeLibraryRoots(List<string> allLibraryRoots)
    {
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

        return normalizedAllRoots;
    }

    /// <summary>
    ///     Resolves an absolute trash path, adding it to <paramref name="existingPaths"/> only when it
    ///     exists on disk and is not itself a library root.
    /// </summary>
    private static void ResolveAbsoluteTrashFolder(
        string queryPath,
        IReadOnlyList<string> normalizedAllRoots,
        StringComparison comparison,
        List<string> existingPaths)
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
                return;
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

    /// <summary>
    ///     Resolves a relative trash path against each managed (filtered) library root, adding the per-library trash folders that exist on disk, stay within their root, and are not roots.
    /// </summary>
    private void ResolveRelativeTrashFolders(
        string queryPath,
        IEnumerable<VirtualFolderInfo> virtualFolders,
        IReadOnlyList<string> normalizedAllRoots,
        StringComparison comparison,
        List<string> existingPaths)
    {
        // Relative path: resolve per filtered library (only managed libraries have trash).
        // Derive the filtered set from the already-fetched virtualFolders snapshot.
        var libraryFolders = GetManagedLibraryFolders(virtualFolders);

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

    /// <summary>
    ///     Derives the managed library folders (video libraries that are not collections/boxsets and not user-excluded) from the supplied virtual-folder snapshot.
    /// </summary>
    private List<string> GetManagedLibraryFolders(IEnumerable<VirtualFolderInfo> virtualFolders)
    {
        var config = GetConfig();
        var excludedSet = ParseCommaSeparated(config.ExcludedLibraries);
        return virtualFolders
            .Where(f =>
            {
                var name = f.Name ?? string.Empty;

                // Fail-safe allow-list (mirror of GetFilteredLibraryLocations): only manage trash
                // for libraries that can hold video/audio media; skip books and any unknown/null type.
                if (!IsCleanupEligibleCollectionType(f.CollectionType))
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
    }

    /// <summary>
    ///     Returns the OS-appropriate StringComparison for file-system path comparisons. Note: macOS is treated as case-insensitive because the overwhelming majority of installations use case-insensitive APFS/HFS+.
    /// </summary>
    private static StringComparison GetOsPathComparison()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>
    ///     Determines whether a task should run in dry-run mode based on its TaskMode. Returns true only for DryRun; false for all other modes.
    /// </summary>
    /// <param name="mode">The task mode.</param>
    /// <returns>True if the task should run in dry-run mode.</returns>
    public static bool IsDryRun(TaskMode mode)
    {
        return mode == TaskMode.DryRun;
    }

    /// <summary>
    ///     Determines whether a library's collection type is eligible for destructive cleanup (empty-folder deletion, trash management, etc.).
    /// </summary>
    /// <param name="collectionType">The library's Jellyfin collection type (may be null/unknown).</param>
    /// <returns><see langword="false" /> for books/music/boxsets; otherwise <see langword="true" />.</returns>
    internal static bool IsCleanupEligibleCollectionType(CollectionTypeOptions? collectionType)
    {
        return collectionType is not (CollectionTypeOptions.books
            or CollectionTypeOptions.music
            or CollectionTypeOptions.boxsets);
    }

    /// <summary>
    ///     Returns true when any path segment of location is exactly "collections" (case-insensitive). Handles both forward-slash and backslash separators.
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

        // Split on all three possible separators so the check works correctly on both Linux (separator='/') and Windows (separator='\'), including Windows-style paths stored in config on a Linux host.
        var segments = location.Split(['/', '\\'], StringSplitOptions.None);
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
