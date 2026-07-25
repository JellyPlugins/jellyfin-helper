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
}
