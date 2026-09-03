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

        var normalizedItem = NormalizeForPrefix(itemPath);

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            if (IsUnderRoot(normalizedItem, NormalizeForPrefix(root)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the allowed and excluded library roots as a single scope, so nested exclusions can be
    /// denied even when they sit under an allowed root.
    /// </summary>
    /// <remarks>
    /// A name-based exclusion alone is not enough: when an excluded library (for example
    /// <c>/media/anime</c>) is nested under an allowed one (<c>/media</c>), dropping the excluded
    /// folder still leaves its items under the retained allowed root. Carrying the excluded
    /// locations lets <see cref="IsAllowed(string?, LibraryRootScope)"/> deny them by choosing the
    /// most specific matching root.
    /// </remarks>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="excludedLibraryNames">Library names to exclude (case-insensitive). May be empty.</param>
    /// <returns>The allowed and excluded root locations partitioned by the name exclusion set.</returns>
    public static LibraryRootScope GetLibraryRootScope(
        ILibraryManager libraryManager,
        IReadOnlySet<string> excludedLibraryNames)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(excludedLibraryNames);

        var allowed = new List<string>();
        var excluded = new List<string>();

        foreach (var folder in libraryManager.GetVirtualFolders())
        {
            var target = excludedLibraryNames.Count > 0 && excludedLibraryNames.Contains(folder.Name ?? string.Empty)
                ? excluded
                : allowed;

            foreach (var location in folder.Locations ?? [])
            {
                target.Add(location);
            }
        }

        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return new LibraryRootScope(
            allowed.Distinct(comparer).ToList(),
            excluded.Distinct(comparer).ToList());
    }

    /// <summary>
    /// Determines whether an item path is permitted by the supplied scope, resolving the case where
    /// an excluded root is nested under an allowed one (or vice versa).
    /// </summary>
    /// <remarks>
    /// The item is permitted only when the most specific root that contains it is an allowed root.
    /// A deeper excluded root therefore overrides a shallower allowed root, so an item under
    /// <c>/media/anime</c> is denied even though it also sits under an allowed <c>/media</c>.
    /// </remarks>
    /// <param name="itemPath">The item's path on disk. A null or empty path is treated as not allowed.</param>
    /// <param name="scope">The allowed and excluded root locations.</param>
    /// <returns><see langword="true"/> if the item is permitted; otherwise <see langword="false"/>.</returns>
    public static bool IsAllowed(string? itemPath, LibraryRootScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        var normalizedItem = NormalizeForPrefix(itemPath);

        var deepestAllowed = DeepestMatchingRootLength(normalizedItem, scope.AllowedRoots);
        if (deepestAllowed < 0)
        {
            return false;
        }

        var deepestExcluded = DeepestMatchingRootLength(normalizedItem, scope.ExcludedRoots);

        // An excluded root wins only when it is strictly more specific than the allowed match, so an
        // allowed library nested under an excluded one still admits its own items.
        return deepestExcluded <= deepestAllowed;
    }

    // Returns the normalized length of the longest root that contains the item, or -1 if none do.
    // Length is the specificity measure: a deeper (longer) root path is the more specific match.
    private static int DeepestMatchingRootLength(string normalizedItem, IReadOnlyCollection<string> roots)
    {
        var deepest = -1;
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var normalizedRoot = NormalizeForPrefix(root);
            if (IsUnderRoot(normalizedItem, normalizedRoot) && normalizedRoot.Length > deepest)
            {
                deepest = normalizedRoot.Length;
            }
        }

        return deepest;
    }

    // Directory-boundary containment test for two already-normalized paths, shared by the flat
    // allow-list check and the scoped most-specific-root resolution so both use one implementation.
    private static bool IsUnderRoot(string normalizedItem, string normalizedRoot)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // The filesystem root normalizes to "/"; its child prefix is "/" itself, not "//",
        // otherwise every descendant would fail the boundary check.
        var childPrefix = normalizedRoot == "/" ? "/" : normalizedRoot + '/';

        // Exact match (the item is the root itself) or a child under a directory boundary.
        return normalizedItem.Equals(normalizedRoot, comparison)
            || normalizedItem.StartsWith(childPrefix, comparison);
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
