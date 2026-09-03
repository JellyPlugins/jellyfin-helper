using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Resolves and deduplicates library folder paths from the Jellyfin library manager.
/// </summary>
public static class LibraryPathResolver
{
    /// <summary>
    /// Gets all distinct library location paths across all virtual folders.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>A deduplicated list of library root paths.</returns>
    public static IReadOnlyList<string> GetDistinctLibraryLocations(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        return libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                try
                {
                    return Path.GetFullPath(p);
                }
                catch
                {
                    return p;
                }
            })
            .ToList();
    }

    /// <summary>
    /// Gets the distinct library location paths, excluding any virtual folder whose name is in the
    /// supplied exclusion set.
    /// </summary>
    /// <remarks>
    /// This is the allow-list of roots the recommendation pipeline is permitted to read from. Unlike
    /// the cleanup path filter it does not impose a collection-type allow-list, because the
    /// recommendation queries already restrict themselves to movies, series, and episodes; the only
    /// thing being honored here is the user's explicit library exclusion.
    /// </remarks>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="excludedLibraryNames">Library names to exclude (case-insensitive). May be empty.</param>
    /// <returns>The allowed root paths after applying the exclusion set.</returns>
    public static IReadOnlyList<string> GetAllowedLibraryRoots(
        ILibraryManager libraryManager,
        IReadOnlySet<string> excludedLibraryNames)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(excludedLibraryNames);

        var folders = libraryManager.GetVirtualFolders();
        var roots = new List<string>();

        foreach (var folder in folders)
        {
            if (excludedLibraryNames.Count > 0 && excludedLibraryNames.Contains(folder.Name ?? string.Empty))
            {
                continue;
            }

            foreach (var location in folder.Locations ?? [])
            {
                roots.Add(location);
            }
        }

        return roots
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Determines whether an item path sits under one of the supplied allowed root locations.
    /// </summary>
    /// <remarks>
    /// The comparison normalizes separators and matches on a directory boundary so that a root
    /// such as <c>/media/movies</c> does not spuriously match a sibling like <c>/media/movies2</c>.
    /// Path casing follows the platform convention Jellyfin uses elsewhere: ordinal on Linux,
    /// case-insensitive otherwise.
    /// </remarks>
    /// <param name="itemPath">The item's path on disk. A null or empty path is treated as not allowed.</param>
    /// <param name="allowedRoots">The allowed root locations. A null or empty set allows nothing.</param>
    /// <returns><see langword="true"/> if the item path is under an allowed root; otherwise <see langword="false"/>.</returns>
    public static bool IsUnderAllowedRoot(string? itemPath, IReadOnlyCollection<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        if (string.IsNullOrEmpty(itemPath) || allowedRoots.Count == 0)
        {
            return false;
        }

        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var normalizedItem = NormalizeForPrefix(itemPath);

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var normalizedRoot = NormalizeForPrefix(root);

            // The filesystem root normalizes to "/"; its child prefix is "/" itself, not "//",
            // otherwise every descendant would fail the boundary check.
            var childPrefix = normalizedRoot == "/" ? "/" : normalizedRoot + '/';

            // Exact match (the item is the root itself) or a child under a directory boundary.
            if (normalizedItem.Equals(normalizedRoot, comparison)
                || normalizedItem.StartsWith(childPrefix, comparison))
            {
                return true;
            }
        }

        return false;
    }

    // Collapse backslashes to forward slashes and strip a trailing separator so prefix checks
    // compare directory segments rather than raw strings across Windows and Linux paths.
    private static string NormalizeForPrefix(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Length > 1 && normalized.EndsWith('/')
            ? normalized[..^1]
            : normalized;
    }
}
