using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;

/// <summary>
///     Implementation of IFolderBrowserService that provides server-side directory browsing for the admin settings folder picker UI.
/// </summary>
public class FolderBrowserService : IFolderBrowserService
{
    private const string ErrorDirectoryDoesNotExist = "Directory does not exist.";
    private const string ErrorCannotAccessDirectory = "Cannot access this directory.";

    private static readonly string[] SafeHiddenPrefixes =
    [
        ".jellyfin-trash",
        ".Trash-",
    ];

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
            var entries = _isWindows ? GetWindowsRootEntries() : GetUnixRootEntries();

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

    /// <summary>
    ///     Enumerates ready fixed/network/RAM/removable drives as root entries (Windows branch).
    /// </summary>
    private List<FolderEntry> GetWindowsRootEntries()
    {
        var entries = new List<FolderEntry>();

        // On Windows, list available drive letters. DriveInfo property access (IsReady, DriveType, Name) can throw for problematic drives, so all checks are inside the per-drive try/catch to ensure a single bad drive cannot abort the entire listing.
        foreach (var drive in DriveInfo.GetDrives())
        {
            var entry = TryBuildDriveEntry(drive);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>
    ///     Builds a root FolderEntry for a single drive, or returns null when the drive is not ready, is an unsupported type, or is inaccessible.
    /// </summary>
    private FolderEntry? TryBuildDriveEntry(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Network
                    or DriveType.Ram or DriveType.Removable))
            {
                return null;
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

            return new FolderEntry
            {
                Name = displayName,
                Path = rootPath,
                HasChildren = hasChildren
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SecurityException)
        {
            // Guard for consistency with the volume-label log above. Both sites use constant messages today, but guarding uniformly prevents a future maintainer from adding a parameterized argument (e.g.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Skipping inaccessible drive while enumerating roots");
            }

            return null;
        }
    }

    /// <summary>
    ///     Returns the single "/" root entry (Linux/macOS branch).
    /// </summary>
    private static List<FolderEntry> GetUnixRootEntries()
    {
        // On Linux/macOS, root is always "/"
        var hasChildren = SafeHasSubdirectories("/");
        return
        [
            new FolderEntry
            {
                Name = "/",
                Path = "/",
                HasChildren = hasChildren
            }
        ];
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
                return ResolveMissingDirectoryError(dirInfo);
            }

