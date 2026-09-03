using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Single source of truth for how file-system paths are compared across platforms, so the OS branch
/// is not re-derived (and cannot drift) at each call site.
/// </summary>
/// <remarks>
/// macOS is treated as case-insensitive because the overwhelming majority of installations run on
/// case-insensitive APFS/HFS+; only Linux (and other case-sensitive platforms) compare ordinally.
/// </remarks>
public static class PathComparison
{
    /// <summary>
    /// Gets the <see cref="StringComparison"/> to use for file-system path comparisons on the current OS.
    /// </summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Gets the <see cref="StringComparer"/> matching <see cref="Comparison"/>, for set/dedup use.
    /// </summary>
    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
