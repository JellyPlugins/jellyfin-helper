using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Provides path validation utilities to prevent path traversal attacks.
/// </summary>
internal static class PathValidator
{
    /// <summary>
    ///     Cached set of invalid filename characters (excluding directory separators). Thread-safe: static readonly field initializers are guaranteed to run once.
    /// </summary>
    private static readonly HashSet<char> InvalidFileNameChars =
        Path.GetInvalidFileNameChars()
            .Where(c => c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            .ToHashSet();

    /// <summary>
    ///     Canonical set of sensitive system directories that plugin features must never read from, write to, delete, browse into, or repair links toward.
    /// </summary>
    private static readonly string[] SensitiveSystemRoots = BuildSensitiveSystemRoots();

    private static string[] BuildSensitiveSystemRoots()
        => BuildSensitiveSystemRoots(OperatingSystem.IsWindows(), Environment.GetFolderPath);

    /// <summary>
    ///     Builds the protected root set with explicit platform inputs so alternative Windows installations
    ///     and missing special folders can be validated independently of the host operating system.
    /// </summary>
    /// <param name="isWindows">Whether Windows special folders should be resolved.</param>
    /// <param name="resolveFolder">Resolves a Windows special folder to its current path.</param>
    /// <returns>The protected roots, without duplicate Windows paths.</returns>
    internal static string[] BuildSensitiveSystemRoots(bool isWindows, Func<Environment.SpecialFolder, string> resolveFolder)
    {
        ArgumentNullException.ThrowIfNull(resolveFolder);
        var roots = new List<string>
        {
            "/config", "/cache", "/data", "/etc", "/usr", "/bin", "/sbin", "/lib", "/lib64",
            "/boot", "/proc", "/sys", "/dev", "/var", "/root", "/run",
            // Common Windows system roots as literals so they are refused even on a non-Windows host (defense-in-depth for cross-platform string checks) and regardless of drive when the OS *is* installed on C:.
            @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
        };

        // Additionally resolve the real Windows system dirs from the OS, so a server installed on a drive other than C: (e.g.
        if (isWindows)
        {
            foreach (var path in new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            }.Select(resolveFolder)
                .Where(path => !string.IsNullOrEmpty(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                roots.Add(path);
            }
        }

        return roots.ToArray();
    }

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

        // Reject null bytes and segment-level ".." traversal. A substring Contains("..") would produce false positives for names like "my..folder", so split on both separators and check each segment individually.
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            pluginLog?.LogWarning("PathValidator", $"Path validation failed: null byte detected in '{path}'.");
            return false;
        }

        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None);
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
    ///     Segment-aware path-traversal check: splits on both directory separators (dropping empty
    ///     segments) and reports whether any segment is "." or "..". Distinct from <see cref="IsSafePath" />,
    ///     which additionally validates containment within a base directory and treats empty segments as
    ///     significant; callers here only need the segment screen with <see cref="StringSplitOptions.RemoveEmptyEntries" />.
    /// </summary>
    /// <param name="path">The caller-supplied path to screen.</param>
    /// <returns><c>true</c> if any segment is "." or ".."; otherwise <c>false</c>.</returns>
    internal static bool HasTraversalSegment(string? path)
        => !string.IsNullOrEmpty(path)
           && path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or "..");

    /// <summary>
    /// Validates that is safe for recursive deletion. Rejects filesystem roots, paths that equal or are an ancestor of any library root, and paths that are a child of any library root (would delete content inside a library).
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

            // Reject if candidate is a CHILD of the library root (deleting it removes content inside the library) Note: callers that legitimately manage a trash sub-folder inside a library must confirm the path is the configured trash folder before calling this method.
            if (candidate.StartsWith(libraryRoot + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Determines whether a fully-normalized absolute path equals, or is inside, a sensitive system directory (see SensitiveSystemRoots).
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

        return SensitiveSystemRoots.Select(sensitive => sensitive.TrimEnd('/', '\\'))
            .Any(s => string.Equals(path, s, comparison)
                || path.StartsWith(s + "/", comparison)
                || path.StartsWith(s + "\\", comparison));
    }

    /// <summary>
    ///     Resolves directory links in every path component, refusing protected targets before probing their descendants.
    ///     Missing components remain lexical so callers can perform their usual existence checks.
    /// </summary>
    /// <param name="path">The absolute directory path.</param>
    /// <returns>The path with directory links resolved.</returns>
    internal static string ResolveDirectoryPath(string path)
        => ResolveDirectoryPath(path, 0);

    private static string ResolveDirectoryPath(string path, int linkDepth)
    {
        if (linkDepth >= 40)
        {
            throw new IOException("Too many directory links.");
        }

        var normalized = Path.GetFullPath(path);
        // Device namespaces bypass normal Windows path canonicalization and protected-root matching.
        if (OperatingSystem.IsWindows() &&
            (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             normalized.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            throw new SecurityException("Device directory paths are not allowed.");
        }

        if (IsSensitiveSystemPath(normalized))
        {
            throw new SecurityException("Protected system directory.");
        }

        var root = Path.GetPathRoot(normalized)!;
        var resolved = root;
        var segments = normalized[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            resolved = Path.Join(resolved, segment);
            if (IsSensitiveSystemPath(resolved))
            {
                throw new SecurityException("Protected system directory.");
            }

            var directory = new DirectoryInfo(resolved);
            if (directory.LinkTarget is not null)
            {
                // Resolve only this hop: following a whole chain here could probe a protected target.
                var target = directory.ResolveLinkTarget(returnFinalTarget: false)
                    ?? throw new IOException("Cannot resolve directory link.");
                resolved = ResolveDirectoryPath(target.FullName, linkDepth + 1);
            }
        }

        return resolved;
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

        // Single pass: replace invalid filename characters with '_' and treat '\' as a directory separator (on Linux '\' is legal but must not appear in a filename).
        var name = new string(fileName.Select(ch =>
        {
            if (ch == '\\')
            {
                return '/';
            }

            return InvalidFileNameChars.Contains(ch) ? '_' : ch;
        }).ToArray());

        // Strip any directory components left after the pass above.
        name = Path.GetFileName(name);

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            return "export";
        }

        return name;
    }
}
