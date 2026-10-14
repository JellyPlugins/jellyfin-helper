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
/// TMDb id but no real media file. Those stubs would otherwise be treated as owned and suppress every
/// discovery suggestion for the same title. An item is virtual when Jellyfin flags it as such, when it
/// has no physical location, or when its path resolves inside the plugin host data directory. Detection
/// is intentionally free of name, extension, or size heuristics so a legitimately named real file is
/// never misclassified.
/// </remarks>
public sealed class VirtualItemDetector
{
    private readonly string _dataPath;
    private readonly ILogger<VirtualItemDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualItemDetector"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the host data directory used as the virtual-location root.</param>
    /// <param name="logger">The logger.</param>
    public VirtualItemDetector(IApplicationPaths applicationPaths, ILogger<VirtualItemDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _dataPath = applicationPaths.DataPath;
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

        if (IsUnderDataPath(item.Path))
        {
            LogSkip(item, "path under host data directory");
            return true;
        }

        return false;
    }

    // A real file counts as owned only when it lives outside the host data directory. The comparison is
    // ordinal and anchored on a trailing separator so a sibling like "<data>-cache" never matches "<data>".
    private bool IsUnderDataPath(string? itemPath)
    {
        if (string.IsNullOrEmpty(itemPath) || string.IsNullOrEmpty(_dataPath))
        {
            return false;
        }

        var root = NormalizeRoot(_dataPath);
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemPath));
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
