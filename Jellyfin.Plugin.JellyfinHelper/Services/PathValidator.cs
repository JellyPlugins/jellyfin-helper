using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Provides path validation utilities to prevent path traversal attacks.
/// </summary>
internal static class PathValidator
{
    /// <summary>
    /// Cached set of invalid filename characters (excluding directory separators).
    /// Thread-safe: static readonly field initializers are guaranteed to run once.
    /// </summary>
    private static readonly HashSet<char> InvalidFileNameChars =
        Path.GetInvalidFileNameChars()
            .Where(c => c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            .ToHashSet();

    /// <summary>
    /// Canonical set of sensitive system directories that plugin features must never
    /// read from, write to, delete, browse into, or repair links toward. This is the
    /// single source of truth — previously each of LinkRepairService, TrashController
    /// and FolderBrowserService kept its own (drifting) copy. Includes Jellyfin's own
    /// data/config/cache mounts and OS system roots on both Linux and Windows.
    /// </summary>
    private static readonly string[] SensitiveSystemRoots =
    {
        "/config", "/cache", "/data", "/etc", "/usr", "/bin", "/sbin", "/lib", "/lib64",
        "/boot", "/proc", "/sys", "/dev", "/var", "/root", "/run",
        @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
    };

    /// <summary>
    /// Validates that a given path does not contain path traversal sequences
    /// and resolves to a location within the allowed base directory.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <param name="allowedBaseDirectory">The allowed base directory.</param>
    /// <param name="pluginLog">Optional plugin log service for diagnostics.</param>
    /// <returns><c>true</c> if the path is safe; <c>false</c> otherwise.</returns>
    internal static bool IsSafePath(string? path, string allowedBaseDirectory, IPluginLogService? pluginLog = null)
    {
        if (string.IsNullOrEmpty(allowedBaseDirectory))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            pluginLog?.LogDebug("PathValidator", "Path validation failed: path is empty or null.");
            return false;
        }

        // Reject null bytes and segment-level ".." traversal.
        // A substring Contains("..") would produce false positives for names like "my..folder",
        // so split on both separators and check each segment individually.
        // The real protection is Path.GetFullPath + StartsWith below; this is an early-exit guard.
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            pluginLog?.LogWarning("PathValidator", $"Path validation failed: null byte detected in '{path}'.");
            return false;
        }

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Array.Exists(segments, s => s == ".." || s == "."))
        {
            pluginLog?.LogWarning("PathValidator", $"Path validation failed: traversal segment detected in '{path}'.");
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var basePath = Path.GetFullPath(allowedBaseDirectory);

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // Accept the base directory itself, or anything strictly inside it.
            var baseWithSep = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            return fullPath.Equals(basePath, comparison)
                   || fullPath.StartsWith(baseWithSep, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or ArgumentNullException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that <paramref name="fullPath"/> is safe for recursive deletion.
    /// Rejects filesystem roots, paths that equal or are an ancestor of any library root,
    /// and paths that are a child of any library root (would delete content inside a library).
    /// </summary>
    /// <param name="fullPath">Fully-resolved absolute path to validate.</param>
    /// <param name="libraryFolders">Library root folders that must not be deleted.</param>
    /// <returns><c>true</c> if deletion is safe; <c>false</c> otherwise.</returns>
    internal static bool IsPathSafeForDeletion(string fullPath, IReadOnlyList<string> libraryFolders)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathRooted(fullPath))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = Path.GetPathRoot(fullPath);
        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return false;
        }

        foreach (var folder in libraryFolders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var libraryRoot = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Reject if candidate IS the library root
            if (string.Equals(candidate, libraryRoot, comparison))
            {
                return false;
            }

            // Reject if candidate is an ANCESTOR of the library root (deleting it removes the root's parent)
            if (libraryRoot.StartsWith(candidate + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }

            // Reject if candidate is a CHILD of the library root (deleting it removes content inside the library)
            // Note: callers that legitimately manage a trash sub-folder inside a library must confirm
            // the path is the configured trash folder before calling this method.
            if (candidate.StartsWith(libraryRoot + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a fully-normalized absolute path equals, or is inside, a
    /// sensitive system directory (see <see cref="SensitiveSystemRoots"/>). Callers pass
    /// an already-normalized path (e.g. from <see cref="Path.GetFullPath(string)"/>).
    /// A cross-location media target (another library / mount) is NOT sensitive — only
    /// the well-known system/config locations are refused.
    /// </summary>
    /// <param name="normalizedPath">A normalized absolute path.</param>
    /// <returns><c>true</c> if the path is a sensitive system location; otherwise <c>false</c>.</returns>
    internal static bool IsSensitiveSystemPath(string? normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var path = normalizedPath.TrimEnd('/', '\\');

        foreach (var sensitive in SensitiveSystemRoots)
        {
            var s = sensitive.TrimEnd('/', '\\');
            if (string.Equals(path, s, comparison)
                || path.StartsWith(s + "/", comparison)
                || path.StartsWith(s + "\\", comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sanitizes a filename by removing any directory components and invalid characters.
    /// </summary>
    /// <param name="fileName">The raw filename input.</param>
    /// <returns>A sanitized filename safe for use in file operations.</returns>
    internal static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "export";
        }

        // Single pass: replace invalid filename characters with '_' and treat '\' as a
        // directory separator (on Linux '\' is legal but must not appear in a filename).
        // Directory separators are preserved here so Path.GetFileName can strip them next.
        var name = new string(fileName.Select(ch =>
            ch == '\\' ? '/' :
            InvalidFileNameChars.Contains(ch) ? '_' : ch).ToArray());

        // Strip any directory components left after the pass above.
        name = Path.GetFileName(name);

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            return "export";
        }

        return name;
    }
}
