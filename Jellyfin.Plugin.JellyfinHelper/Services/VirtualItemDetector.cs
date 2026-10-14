using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Decides whether a library item is virtual and therefore must not count as owned content.
/// </summary>
/// <remarks>
/// External request helpers (for example JellyNext) create stub libraries whose entries carry a real
/// TMDb id but no real media file, written under the host plugins directory (for example
/// <c>&lt;data&gt;/plugins/Jellyfin.Plugin.JellyNext/jellynext-virtual/...</c>). Those stubs would otherwise be
/// treated as owned and suppress every discovery suggestion for the same title. An item is virtual when Jellyfin
/// flags it as such, when it has no physical location, or when its path resolves inside the plugins directory.
/// The containment check is scoped to the plugins directory, not the whole data directory, so a real media
/// library an administrator happens to store under the data directory is never misclassified. Detection is
/// intentionally free of name, extension, or size heuristics.
/// </remarks>
public sealed class VirtualItemDetector
{
    private readonly ILogger<VirtualItemDetector> _logger;

    // The plugins directory normalized once with a trailing separator, so the per-item containment check does no
    // repeated GetFullPath work across a large library scan. Null when the host reports no plugins path or the
    // path cannot be normalized, in which case containment never matches and every real file stays owned.
    private readonly string? _normalizedPluginsRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualItemDetector"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the host plugins directory used as the stub-location root.</param>
    /// <param name="logger">The logger.</param>
    public VirtualItemDetector(IApplicationPaths applicationPaths, ILogger<VirtualItemDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _normalizedPluginsRoot = NormalizeRoot(applicationPaths.PluginsPath);
    }

    /// <summary>
    /// Determines whether the supplied item is virtual and must be excluded from ownership checks.
    /// </summary>
    /// <param name="item">The library item to inspect.</param>
    /// <returns><see langword="true"/> when the item is virtual; otherwise <see langword="false"/>.</returns>
    public bool IsVirtual(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Treat the item as virtual when Jellyfin flags it, or when it has no physical path. An empty path is how
        // Jellyfin represents a purely virtual entry, and reading it directly avoids BaseItem.LocationType, which
        // derives the same thing but reads SourceType and can throw on a partially materialized item.
        if (item.IsVirtualItem || string.IsNullOrEmpty(item.Path))
        {
            LogSkip(item, "flagged virtual or no physical path");
            return true;
        }

        if (IsUnderPluginsPath(item.Path))
        {
            LogSkip(item, "path under host plugins directory");
            return true;
        }

        return false;
    }

    // A real file counts as owned unless it lives inside the host plugins directory, where only plugin-created
    // stubs live. The comparison is ordinal and anchored on a trailing separator so a sibling like
    // "<plugins>-cache" never matches "<plugins>". The root is normalized once in the ctor; only the per-item
    // path is normalized here.
    private bool IsUnderPluginsPath(string? itemPath)
    {
        if (string.IsNullOrEmpty(itemPath) || _normalizedPluginsRoot is null)
        {
            return false;
        }

        // Normalize the item path and fail closed: a path with invalid characters cannot be a plugins-directory
        // stub, so treat it as owned rather than letting the exception unwind into AddLibraryExclusions, which
        // contains the whole scan and would otherwise discard every owned title's exclusion over one bad path.
        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemPath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidate.Equals(Path.TrimEndingDirectorySeparator(_normalizedPluginsRoot), comparison)
            || candidate.StartsWith(_normalizedPluginsRoot, comparison);
    }

    // Normalizes the plugins root once with a trailing separator. Returns null when the host reports no path or
    // the path cannot be normalized, so containment simply never matches rather than throwing on every scan.
    private static string? NormalizeRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            // A filesystem root (for example "/" or "C:\") keeps its trailing separator through the trim above, so
            // only append one when it is not already present, or a root would gain a double separator and never
            // match its children.
            return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private void LogSkip(BaseItem item, string reason)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "VirtualItemDetector skipping item {ItemId} ({Reason}).",
            item.Id,
            reason);
    }
}
