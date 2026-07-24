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
    private readonly bool _isWindows;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FolderBrowserService" /> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public FolderBrowserService(ILogger<FolderBrowserService> logger)
        : this(logger, OperatingSystem.IsWindows())
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FolderBrowserService" /> class
    ///     with an explicit OS-detection override. Test-only overload that lets callers
    ///     exercise both the Windows drive-enumeration branch and the Unix "/" branch
    ///     regardless of the actual host operating system.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="isWindows">Whether to run the Windows drive-enumeration branch.</param>
    internal FolderBrowserService(ILogger<FolderBrowserService> logger, bool isWindows)
    {
        _logger = logger;
        _isWindows = isWindows;
    }

    /// <inheritdoc />
    public FolderBrowseResult GetRoots()
    {
        try
        {
            var entries = new List<FolderEntry>();

            if (_isWindows)
            {
                // On Windows, list available drive letters.
                // DriveInfo property access (IsReady, DriveType, Name) can throw for
                // problematic drives, so all checks are inside the per-drive try/catch
                // to ensure a single bad drive cannot abort the entire listing.
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Network
                                or DriveType.Ram or DriveType.Removable))
                        {
                            continue;
                        }

                        var rootPath = drive.RootDirectory.FullName;
                        var hasChildren = SafeHasSubdirectories(rootPath);

                        var baseName = rootPath.TrimEnd(Path.DirectorySeparatorChar);
                        string? volumeLabel = null;
                        try
                        {
                            volumeLabel = drive.VolumeLabel;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                       or SecurityException)
                        {
                            if (_logger.IsEnabled(LogLevel.Debug))
                            {
                                _logger.LogDebug(ex, "Could not read volume label for drive {Drive}", baseName);
                            }
                        }

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
                        // CA1873: guard for consistency with the volume-label log above.
                        // Both sites use constant messages today, but guarding uniformly
                        // prevents a future maintainer from adding a parameterized argument
                        // (e.g. drive letter) and silently regressing the pattern.
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(ex, "Skipping inaccessible drive while enumerating roots");
                        }
                    }
                }

                entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
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
                // DirectoryInfo.Exists returns false for both missing AND permission-denied directories.
                // Attempt to read attributes to distinguish the two cases.
                try
                {
                    _ = dirInfo.Attributes;
                    // If we get here without exception, the directory truly does not exist.
                    return new FolderBrowseResult { Error = "Directory does not exist." };
                }
                catch (UnauthorizedAccessException)
                {
                    return new FolderBrowseResult { Error = "Cannot access this directory." };
                }
                catch (SecurityException)
                {
                    return new FolderBrowseResult { Error = "Cannot access this directory." };
                }
                catch (DirectoryNotFoundException)
                {
                    return new FolderBrowseResult { Error = "Directory does not exist." };
                }
                catch (IOException)
                {
                    return new FolderBrowseResult { Error = "Cannot access this directory." };
                }
                catch (ArgumentException)
                {
                    return new FolderBrowseResult { Error = "Directory does not exist." };
                }
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
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(ex, "Skipping inaccessible directory {Dir}", SanitizeForLog(subdir.FullName));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                           or SecurityException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Cannot enumerate children of {Path}", SanitizeForLog(normalizedPath));
                }

                var errorParentPath = GetParentPath(normalizedPath);
                return new FolderBrowseResult
                {
                    CurrentPath = normalizedPath,
                    ParentPath = errorParentPath,
                    CanGoUp = errorParentPath != null,
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
            _logger.LogWarning(ex, "Error browsing directory {Path}", SanitizeForLog(path));
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

        // Reject path traversal patterns (segment-aware to avoid false positives on names like "my..folder").
        // Always split on both separators explicitly so backslash-encoded traversal is caught on Linux too
        // (on Linux Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar == '/').
        var segments = path.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static s => s == ".."))
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

            // Verify directory exists — use Attributes to distinguish access-denied from missing.
            // Directory.Exists() returns false for BOTH cases, which would collapse
            // permission errors into the wrong "does not exist" message. However, on modern .NET
            // DirectoryInfo.Attributes silently returns (FileAttributes)(-1) for non-existent paths
            // rather than throwing DirectoryNotFoundException — so we probe existence first via
            // File.Exists/Directory.Exists (which agree on the "does the path exist at all" question)
            // and only fall through to the Attributes path to distinguish access-denied when the
            // entry appears to be missing.
            try
            {
                if (Directory.Exists(normalized))
                {
                    // Path exists AND is a directory — happy path.
                    var attrs = new DirectoryInfo(normalized).Attributes;
                    if (attrs != (FileAttributes)(-1) && !attrs.HasFlag(FileAttributes.Directory))
                    {
                        return "Path must point to a directory.";
                    }
                }
                else if (File.Exists(normalized))
                {
                    // Path exists but is a file, not a directory.
                    return "Path must point to a directory.";
                }
                else
                {
                    // Path does not exist as file or directory — but Directory.Exists also returns
                    // false when the caller lacks read permission on the parent. Probe Attributes
                    // to distinguish the two cases: a permission-denied path will throw
                    // UnauthorizedAccessException/SecurityException/IOException, while a truly
                    // missing path returns (FileAttributes)(-1) (or throws DirectoryNotFoundException
                    // on some runtimes).
                    var attrs = new DirectoryInfo(normalized).Attributes;
                    if (attrs == (FileAttributes)(-1))
                    {
                        return "Directory does not exist.";
                    }

                    // Attributes returned a real value but Directory.Exists said no — treat as
                    // "not a directory" (e.g. concurrent modification, or a non-directory entry).
                    return "Path must point to a directory.";
                }
            }
            catch (UnauthorizedAccessException)
            {
                return "Cannot access this directory.";
            }
            catch (SecurityException)
            {
                return "Cannot access this directory.";
            }
            catch (DirectoryNotFoundException)
            {
                return "Directory does not exist.";
            }
            catch (FileNotFoundException)
            {
                return "Directory does not exist.";
            }
            catch (IOException)
            {
                return "Cannot access this directory.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or SecurityException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Invalid folder-browse path {Path}", SanitizeForLog(path));
            }

            return "Invalid path.";
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
            foreach (var childPath in Directory.EnumerateDirectories(path))
            {
                try
                {
                    var child = new DirectoryInfo(childPath);
                    if (!IsSystemOrHiddenCritical(child))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                               or SecurityException or ArgumentException)
                {
                    // Ignore individual inaccessible children when probing for visible descendants.
                }
            }

            return false;
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

    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
}