using System;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Provides path validation utilities to prevent path traversal attacks.
/// </summary>
internal static class PathValidator
{
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
        if (string.IsNullOrWhiteSpace(path))
        {
            pluginLog?.LogDebug("PathValidator", "Path validation failed: path is empty or null.");
            return false;
        }

        // Reject obvious traversal patterns
        if (path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\0', StringComparison.Ordinal))
        {
            pluginLog?.LogWarning("PathValidator", $"Path validation failed: traversal pattern detected in '{path}'.");
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var basePath = Path.GetFullPath(allowedBaseDirectory);

            // Ensure trailing separator for correct prefix matching
            if (!basePath.EndsWith(Path.DirectorySeparatorChar))
            {
                basePath += Path.DirectorySeparatorChar;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fullPath.StartsWith(basePath, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

        // Replace invalid filename characters first (except directory separators,
        // which are needed by Path.GetFileName to strip directory components).
        // This avoids passing characters like '\0' into Path.GetFileName, which
        // can behave unexpectedly on some platforms.
        var invalidChars = Path.GetInvalidFileNameChars();
        var name = fileName;
        foreach (var c in invalidChars)
        {
            if (c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            {
                name = name.Replace(c, '_');
            }
        }

        // Strip any directory separators
        name = Path.GetFileName(name);

        return string.IsNullOrWhiteSpace(name) ? "export" : name;
    }
}
