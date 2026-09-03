using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// The allowed and excluded library root locations resolved for a recommendation run, used to deny
/// items in an excluded library even when that library is nested under an allowed one.
/// </summary>
public sealed class LibraryRootScope
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRootScope"/> class.
    /// </summary>
    /// <param name="allowedRoots">The root locations the recommendation pipeline may read from.</param>
    /// <param name="excludedRoots">The root locations the user chose to exclude.</param>
    public LibraryRootScope(IReadOnlyList<string> allowedRoots, IReadOnlyList<string> excludedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        ArgumentNullException.ThrowIfNull(excludedRoots);
        AllowedRoots = allowedRoots;
        ExcludedRoots = excludedRoots;
    }

    /// <summary>
    /// Gets the root locations the recommendation pipeline is permitted to read from.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; }

    /// <summary>
    /// Gets the root locations the user excluded, honored ahead of a shallower allowed root.
    /// </summary>
    public IReadOnlyList<string> ExcludedRoots { get; }
}