            return BuildChildrenListing(dirInfo, normalizedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(ex, "Error browsing directory {Path}", SanitizeForLog(path));
            return new FolderBrowseResult { Error = ErrorCannotAccessDirectory };
        }
    }

    /// <summary>
    ///     Distinguishes a genuinely missing directory from a permission-denied one (both surface as Exists == false) and returns the appropriate error result.
    /// </summary>
    private static FolderBrowseResult ResolveMissingDirectoryError(DirectoryInfo dirInfo)
    {
        // DirectoryInfo.Exists returns false for both missing AND permission-denied directories.
        // Attempt to read attributes to distinguish the two cases.
        try
        {
            _ = dirInfo.Attributes;
            // If we get here without exception, the directory truly does not exist.
            return new FolderBrowseResult { Error = ErrorDirectoryDoesNotExist };
        }
        catch (UnauthorizedAccessException)
        {
            return new FolderBrowseResult { Error = ErrorCannotAccessDirectory };
        }
        catch (SecurityException)
        {
            return new FolderBrowseResult { Error = ErrorCannotAccessDirectory };
        }
        catch (DirectoryNotFoundException)
        {
            return new FolderBrowseResult { Error = ErrorDirectoryDoesNotExist };
        }
        catch (IOException)
        {
            return new FolderBrowseResult { Error = ErrorCannotAccessDirectory };
        }
        catch (ArgumentException)
        {
            return new FolderBrowseResult { Error = ErrorDirectoryDoesNotExist };
        }
    }

    /// <summary>
    ///     Enumerates the accessible immediate subdirectories of an existing directory and builds the browse result, returning an error result when the directory itself cannot be enumerated.
    /// </summary>
    private FolderBrowseResult BuildChildrenListing(DirectoryInfo dirInfo, string normalizedPath)
    {
        var entries = new List<FolderEntry>();

        // Enumerate immediate subdirectories, skipping those we can't access. EnumerateDirectories() returns a lazy iterator - exceptions from MoveNext() (e.g.
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
                Error = ErrorCannotAccessDirectory
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

    /// <inheritdoc />
    public string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path must not be empty.";
        }

        // Reject path traversal patterns (segment-aware to avoid false positives on names like "my..folder").
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

            // Refuse sensitive system / application directories (Jellyfin's own /config, /data, OS roots like /etc, C:\Windows, etc.).
            if (PathValidator.IsSensitiveSystemPath(normalized))
            {
                _logger.LogWarning(
                    "Folder-browse request refused for sensitive system path {Path}",
                    SanitizeForLog(path));
                return "This is a protected system folder and cannot be browsed.";
            }

            // Verify directory exists - use Attributes to distinguish access-denied from missing. Directory.Exists() returns false for BOTH cases, which would collapse permission errors into the wrong "does not exist" message.
            return ValidateDirectoryTarget(normalized, path);
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
    }

    /// <summary>
    ///     Verifies that resolves to a browsable directory: it exists, is a directory (not a file), and , when it is a symlink , does not resolve to a sensitive system target.
    /// </summary>
    private string? ValidateDirectoryTarget(string normalized, string path)
    {
        try
        {
            if (Directory.Exists(normalized))
            {
                return ValidateExistingDirectory(normalized, path);
            }

            if (File.Exists(normalized))
            {
                // Path exists but is a file, not a directory.
                return "Path must point to a directory.";
            }

            // Path does not exist as file or directory - but Directory.Exists also returns false when the caller lacks read permission on the parent.
            var missingAttrs = new DirectoryInfo(normalized).Attributes;
            if (missingAttrs == (FileAttributes)(-1))
            {
                return ErrorDirectoryDoesNotExist;
            }

            // Attributes returned a real value but Directory.Exists said no - treat as
            // "not a directory" (e.g. concurrent modification, or a non-directory entry).
            return "Path must point to a directory.";
        }
        catch (UnauthorizedAccessException)
        {
            return ErrorCannotAccessDirectory;
        }
        catch (SecurityException)
        {
            return ErrorCannotAccessDirectory;
        }
        catch (DirectoryNotFoundException)
        {
            return ErrorDirectoryDoesNotExist;
        }
        catch (FileNotFoundException)
        {
            return ErrorDirectoryDoesNotExist;
        }
        catch (IOException)
        {
            return ErrorCannotAccessDirectory;
        }
    }

    /// <summary>
    ///     Validates a path already confirmed to exist as a directory: rejects a non-directory entry and, for symlinks, refuses a browse into a link that resolves to a sensitive system target.
    /// </summary>
    private string? ValidateExistingDirectory(string normalized, string path)
    {
        // Path exists AND is a directory - happy path.
        var attrs = new DirectoryInfo(normalized).Attributes;
        if (attrs != (FileAttributes)(-1) && !attrs.HasFlag(FileAttributes.Directory))
        {
            return "Path must point to a directory.";
        }

        // Symlink escape guard: the lexical IsSensitiveSystemPath check above cannot see through a directory link whose own path is innocuous but which points at a sensitive target (e.g.
        if (attrs != (FileAttributes)(-1) && (attrs & FileAttributes.ReparsePoint) != 0)
        {
            var resolved = new DirectoryInfo(normalized).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null && PathValidator.IsSensitiveSystemPath(resolved.FullName))
            {
                _logger.LogWarning(
                    "Folder-browse request refused: {Path} is a link to a sensitive system path",
                    SanitizeForLog(path));
                return "This is a protected system folder and cannot be browsed.";
            }
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
    ///     Determines if a directory is a critical system/hidden directory that should be filtered out from the browser to avoid confusion (e.g.
    /// </summary>
    /// <param name="dirInfo">The directory info to check.</param>
    /// <returns>True if the directory should be hidden from the browser.</returns>
    private static bool IsSystemOrHiddenCritical(DirectoryInfo dirInfo)
    {
        // Hide sensitive system / application directories from listings entirely, on every OS - Jellyfin's own /config, /data, /cache and OS roots like /etc, C:\Windows.
        if (PathValidator.IsSensitiveSystemPath(dirInfo.FullName))
        {
            return true;
        }

        // Symlink/junction escape guard: IsSensitiveSystemPath is purely lexical and Path.GetFullPath does NOT dereference symlinks on .NET, so a directory link whose OWN path is innocuous (e.g.
        try
        {
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null && PathValidator.IsSensitiveSystemPath(resolved.FullName))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot resolve the link (broken, permission, cyclic) - treat as unlistable/critical
            // so an unresolvable reparse point is never browsed into.
            return true;
        }

        // On Windows, filter out system-level hidden dirs (e.g. $RECYCLE.BIN, System Volume Information).
        if (OperatingSystem.IsWindows())
        {
            var attrs = dirInfo.Attributes;
            return attrs.HasFlag(FileAttributes.Hidden) && attrs.HasFlag(FileAttributes.System);
        }

        // On Linux/macOS, hide dot-directories unless they are known-safe plugin paths.
        // This prevents sensitive dirs like .ssh, .gnupg, .aws from being shown in the UI.
        if (dirInfo.Name.StartsWith('.'))
        {
            if (SafeHiddenPrefixes.Any(prefix => dirInfo.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
}