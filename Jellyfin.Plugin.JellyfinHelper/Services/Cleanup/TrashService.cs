using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Manages a trash/recycle bin for deleted media items instead of permanent deletion.
///     Items are moved to a timestamped trash folder and can be permanently purged after a retention period.
///     Registered as a singleton via DI.
/// </summary>
public class TrashService : ITrashService
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    ///     Maximum length of a single path component (filename or directory name).
    ///     POSIX NAME_MAX is 255 bytes on virtually all Linux/macOS filesystems.
    ///     Windows NTFS caps individual components at 255 UTF-16 code units.
    ///     On non-Windows platforms this limit is enforced in bytes (UTF-8);
    ///     on Windows it is enforced in characters (UTF-16 code units).
    /// </summary>
    private const int MaxPathComponentLimit = 255;

    /// <summary>
    ///     Maximum allowed path length. Windows has a legacy MAX_PATH of 260; macOS defines
    ///     PATH_MAX as 1024; Linux allows up to 4096. We cap at 259 on Windows, 1023 on macOS,
    ///     and 4095 on Linux to guarantee the resulting path is always valid even after
    ///     suffixes or a GUID are appended.
    ///     On non-Windows platforms this is enforced in bytes (UTF-8);
    ///     on Windows it is enforced in characters (UTF-16 code units).
    /// </summary>
    private static readonly int MaxPathLimit =
        OperatingSystem.IsWindows() ? 259 :
        OperatingSystem.IsMacOS() ? 1023 :
        4095;

    private static readonly int SeparatorSize = MeasureString(Path.DirectorySeparatorChar.ToString());

    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TrashService" /> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    public TrashService(IPluginLogService pluginLog)
    {
        _pluginLog = pluginLog;
    }

    /// <summary>
    ///     Gets the platform-aware string comparison for path containment checks.
    ///     Windows filesystems (NTFS, FAT) are case-insensitive; macOS default APFS is case-insensitive
    ///     (case-preserving); Linux (ext4, XFS) is case-sensitive.
    /// </summary>
    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <inheritdoc />
    public long MoveToTrash(string sourcePath, string trashBasePath, ILogger logger, DateTime? utcNow = null)
    {
        try
        {
            if (!Directory.Exists(sourcePath))
            {
                _pluginLog.LogWarning("Trash", $"Source path does not exist for trash: {sourcePath}", logger: logger);
                return 0;
            }

            // Guard: prevent re-trashing items that are already inside the trash folder.
            // This can occur if a cleanup task's recursive directory scan inadvertently
            // includes the trash directory. Each re-trash prepends a timestamp prefix,
            // eventually exceeding PATH_MAX and causing an IOException.
            // Path.GetFullPath normalizes trailing separators, relative segments, and mixed slashes.
            var normalizedSource = Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashRoot = Path.GetFullPath(trashBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashPrefix = normalizedTrashRoot + Path.DirectorySeparatorChar;
            if (normalizedSource.Equals(normalizedTrashRoot, PathComparison)
                || normalizedSource.StartsWith(normalizedTrashPrefix, PathComparison))
            {
                _pluginLog.LogWarning(
                    "Trash",
                    $"Source is already inside trash folder, skipping: {sourcePath}",
                    logger: logger);
                return 0;
            }

            var dirName = Path.GetFileName(normalizedSource);
            var timestamp = (utcNow ?? DateTime.UtcNow).ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{dirName}";
            var trashItemPath = Path.Join(trashBasePath, trashItemName);

            // Ensure trash folder exists
            Directory.CreateDirectory(trashBasePath);

            // Avoid collision if an item with the same name was already trashed in the same second
            trashItemPath = ResolveCollision(trashItemPath);

            // Calculate size before moving
            var size = CalculateDirectorySize(sourcePath);

            // Move to trash
            Directory.Move(sourcePath, trashItemPath);

            _pluginLog.LogInfo("Trash", $"Moved to trash: {sourcePath} → {trashItemPath} ({size} bytes)", logger);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to move directory to trash: {sourcePath}", ex, logger);
            return 0;
        }
    }

    /// <inheritdoc />
    public long MoveFileToTrash(string sourceFilePath, string trashBasePath, ILogger logger, DateTime? utcNow = null)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
            {
                _pluginLog.LogWarning(
                    "Trash",
                    $"Source file does not exist for trash: {sourceFilePath}",
                    logger: logger);
                return 0;
            }

            // Guard: prevent re-trashing files that are already inside the trash folder.
            // This mirrors the equivalent guard in MoveDirectoryToTrash() and prevents
            // path-length growth from repeated timestamp prefixing.
            var normalizedFile = Path.GetFullPath(sourceFilePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashRoot = Path.GetFullPath(trashBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashPrefix = normalizedTrashRoot + Path.DirectorySeparatorChar;
            if (normalizedFile.Equals(normalizedTrashRoot, PathComparison)
                || normalizedFile.StartsWith(normalizedTrashPrefix, PathComparison))
            {
                _pluginLog.LogWarning(
                    "Trash",
                    $"Source file is already inside trash folder, skipping: {sourceFilePath}",
                    logger: logger);
                return 0;
            }

            var fileName = Path.GetFileName(normalizedFile);
            var timestamp = (utcNow ?? DateTime.UtcNow).ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{fileName}";
            var trashItemPath = Path.Join(trashBasePath, trashItemName);

            // Ensure trash folder exists before ResolveCollision so File.Exists checks are valid
            Directory.CreateDirectory(trashBasePath);

            // Avoid collision if an item with the same name was already trashed in the same second
            trashItemPath = ResolveCollision(trashItemPath);

            // Get size before moving
            var size = new FileInfo(sourceFilePath).Length;

            // Move to trash
            File.Move(sourceFilePath, trashItemPath);

            _pluginLog.LogInfo(
                "Trash",
                $"Moved file to trash: {sourceFilePath} → {trashItemPath} ({size} bytes)",
                logger);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to move file to trash: {sourceFilePath}", ex, logger);
            return 0;
        }
    }

    /// <inheritdoc />
    public (long BytesFreed, int ItemsPurged) PurgeExpiredTrash(
        string trashBasePath,
        int retentionDays,
        ILogger logger,
        DateTime? utcNow = null)
    {
        long totalBytesFreed = 0;
        var itemsPurged = 0;

        if (!Directory.Exists(trashBasePath))
        {
            return (0, 0);
        }

        // retentionDays <= 0 is treated as "disabled" — never purge anything.
        // Callers that want to purge everything immediately should pass retentionDays = 1
        // (or use a positive value). Zero and negative values are sentinel "off" states
        // consistent with how SeerrCleanupAgeDays = 0 means "feature disabled".
        if (retentionDays <= 0)
        {
            return (0, 0);
        }

        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-retentionDays);

        try
        {
            // Purge old directories
            foreach (var dir in Directory.GetDirectories(trashBasePath))
            {
                var dirName = Path.GetFileName(dir);
                if (!TryParseTrashTimestamp(dirName, out var timestamp) || timestamp >= cutoff)
                {
                    continue;
                }

                try
                {
                    var size = CalculateDirectorySize(dir);
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Delete only the symlink/junction itself, not what it points to
                        dirInfo.Delete();
                    }
                    else
                    {
                        Directory.Delete(dir, true);
                    }

                    totalBytesFreed += size;
                    itemsPurged++;
                    _pluginLog.LogInfo(
                        "Trash",
                        $"Purged expired trash directory: {dir} ({size} bytes, created {timestamp})",
                        logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _pluginLog.LogError("Trash", $"Failed to purge trash directory: {dir}", ex, logger);
                }
            }

            // Purge old files
            foreach (var file in Directory.GetFiles(trashBasePath))
            {
                var fileName = Path.GetFileName(file);
                if (!TryParseTrashTimestamp(fileName, out var timestamp) || timestamp >= cutoff)
                {
                    continue;
                }

                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    totalBytesFreed += size;
                    itemsPurged++;
                    _pluginLog.LogInfo(
                        "Trash",
                        $"Purged expired trash file: {file} ({size} bytes, created {timestamp})",
                        logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _pluginLog.LogError("Trash", $"Failed to purge trash file: {file}", ex, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to enumerate trash folder: {trashBasePath}", ex, logger);
        }

        return (totalBytesFreed, itemsPurged);
    }

    /// <inheritdoc />
    public (long TotalSize, int ItemCount) GetTrashSummary(string trashBasePath, ILogger? logger = null)
    {
        if (!Directory.Exists(trashBasePath))
        {
            return (0, 0);
        }

        long totalSize = 0;
        var itemCount = 0;

        try
        {
            var dirs = Directory.EnumerateDirectories(trashBasePath);
            foreach (var dir in dirs)
            {
                itemCount++;
                totalSize += CalculateDirectorySize(dir);
            }

            var files = Directory.EnumerateFiles(trashBasePath);
            foreach (var f in files)
            {
                itemCount++;
                totalSize += new FileInfo(f).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("Trash", $"Partial trash summary — could not fully enumerate {trashBasePath}: {ex.Message}", ex, logger);
        }

        return (totalSize, itemCount);
    }

    /// <inheritdoc />
    public IReadOnlyList<TrashItemInfo> GetTrashContents(string trashBasePath, int retentionDays, ILogger? logger = null)
    {
        var items = new List<TrashItemInfo>();

        if (!Directory.Exists(trashBasePath))
        {
            return items;
        }

        try
        {
            // Directories
            foreach (var dir in Directory.GetDirectories(trashBasePath))
            {
                var dirName = Path.GetFileName(dir);
                var originalName = ExtractOriginalName(dirName);
                var size = CalculateDirectorySize(dir);

                DateTime? trashedAt = null;
                DateTime? purgesAt = null;
                if (TryParseTrashTimestamp(dirName, out var timestamp))
                {
                    trashedAt = timestamp;
                    purgesAt = retentionDays > 0 ? timestamp.AddDays(retentionDays) : (DateTime?)null;
                }

                items.Add(
                    new TrashItemInfo
                    {
                        Name = originalName,
                        FullName = dirName,
                        Size = size,
                        IsDirectory = true,
                        TrashedAt = trashedAt,
                        PurgesAt = purgesAt
                    });
            }

            // Files
            foreach (var file in Directory.GetFiles(trashBasePath))
            {
                var fileName = Path.GetFileName(file);
                var originalName = ExtractOriginalName(fileName);
                var size = new FileInfo(file).Length;

                DateTime? trashedAt = null;
                DateTime? purgesAt = null;
                if (TryParseTrashTimestamp(fileName, out var timestamp))
                {
                    trashedAt = timestamp;
                    purgesAt = retentionDays > 0 ? timestamp.AddDays(retentionDays) : (DateTime?)null;
                }

                items.Add(
                    new TrashItemInfo
                    {
                        Name = originalName,
                        FullName = fileName,
                        Size = size,
                        IsDirectory = false,
                        TrashedAt = trashedAt,
                        PurgesAt = purgesAt
                    });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogWarning("Trash", $"Partial trash contents — could not fully enumerate {trashBasePath}: {ex.Message}", ex, logger);
        }

        // Sort by trashed date descending (newest first)
        items.Sort((a, b) => (b.TrashedAt ?? DateTime.MinValue).CompareTo(a.TrashedAt ?? DateTime.MinValue));

        return items;
    }

    /// <inheritdoc />
    public (int Moved, int Failed) RelocateTrashContents(string oldTrashPath, string newTrashPath, ILogger logger)
    {
        var moved = 0;
        var failed = 0;

        if (!Directory.Exists(oldTrashPath))
        {
            _pluginLog.LogInfo("Trash", $"Old trash path does not exist, nothing to relocate: {oldTrashPath}", logger);
            return (0, 0);
        }

        // Safety: normalize paths and create destination — guard against invalid/malformed paths
        string normalizedOld;
        string normalizedNew;
        try
        {
            normalizedOld = Path.GetFullPath(oldTrashPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            normalizedNew = Path.GetFullPath(newTrashPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _pluginLog.LogError("Trash", $"Failed to normalize trash relocation paths: {oldTrashPath} → {newTrashPath}", ex, logger);
            return (0, 0);
        }

        if (string.Equals(normalizedOld, normalizedNew, PathComparison))
        {
            _pluginLog.LogWarning("Trash", "Old and new trash paths are identical, skipping relocation.", logger: logger);
            return (0, 0);
        }

        // Safety: new path must not be inside old path (would cause recursive move)
        var oldPrefix = normalizedOld + Path.DirectorySeparatorChar;
        if (normalizedNew.StartsWith(oldPrefix, PathComparison))
        {
            _pluginLog.LogError("Trash", $"New trash path is inside old trash path, aborting relocation: {newTrashPath}", null, logger);
            return (0, 0);
        }

        // Safety: old path must not be inside new path (would cause data loss)
        var newPrefix = normalizedNew + Path.DirectorySeparatorChar;
        if (normalizedOld.StartsWith(newPrefix, PathComparison))
        {
            _pluginLog.LogError("Trash", $"Old trash path is inside new trash path, aborting relocation: {oldTrashPath}", null, logger);
            return (0, 0);
        }

        // Ensure destination exists
        try
        {
            Directory.CreateDirectory(newTrashPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to create destination trash directory: {newTrashPath}", ex, logger);
            return (0, 0);
        }

        // Move directories
        try
        {
            foreach (var dir in Directory.GetDirectories(oldTrashPath))
            {
                var dirName = Path.GetFileName(dir);
                var destPath = Path.Join(newTrashPath, dirName);

                try
                {
                    destPath = ResolveCollision(destPath);
                    Directory.Move(dir, destPath);
                    moved++;
                    _pluginLog.LogInfo("Trash", $"Relocated directory: {dir} → {destPath}", logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _pluginLog.LogError("Trash", $"Failed to relocate directory: {dir}", ex, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to enumerate directories in old trash: {oldTrashPath}", ex, logger);
        }

        // Move files
        try
        {
            foreach (var file in Directory.GetFiles(oldTrashPath))
            {
                var fileName = Path.GetFileName(file);
                var destPath = Path.Join(newTrashPath, fileName);

                try
                {
                    destPath = ResolveCollision(destPath);
                    File.Move(file, destPath);
                    moved++;
                    _pluginLog.LogInfo("Trash", $"Relocated file: {file} → {destPath}", logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _pluginLog.LogError("Trash", $"Failed to relocate file: {file}", ex, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError("Trash", $"Failed to enumerate files in old trash: {oldTrashPath}", ex, logger);
        }

        // Remove the old trash folder if it is now empty
        TryRemoveEmptyDirectory(oldTrashPath, logger);

        _pluginLog.LogInfo("Trash", $"Relocation complete: {moved} moved, {failed} failed ({oldTrashPath} → {newTrashPath})", logger);
        return (moved, failed);
    }

    /// <summary>
    ///     Attempts to remove a directory if it is empty (no files or subdirectories).
    ///     Silently ignores errors if the directory cannot be removed.
    /// </summary>
    /// <param name="directoryPath">The directory to remove.</param>
    /// <param name="logger">The logger.</param>
    private void TryRemoveEmptyDirectory(string directoryPath, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            if (Directory.GetFileSystemEntries(directoryPath).Length == 0)
            {
                Directory.Delete(directoryPath, false);
                _pluginLog.LogInfo("Trash", $"Removed empty old trash folder: {directoryPath}", logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-critical: old folder stays if it can't be removed
            _pluginLog.LogWarning("Trash", $"Could not remove old trash folder: {directoryPath}", ex, logger);
        }
    }

    /// <summary>
    ///     Extracts the original name from a timestamped trash item name.
    ///     Format: "yyyyMMdd-HHmmss_originalname" → "originalname".
    /// </summary>
    /// <param name="trashItemName">The full trash item name including timestamp prefix.</param>
    /// <returns>The original name, or the full name if no timestamp prefix was found.</returns>
    internal static string ExtractOriginalName(string trashItemName)
    {
        if (string.IsNullOrEmpty(trashItemName) || trashItemName.Length <= TimestampFormat.Length + 1)
        {
            return trashItemName;
        }

        // Check if it matches the expected pattern: "yyyyMMdd-HHmmss_..."
        if (trashItemName[TimestampFormat.Length] == '_' &&
            TryParseTrashTimestamp(trashItemName, out _))
        {
            var original = trashItemName[(TimestampFormat.Length + 1)..];
            // Fall back to the full name when the original part is empty (e.g. item named "")
            return string.IsNullOrEmpty(original) ? trashItemName : original;
        }

        return trashItemName;
    }

    /// <summary>
    ///     Tries to parse the timestamp prefix from a trash item name.
    ///     Format: "yyyyMMdd-HHmmss_originalname".
    /// </summary>
    /// <param name="name">The trash item name including timestamp prefix.</param>
    /// <param name="timestamp">
    ///     When this method returns, contains the parsed timestamp, or <see cref="DateTime.MinValue" /> if
    ///     parsing failed.
    /// </param>
    /// <returns>True if the timestamp was successfully parsed; otherwise, false.</returns>
    internal static bool TryParseTrashTimestamp(string name, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;

        if (string.IsNullOrEmpty(name) || name.Length < TimestampFormat.Length + 1)
        {
            return false;
        }

        var timestampPart = name[..TimestampFormat.Length];
        return DateTime.TryParseExact(
            timestampPart,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    /// <summary>
    ///     Resolves naming collisions for trash items by appending a numeric suffix (_2, _3, …)
    ///     if the target path already exists as a file or directory.
    ///     The returned path is guaranteed to fit within the OS path-length limit.
    /// </summary>
    /// <param name="desiredPath">The initially desired trash path.</param>
    /// <returns>A collision-free path that does not yet exist on disk and is within the OS path limit.</returns>
    internal static string ResolveCollision(string desiredPath)
    {
        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;

        // Fail fast: if the directory path alone exhausts the OS path budget,
        // no child name (even a single character) can fit. Throwing here prevents
        // EnsurePathLength from silently returning an over-budget path that would
        // fail at Directory.Move/File.Move time.
        if (GetMaxComponentSize(directory) <= 0)
        {
            throw new IOException(
                $"Trash path is too long to create an entry under '{directory}'.");
        }

        var safePath = EnsurePathLength(desiredPath);
        if (!File.Exists(safePath) && !Directory.Exists(safePath))
        {
            return safePath;
        }

        var name = Path.GetFileName(desiredPath);
        var maxNameSize = GetMaxComponentSize(directory);

        // Fail fast when the remaining name budget cannot encode a unique suffix.
        // Without this guard, BuildSuffixSafeCandidate collapses every candidate to the
        // same truncated path and the retry loops would spin indefinitely.
        if (maxNameSize < MeasureString("_2"))
        {
            throw new IOException(
                $"Cannot create a unique trash path under '{directory}': insufficient path budget " +
                $"(available: {maxNameSize}, minimum required: {MeasureString("_2")}).");
        }

        for (var i = 2; i < 1000; i++)
        {
            var suffix = $"_{i}";
            var candidate = BuildSuffixSafeCandidate(directory, name, suffix);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // Extremely unlikely fallback: append a GUID and verify the final truncated path.
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var guidCandidate = BuildSuffixSafeCandidate(directory, name, $"_{Guid.NewGuid():N}");
            if (!File.Exists(guidCandidate) && !Directory.Exists(guidCandidate))
            {
                return guidCandidate;
            }
        }

        throw new IOException(
            $"Cannot create a unique trash path under '{directory}' within the remaining path budget.");
    }

    /// <summary>
    ///     Builds a length-safe candidate path by truncating the <paramref name="baseName" />
    ///     (not the suffix) so that the suffix is always preserved in the result.
    ///     This prevents the degenerate case where appending a suffix then truncating removes
    ///     the suffix entirely, causing every candidate to resolve to the same existing path.
    ///     On Unix, limits are enforced in UTF-8 bytes; on Windows, in UTF-16 code units (chars).
    /// </summary>
    private static string BuildSuffixSafeCandidate(string directory, string baseName, string suffix)
    {
        var maxNameSize = GetMaxComponentSize(directory);

        var suffixSize = MeasureString(suffix);
        var availableForBase = maxNameSize - suffixSize;
        if (availableForBase <= 0)
        {
            // Suffix alone fills the budget — truncate suffix as last resort.
            var truncatedSuffix = TruncateToSize(suffix, Math.Max(0, maxNameSize));
            return Path.Join(directory, truncatedSuffix);
        }

        var truncatedBase = TruncateToSize(baseName, availableForBase);
        return Path.Join(directory, $"{truncatedBase}{suffix}");
    }

    /// <summary>
    ///     Ensures the path does not exceed the platform path limit.
    ///     If it does, the file-name component is truncated (from the end, preserving the directory)
    ///     until the full path fits. The directory part is never truncated.
    ///     On Unix, limits are enforced in UTF-8 bytes; on Windows, in UTF-16 code units (chars).
    /// </summary>
    private static string EnsurePathLength(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);

        var maxNameSize = GetMaxComponentSize(directory);
        if (maxNameSize <= 0)
        {
            // Directory itself is already at or over the limit — nothing safe to do;
            // return the path as-is and let the caller's IOException handler log it.
            return path;
        }

        var pathSize = MeasureString(path);
        var nameSize = MeasureString(name);
        if (pathSize <= MaxPathLimit && nameSize <= MaxPathComponentLimit)
        {
            return path;
        }

        var truncatedName = TruncateToSize(name, maxNameSize);
        return Path.Join(directory, truncatedName);
    }

    /// <summary>
    ///     Computes the maximum allowed size for a path component given its parent directory.
    ///     Takes into account both the total path limit (PATH_MAX) and the per-component limit (NAME_MAX).
    ///     On Unix, sizes are UTF-8 byte counts; on Windows, UTF-16 char counts.
    /// </summary>
    private static int GetMaxComponentSize(string directory)
    {
        // +1 accounts for the path separator between directory and name
        var directorySize = MeasureString(directory);
        return Math.Min(
            MaxPathLimit - directorySize - SeparatorSize,
            MaxPathComponentLimit);
    }

    /// <summary>
    ///     Measures the size of a string in the platform-appropriate unit.
    ///     On Unix (where filesystem limits are byte-based), returns the UTF-8 byte count.
    ///     On Windows (where limits are char-based), returns the string length (UTF-16 code units).
    /// </summary>
    /// <param name="value">The string to measure.</param>
    /// <returns>The size in bytes (Unix) or characters (Windows).</returns>
    internal static int MeasureString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        return OperatingSystem.IsWindows() ? value.Length : Encoding.UTF8.GetByteCount(value);
    }

    /// <summary>
    ///     Truncates a string so that its platform-measured size does not exceed <paramref name="maxSize" />.
    ///     On Unix, truncates to fit within a UTF-8 byte budget without splitting multi-byte sequences.
    ///     On Windows, truncates to fit within a character (UTF-16 code unit) budget without splitting
    ///     surrogate pairs.
    /// </summary>
    /// <param name="value">The string to truncate.</param>
    /// <param name="maxSize">The maximum allowed size (bytes on Unix, chars on Windows).</param>
    /// <returns>
    ///     The original string if it already fits, or a truncated prefix that respects encoding boundaries.
    /// </returns>
    internal static string TruncateToSize(string value, int maxSize)
    {
        if (string.IsNullOrEmpty(value) || maxSize <= 0)
        {
            return string.Empty;
        }

        if (MeasureString(value) <= maxSize)
        {
            return value;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows: limit is in UTF-16 code units (chars).
            // Avoid splitting surrogate pairs.
            var length = Math.Min(value.Length, maxSize);
            if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            {
                length--;
            }

            return value[..length];
        }

        // Unix: limit is in UTF-8 bytes. Iterate through characters accumulating byte counts,
        // stopping before we would exceed the budget. Use Rune enumeration to avoid
        // splitting multi-byte sequences.
        var byteCount = 0;
        var charIndex = 0;
        while (charIndex < value.Length)
        {
            int runeByteCount;
            int charsConsumed;
            if (Rune.TryGetRuneAt(value, charIndex, out var rune))
            {
                runeByteCount = rune.Utf8SequenceLength;
                charsConsumed = rune.Utf16SequenceLength;
            }
            else
            {
                // Isolated surrogate or invalid — treat as replacement char (3 bytes in UTF-8)
                runeByteCount = 3;
                charsConsumed = 1;
            }

            if (byteCount + runeByteCount > maxSize)
            {
                break;
            }

            byteCount += runeByteCount;
            charIndex += charsConsumed;
        }

        return value[..charIndex];
    }

    /// <summary>
    ///     Calculates the total size of all files in a directory tree using <see cref="DirectoryInfo" />.
    ///     This is a self-contained implementation for the trash module which operates outside the
    ///     Jellyfin <c>IFileSystem</c> abstraction. For library paths, prefer
    ///     <see cref="FileSystemHelper.CalculateDirectorySize" /> instead.
    /// </summary>
    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var dirInfo = new DirectoryInfo(path);
            size += dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Access errors are expected for inaccessible directories during size calculation
        }

        return size;
    }

    /// <inheritdoc />
    public TrashPathAccessResult CheckPathAccess(string path, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new TrashPathAccessResult
            {
                Exists = false,
                CanRead = false,
                CanWrite = false,
                ErrorMessage = "Path is empty."
            };
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _pluginLog.LogWarning("Trash", $"Path access check failed — invalid path: {path} ({ex.Message})", ex, logger);
            return new TrashPathAccessResult
            {
                Exists = false,
                CanRead = false,
                CanWrite = false,
                ErrorMessage = $"Invalid path: {ex.Message}"
            };
        }

        // If the path exists as a directory, check read/write on it directly.
        if (Directory.Exists(fullPath))
        {
            var canRead = CanReadDirectory(fullPath);
            var canWrite = CanWriteDirectory(fullPath);

            if (!canRead || !canWrite)
            {
                var issue = !canRead && !canWrite ? "read or write" : !canRead ? "read" : "write";
                var msg = $"Insufficient permissions: cannot {issue} path '{fullPath}'.";
                _pluginLog.LogWarning("Trash", msg, logger: logger);
                return new TrashPathAccessResult
                {
                    Exists = true,
                    CanRead = canRead,
                    CanWrite = canWrite,
                    ErrorMessage = msg
                };
            }

            _pluginLog.LogDebug("Trash", $"Path access check OK (exists, read+write): {fullPath}");
            return new TrashPathAccessResult { Exists = true, CanRead = true, CanWrite = true };
        }

        // Path does not exist — walk up to the nearest existing parent and check if we can create there.
        var parent = fullPath;
        while (!string.IsNullOrEmpty(parent))
        {
            parent = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            if (Directory.Exists(parent))
            {
                var canWrite = CanWriteDirectory(parent);
                if (!canWrite)
                {
                    var msg = $"Cannot create trash folder at '{fullPath}': no write permission on parent '{parent}'.";
                    _pluginLog.LogWarning("Trash", msg, logger: logger);
                    return new TrashPathAccessResult
                    {
                        Exists = false,
                        CanRead = true,
                        CanWrite = false,
                        ErrorMessage = msg
                    };
                }

                _pluginLog.LogDebug("Trash", $"Path access check OK (not yet created, parent writable): {fullPath}");
                return new TrashPathAccessResult { Exists = false, CanRead = true, CanWrite = true };
            }
        }

        // No existing ancestor found (should not happen on valid filesystems)
        var fallbackMsg = $"Cannot verify access for '{fullPath}': no existing parent directory found.";
        _pluginLog.LogWarning("Trash", fallbackMsg, logger: logger);
        return new TrashPathAccessResult
        {
            Exists = false,
            CanRead = false,
            CanWrite = false,
            ErrorMessage = fallbackMsg
        };
    }

    /// <summary>
    ///     Attempts to enumerate the contents of a directory to verify read access.
    ///     Returns true if enumeration succeeds, false if access is denied.
    /// </summary>
    private static bool CanReadDirectory(string directoryPath)
    {
        try
        {
            Directory.GetFileSystemEntries(directoryPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Attempts to create and immediately delete a temporary probe file inside the directory
    ///     to verify write access. This is more reliable than checking ACLs because it respects
    ///     effective permissions, SELinux policies, and filesystem mount options.
    /// </summary>
    private static bool CanWriteDirectory(string directoryPath)
    {
        var probePath = Path.Join(directoryPath, $".jfh-access-probe-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            using (File.Create(probePath))
            {
                // File created successfully — write access confirmed.
            }

            created = true;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            if (created)
            {
                try
                {
                    File.Delete(probePath);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
