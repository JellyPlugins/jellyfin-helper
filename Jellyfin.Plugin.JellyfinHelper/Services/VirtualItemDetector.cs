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
    private readonly string _pluginsPath;
    private readonly ILogger<VirtualItemDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualItemDetector"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the host plugins directory used as the stub-location root.</param>
    /// <param name="logger">The logger.</param>
    public VirtualItemDetector(IApplicationPaths applicationPaths, ILogger<VirtualItemDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _pluginsPath = applicationPaths.PluginsPath;
        _logger = logger;
    }

    /// <summary>
    /// Determines whether the supplied item is virtual and must be excluded from ownership checks.
    /// </summary>
    /// <param name="item">The library item to inspect.</param>
    /// <returns><see langword="true"/> when the item is virtual; otherwise <see langword="false"/>.</returns>
    public bool IsVirtual(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // An empty path is how Jellyfin represents a purely virtual entry (BaseItem.LocationType derives
        // Virtual from exactly this). Checking the path directly avoids the derived property, which reads
        // SourceType and throws on a partially materialized item.
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
    // "<plugins>-cache" never matches "<plugins>".
    private bool IsUnderPluginsPath(string? itemPath)
    {
        if (string.IsNullOrEmpty(itemPath) || string.IsNullOrEmpty(_pluginsPath))
        {
            return false;
        }

        // Normalize per item and fail closed: a path with invalid characters cannot be a plugins-directory stub,
        // so treat it as owned rather than letting the exception unwind into AddLibraryExclusions, which contains
        // the whole scan and would otherwise discard every owned title's exclusion over one bad path.
        string candidate;
        string root;
        try
        {
            root = NormalizeRoot(_pluginsPath);
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemPath));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidate.Equals(Path.TrimEndingDirectorySeparator(root), comparison)
            || candidate.StartsWith(root, comparison);
    }

    private static string NormalizeRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full + Path.DirectorySeparatorChar;
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
