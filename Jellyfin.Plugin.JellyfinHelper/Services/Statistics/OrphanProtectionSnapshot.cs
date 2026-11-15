using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
///     Scan-local, positive-only Arr protection. Managed folders protect their descendants and
///     ancestors, but never siblings. Unavailable instances leave their known scope unproven.
/// </summary>
public sealed class OrphanProtectionSnapshot
{
    private readonly HashSet<string> _managedDirectories = new(PathComparison.Comparer);
    private readonly HashSet<string> _ancestors = new(PathComparison.Comparer);
    private readonly string[] _uncertainRoots;

    /// <summary>Initializes a new instance of the <see cref="OrphanProtectionSnapshot"/> class.</summary>
    /// <param name="managedDirectories">Unambiguously mapped local Arr directories.</param>
    /// <param name="uncertainRoots">Local library roots whose Arr ownership could not be checked.</param>
    public OrphanProtectionSnapshot(IEnumerable<string> managedDirectories, IEnumerable<string> uncertainRoots)
    {
        ArgumentNullException.ThrowIfNull(managedDirectories);
        ArgumentNullException.ThrowIfNull(uncertainRoots);
        foreach (var path in managedDirectories)
        {
            if (!TryNormalizePath(path, out var normalized))
            {
                continue;
            }

            _managedDirectories.Add(normalized);
            for (var parent = normalized; parent != null; parent = Path.GetDirectoryName(parent))
            {
                _ancestors.Add(parent);
            }
        }

        _uncertainRoots = uncertainRoots
            .Select(path => TryNormalizePath(path, out var normalized) ? normalized : null)
            .OfType<string>()
            .Distinct(PathComparison.Comparer)
            .ToArray();
    }

    /// <summary>Gets an empty snapshot for scans without configured Arr protection.</summary>
    public static OrphanProtectionSnapshot Empty { get; } = new([], []);

    /// <summary>Determines whether a directory is managed by Arr or contains a managed descendant.</summary>
    /// <param name="directoryPath">The local directory path.</param>
    /// <returns>True when Arr ownership positively protects this directory.</returns>
    public bool ProtectsDirectory(string directoryPath)
    {
        if (!TryNormalizePath(directoryPath, out var normalized))
        {
            return false;
        }

        if (_ancestors.Contains(normalized))
        {
            return true;
        }

        for (var parent = Path.GetDirectoryName(normalized); parent != null; parent = Path.GetDirectoryName(parent))
        {
            if (_managedDirectories.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Determines whether a scoped Arr failure prevents a confirmed orphan verdict.</summary>
    /// <param name="directoryPath">The local directory path.</param>
    /// <returns>True when ownership is unproven in this library root.</returns>
    public bool IsUncertain(string directoryPath)
    {
        return TryNormalizePath(directoryPath, out var normalized)
            && _uncertainRoots.Any(root => IsWithin(normalized, root));
    }

    internal static bool IsWithin(string path, string root)
    {
        return string.Equals(path, root, PathComparison.Comparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison.Comparison);
    }

    internal static bool IsExcludedMediaPath(string path, string root, string? trashPath, string trashName)
    {
        if ((trashPath != null && IsWithin(path, trashPath)) || CleanupConfigHelper.IsCollectionsPath(path))
        {
            return true;
        }

        return Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None)
            .Any(segment => segment.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)
                || segment.Contains("[boxset]", StringComparison.OrdinalIgnoreCase)
                || segment.Contains("[collection]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, trashName, PathComparison.Comparison));
    }

    internal static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
