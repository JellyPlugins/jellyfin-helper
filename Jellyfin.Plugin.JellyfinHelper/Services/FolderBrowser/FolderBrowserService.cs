using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;

/// <summary>
///     Implementation of <see cref="IFolderBrowserService" /> that provides server-side
///     directory browsing for the admin settings folder picker UI.
///     All paths are validated and sanitized to prevent directory traversal attacks.
/// </summary>
public class FolderBrowserService : IFolderBrowserService
{
    private readonly ILogger<FolderBrowserService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FolderBrowserService" /> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public FolderBrowserService(ILogger<FolderBrowserService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public FolderBrowseResult GetRoots()
    {
        try
        {
            var entries = new List<FolderEntry>();

            if (OperatingSystem.IsWindows())
            {
                // On Windows, list available drive letters
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Ram)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var drive in drives)
                {
                    try
                    {
                        var rootPath = drive.RootDirectory.FullName;
                        var hasChildren = SafeHasSubdirectories(rootPath);

                        string? volumeLabel = null;
                        try
                        {
                            volumeLabel = drive.VolumeLabel;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                       or SecurityException)
                        {
                            _logger.LogDebug(ex, "Could not read volume label for drive {Drive}", drive.Name);
                        }

                        var baseName = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                        var displayName = string.IsNullOrWhiteSpace(volumeLabel)
                            ? baseName
                            : $"{baseName} ({volumeLabel})";

                        entries.Add(new FolderEntry
                        {
                            Name = displayName,
                            Path = rootPath,
                            HasChildren = hasChildren
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or SecurityException)
                    {
                        _logger.LogDebug(ex, "Skipping inaccessible drive {Drive}", drive.Name);
                    }
                }
            }
            else
            {
                // On Linux/macOS, root is always "/"
                var hasChildren = SafeHasSubdirectories("/");
                entries.Add(new FolderEntry
                {
                    Name = "/",
                    Path = "/",
                    HasChildren = hasChildren
                });
            }

            return new FolderBrowseResult
            {
                CurrentPath = null,
                ParentPath = null,
                CanGoUp = false,
                Directories = entries
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogWarning(ex, "Failed to enumerate filesystem roots");
            return new FolderBrowseResult
            {
                Error = "Cannot access filesystem roots."
            };
        }
    }

    /// <inheritdoc />
    public FolderBrowseResult GetChildren(string path)
    {
        var validationError = ValidatePath(path);
        if (validationError != null)
        {
            return new FolderBrowseResult { Error = validationError };
        }

        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var dirInfo = new DirectoryInfo(normalizedPath);

            if (!dirInfo.Exists)
            {
                return new FolderBrowseResult { Error = "Directory does not exist." };
            }

            var entries = new List<FolderEntry>();

            // Enumerate immediate subdirectories, skipping those we can't access.
            // EnumerateDirectories() returns a lazy iterator — exceptions from MoveNext()
            // (e.g. UnauthorizedAccessException when accessing Attributes inside the
            // Where predicate) are thrown during foreach iteration, not at assignment time.
            // Therefore all filtering is done inside the per-entry try/catch to ensure a
            // single inaccessible directory cannot abort the entire listing.
            try
            {
                foreach (var subdir in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        if (IsSystemOrHiddenCritical(subdir))
                        {
                            continue;
                        }

                        entries.Add(new FolderEntry
                        {
                            Name = subdir.Name,
                            Path = subdir.FullName,
                            HasChildren = SafeHasSubdirectories(subdir.FullName)
                        });
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                                   or SecurityException)
                    {
                        // Skip individual directories we cannot access
                        _logger.LogDebug(ex, "Skipping inaccessible directory {Dir}", subdir.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Cannot enumerate children of {Path}", normalizedPath);
                return new FolderBrowseResult
                {
                    CurrentPath = normalizedPath,
                    ParentPath = GetParentPath(normalizedPath),
                    CanGoUp = GetParentPath(normalizedPath) != null,
                    Directories = [],
                    Error = "Cannot access this directory."
                };
            }

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var parentPath = GetParentPath(normalizedPath);

            return new FolderBrowseResult
            {
                CurrentPath = normalizedPath,
                ParentPath = parentPath,
                CanGoUp = parentPath != null,
                Directories = entries
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(ex, "Error browsing directory {Path}", path);
            return new FolderBrowseResult { Error = "Cannot access this directory." };
        }
    }

    /// <inheritdoc />
    public string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path must not be empty.";
        }

        // Reject path traversal patterns
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return "Path must not contain '..' sequences.";
        }

        // Reject paths with null bytes (injection protection)
        if (path.AsSpan().IndexOf('\0') >= 0)
        {
            return "Path contains invalid characters.";
        }

        // Must be an absolute path (IsPathFullyQualified rejects drive-relative paths like "C:temp")
        if (!Path.IsPathFullyQualified(path))
        {
            return "Path must be absolute.";
        }

        // Attempt to normalize and verify the path is valid
        try
        {
            var normalized = Path.GetFullPath(path);

            // Verify directory exists
            if (!Directory.Exists(normalized))
            {
                return "Directory does not exist.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or SecurityException)
        {
            return $"Invalid path: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    ///     Gets the parent directory path, or null if at a filesystem root.
    /// </summary>
    /// <param name="path">The normalized absolute path.</param>
    /// <returns>The parent path, or null.</returns>
    private static string? GetParentPath(string path)
    {
        try
        {
            var parent = Directory.GetParent(path);
            return parent?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Checks if a directory has any subdirectories, returning false on access errors.
    /// </summary>
    /// <param name="path">The directory path to check.</param>
    /// <returns>True if subdirectories exist and are accessible.</returns>
    private static bool SafeHasSubdirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                       or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Determines if a directory is a critical system/hidden directory that should be
    ///     filtered out from the browser to avoid confusion (e.g. $RECYCLE.BIN, System Volume Information).
    /// </summary>
    /// <param name="dirInfo">The directory info to check.</param>
    /// <returns>True if the directory should be hidden from the browser.</returns>
    private static bool IsSystemOrHiddenCritical(DirectoryInfo dirInfo)
    {
        // On Windows, filter out system-level hidden dirs that are never valid trash targets
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var attrs = dirInfo.Attributes;
        // Only hide dirs that are BOTH hidden AND system (e.g. $RECYCLE.BIN, System Volume Information)
        // Regular hidden folders (like .jellyfin-trash) should still be visible
        return attrs.HasFlag(FileAttributes.Hidden) && attrs.HasFlag(FileAttributes.System);
    }
}