using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Manages a trash/recycle bin for deleted media items instead of permanent deletion.
/// </summary>
public class TrashService : ITrashService
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private const string LogCategory = "Trash";

    /// <summary>
    ///     Maximum length of a single path component (filename or directory name). POSIX NAME_MAX is 255 bytes on virtually all Linux/macOS filesystems.
    /// </summary>
    private const int MaxPathComponentLimit = 255;

    /// <summary>
    ///     Maximum allowed path length. Windows has a legacy MAX_PATH of 260; macOS defines PATH_MAX as 1024; Linux allows up to 4096.
    /// </summary>
    private static readonly int MaxPathLimit = GetMaxPathLimit();

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
    ///     Gets the platform-aware string comparison for path containment checks. Windows filesystems (NTFS, FAT) are case-insensitive; macOS default APFS is case-insensitive (case-preserving); Linux (ext4, XFS) is case-sensitive.
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
                _pluginLog.LogWarning(LogCategory, $"Source path does not exist for trash: {sourcePath}", logger: logger);
                return 0;
            }

            // Guard: prevent re-trashing items that are already inside the trash folder. This can occur if a cleanup task's recursive directory scan inadvertently includes the trash directory.
            var normalizedSource = Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashRoot = Path.GetFullPath(trashBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashPrefix = normalizedTrashRoot + Path.DirectorySeparatorChar;
            if (normalizedSource.Equals(normalizedTrashRoot, PathComparison)
                || normalizedSource.StartsWith(normalizedTrashPrefix, PathComparison))
            {
                _pluginLog.LogWarning(
                    LogCategory,
                    $"Source is already inside trash folder, skipping: {sourcePath}",
                    logger: logger);
                return 0;
            }

            var dirName = Path.GetFileName(normalizedSource);
            var timestamp = (utcNow ?? DateTime.UtcNow).ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{dirName}";
            var trashItemPath = Path.Join(trashBasePath, trashItemName);

            Directory.CreateDirectory(trashBasePath);

            // Avoid collision if an item with the same name was already trashed in the same second
            trashItemPath = ResolveCollision(trashItemPath);

            var size = CalculateDirectorySize(sourcePath);

            // TOCTOU mitigation: ResolveCollision found a free path, but between that check and Directory.Move another process could claim the same path.
            const int MoveRetries = 3;
            for (var moveAttempt = 0; ; moveAttempt++)
            {
                try
                {
                    MoveDirectory(sourcePath, trashItemPath);
                    break;
                }
                catch (IOException) when (
                    moveAttempt < MoveRetries &&
                    DestinationExists(trashItemPath))
                {
                    // Reuse the collision resolver so the retry path shares one naming strategy. EnsurePathLength truncates the name from the END, which on a deep trash directory with a tight budget would cut the trailing GUID and let two retries collapse to the identical path.
                    trashItemPath = ResolveCollision(
                        Path.Join(trashBasePath, $"{timestamp}_{dirName}_{Guid.NewGuid():N}"));
                }
            }

            _pluginLog.LogInfo(LogCategory, $"Moved to trash: {sourcePath} → {trashItemPath} ({size} bytes)", logger);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to move directory to trash: {sourcePath}", ex, logger);
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
                    LogCategory,
                    $"Source file does not exist for trash: {sourceFilePath}",
                    logger: logger);
                return 0;
            }

            // Prevent re-trashing files that are already inside the trash folder. This mirrors the equivalent guard in MoveDirectoryToTrash() and prevents path-length growth from repeated timestamp prefixing.
            var normalizedFile = Path.GetFullPath(sourceFilePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashRoot = Path.GetFullPath(trashBasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedTrashPrefix = normalizedTrashRoot + Path.DirectorySeparatorChar;
            if (normalizedFile.Equals(normalizedTrashRoot, PathComparison)
                || normalizedFile.StartsWith(normalizedTrashPrefix, PathComparison))
            {
                _pluginLog.LogWarning(
                    LogCategory,
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

            var size = new FileInfo(sourceFilePath).Length;

            File.Move(sourceFilePath, trashItemPath);

            _pluginLog.LogInfo(
                LogCategory,
                $"Moved file to trash: {sourceFilePath} → {trashItemPath} ({size} bytes)",
                logger);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to move file to trash: {sourceFilePath}", ex, logger);
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

        // retentionDays <= 0 is treated as "disabled" - never purge anything. Callers that want to purge everything immediately should pass retentionDays = 1 (or use a positive value).
        if (retentionDays <= 0)
        {
            return (0, 0);
        }

        var cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-retentionDays);

        try
        {
            // Guard: refuse to enumerate a trash folder that is itself a symlink/reparse point. If trashBasePath were replaced with a symlink pointing to a media library, enumerating its contents and deleting timestamp-matching entries would destroy real media files.
            if (IsReparsePoint(trashBasePath))
            {
                _pluginLog.LogError(
                    LogCategory,
                    $"Trash folder is a reparse point (symlink/junction) — skipping purge to prevent symlink traversal: {trashBasePath}",
                    logger: logger);
                return (0, 0);
            }

            // Purge old directories
            foreach (var dir in Directory.GetDirectories(trashBasePath))
            {
                PurgeExpiredDirectory(dir, cutoff, logger, ref totalBytesFreed, ref itemsPurged);
            }

            // Purge old files
            foreach (var file in Directory.GetFiles(trashBasePath))
            {
                PurgeExpiredFile(file, cutoff, logger, ref totalBytesFreed, ref itemsPurged);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to enumerate trash folder: {trashBasePath}", ex, logger);
        }

        return (totalBytesFreed, itemsPurged);
    }

    /// <summary>
    ///     Purges a single trash subdirectory when its timestamp-prefixed name is older than .
    /// </summary>
    private void PurgeExpiredDirectory(
        string dir,
        DateTime cutoff,
        ILogger logger,
        ref long totalBytesFreed,
        ref int itemsPurged)
    {
        var dirName = Path.GetFileName(dir);
        if (!TryParseTrashTimestamp(dirName, out var timestamp) || timestamp >= cutoff)
        {
            return;
        }

        try
        {
            if (IsReparsePoint(dir))
            {
                // Delete only the symlink/junction itself, not what it points to.
                // Size is 0 - only the link entry is removed, not the target data.
                try
                {
                    DeleteReparsePointLinkNode(dir);
                }
                catch (InvalidOperationException)
                {
                    // Concurrent replacement detected, so fail closed: leave entry unchanged.
                    _pluginLog.LogWarning(
                        LogCategory,
                        $"Reparse-point node changed type before purge, skipping: {dir}",
                        logger: logger);
                    return;
                }

                itemsPurged++;
                _pluginLog.LogInfo(
                    LogCategory,
                    $"Purged expired trash directory: {dir} (reparse point, created {timestamp})",
                    logger);
            }
            else
            {
                var size = CalculateDirectorySize(dir);
                Directory.Delete(dir, true);
                totalBytesFreed += size;
                itemsPurged++;
                _pluginLog.LogInfo(
                    LogCategory,
                    $"Purged expired trash directory: {dir} ({size} bytes, created {timestamp})",
                    logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to purge trash directory: {dir}", ex, logger);
        }
    }

    /// <summary>
    ///     Purges a single trash file when its timestamp-prefixed name is older than
    ///     <paramref name="cutoff"/>.
    /// </summary>
    private void PurgeExpiredFile(
        string file,
        DateTime cutoff,
        ILogger logger,
        ref long totalBytesFreed,
        ref int itemsPurged)
    {
        var fileName = Path.GetFileName(file);
        if (!TryParseTrashTimestamp(fileName, out var timestamp) || timestamp >= cutoff)
        {
            return;
        }

        try
        {
            var size = new FileInfo(file).Length;
            File.Delete(file);
            totalBytesFreed += size;
            itemsPurged++;
            _pluginLog.LogInfo(
                LogCategory,
                $"Purged expired trash file: {file} ({size} bytes, created {timestamp})",
                logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to purge trash file: {file}", ex, logger);
        }
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
            _pluginLog.LogWarning(LogCategory, $"Partial trash summary - could not fully enumerate {trashBasePath}: {ex.Message}", ex, logger);
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
            _pluginLog.LogWarning(LogCategory, $"Partial trash contents - could not fully enumerate {trashBasePath}: {ex.Message}", ex, logger);
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
            _pluginLog.LogInfo(LogCategory, $"Old trash path does not exist, nothing to relocate: {oldTrashPath}", logger);
            return (0, 0);
        }

        // Normalize paths and create destination - guard against invalid/malformed paths
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
            _pluginLog.LogError(LogCategory, $"Failed to normalize trash relocation paths: {oldTrashPath} → {newTrashPath}", ex, logger);
            return (0, 0);
        }

        if (string.Equals(normalizedOld, normalizedNew, PathComparison))
        {
            _pluginLog.LogWarning(LogCategory, "Old and new trash paths are identical, skipping relocation.", logger: logger);
            return (0, 0);
        }

        // New path must not be inside old path (would cause recursive move)
        var oldPrefix = normalizedOld + Path.DirectorySeparatorChar;
        if (normalizedNew.StartsWith(oldPrefix, PathComparison))
        {
            _pluginLog.LogError(LogCategory, $"New trash path is inside old trash path, aborting relocation: {newTrashPath}", null, logger);
            return (0, 0);
        }

        // Old path must not be inside new path (would cause data loss)
        var newPrefix = normalizedNew + Path.DirectorySeparatorChar;
        if (normalizedOld.StartsWith(newPrefix, PathComparison))
        {
            _pluginLog.LogError(LogCategory, $"Old trash path is inside new trash path, aborting relocation: {oldTrashPath}", null, logger);
            return (0, 0);
        }

        // Ensure destination exists
        try
        {
            Directory.CreateDirectory(newTrashPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to create destination trash directory: {newTrashPath}", ex, logger);
            return (0, 0);
        }

        // Move directories
        MoveTrashDirectories(oldTrashPath, newTrashPath, logger, ref moved, ref failed);

        // Move files
        MoveTrashFiles(oldTrashPath, newTrashPath, logger, ref moved, ref failed);

        TryRemoveEmptyDirectory(oldTrashPath, logger);

        _pluginLog.LogInfo(LogCategory, $"Relocation complete: {moved} moved, {failed} failed ({oldTrashPath} → {newTrashPath})", logger);
        return (moved, failed);
    }

    /// <summary>
    ///     Moves each subdirectory of <paramref name="oldTrashPath"/> into
    ///     <paramref name="newTrashPath"/>, resolving name collisions and tallying outcomes.
    /// </summary>
    private void MoveTrashDirectories(string oldTrashPath, string newTrashPath, ILogger logger, ref int moved, ref int failed)
    {
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
                    _pluginLog.LogInfo(LogCategory, $"Relocated directory: {dir} → {destPath}", logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _pluginLog.LogError(LogCategory, $"Failed to relocate directory: {dir}", ex, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to enumerate directories in old trash: {oldTrashPath}", ex, logger);
        }
    }

    /// <summary>
    ///     Moves each file of <paramref name="oldTrashPath"/> into <paramref name="newTrashPath"/>,
    ///     resolving name collisions and tallying outcomes.
    /// </summary>
    private void MoveTrashFiles(string oldTrashPath, string newTrashPath, ILogger logger, ref int moved, ref int failed)
    {
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
                    _pluginLog.LogInfo(LogCategory, $"Relocated file: {file} → {destPath}", logger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    _pluginLog.LogError(LogCategory, $"Failed to relocate file: {file}", ex, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pluginLog.LogError(LogCategory, $"Failed to enumerate files in old trash: {oldTrashPath}", ex, logger);
        }
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
                _pluginLog.LogInfo(LogCategory, $"Removed empty old trash folder: {directoryPath}", logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old folder stays if it can't be removed
            _pluginLog.LogWarning(LogCategory, $"Could not remove old trash folder: {directoryPath}", ex, logger);
        }
    }

    /// <summary>
    ///     Extracts the original name from a timestamped trash item name.
    ///     Format: "yyyyMMdd-HHmmss_originalname" -> "originalname".
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
    ///     Resolves naming collisions for trash items by appending a numeric suffix (_2, _3, ...) if the target path already exists as a file or directory.
    /// </summary>
    /// <param name="desiredPath">The initially desired trash path.</param>
    /// <returns>A collision-free path that does not yet exist on disk and is within the OS path limit.</returns>
    internal static string ResolveCollision(string desiredPath)
    {
        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;

        // Fail fast: if the directory path alone exhausts the OS path budget, no child name (even a single character) can fit.
        if (GetMaxComponentSize(directory) <= 0)
        {
            throw new IOException(
                $"Trash path is too long to create an entry under '{directory}'.");
        }

        var safePath = EnsurePathLength(desiredPath);
        if (!Path.Exists(safePath))
        {
            return safePath;
        }

        var name = Path.GetFileName(desiredPath);
        var maxNameSize = GetMaxComponentSize(directory);

        // Fail fast when the remaining name budget cannot encode a unique suffix. Without this guard, BuildSuffixSafeCandidate collapses every candidate to the same truncated path and the retry loops would spin indefinitely.
        if (maxNameSize < MeasureString("_2"))
        {
            throw new IOException(
                $"Cannot create a unique trash path under '{directory}': insufficient path budget " +
                $"(available: {maxNameSize}, minimum required: {MeasureString("_2")}).");
        }

        // A short numeric scan keeps human-readable names for the common few-collisions case. We deliberately cap this low (was 998) so that on high-latency mounts (NFS/SMB) we do not perform hundreds of stat round-trips.
        const int NumericScanLimit = 20;
        for (var i = 2; i < NumericScanLimit; i++)
        {
            var suffix = $"_{i}";
            var candidate = BuildSuffixSafeCandidate(directory, name, suffix);
            if (!Path.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fallback: append a GUID and verify the final truncated path.
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var guidCandidate = BuildSuffixSafeCandidate(directory, name, $"_{Guid.NewGuid():N}");
            if (!Path.Exists(guidCandidate))
            {
                return guidCandidate;
            }
        }

        throw new IOException(
            $"Cannot create a unique trash path under '{directory}' within the remaining path budget.");
    }

    /// <summary>
    ///     Builds a length-safe candidate path by truncating the baseName (not the suffix) so that the suffix is always preserved in the result.
    /// </summary>
    private static string BuildSuffixSafeCandidate(string directory, string baseName, string suffix)
    {
        var maxNameSize = GetMaxComponentSize(directory);

        var suffixSize = MeasureString(suffix);
        var availableForBase = maxNameSize - suffixSize;
        if (availableForBase <= 0)
        {
            // Suffix alone fills the budget - truncate suffix as last resort.
            var truncatedSuffix = TruncateToSize(suffix, Math.Max(0, maxNameSize));
            return Path.Join(directory, truncatedSuffix);
        }

        var truncatedBase = TruncateToSize(baseName, availableForBase);
        return Path.Join(directory, $"{truncatedBase}{suffix}");
    }

    /// <summary>
    ///     Ensures the path does not exceed the platform path limit. If it does, the file-name component is truncated (from the end, preserving the directory) until the full path fits.
    /// </summary>
    private static string EnsurePathLength(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);

        var maxNameSize = GetMaxComponentSize(directory);
        if (maxNameSize <= 0)
        {
            // Directory itself is already at or over the limit - nothing safe to do;
            // return the path as-is and let the caller's IOException handler log it.
            return path;
        }

        var pathSize = MeasureString(path);
        var nameSize = MeasureString(name);
        if (pathSize <= MaxPathLimit && nameSize <= MaxPathComponentLimit)
        {
            return path;
        }

        // Trash entries carry a fixed 16-char "yyyyMMdd-HHmmss_" prefix that PurgeExpiredTrash and the trash UI parse to recover the trashed-at time.
        const int trashPrefixLength = 15 + 1; // TimestampFormat length + '_' separator; all ASCII (1 byte/char)
        if (TryParseTrashTimestamp(name, out _))
        {
            if (maxNameSize <= trashPrefixLength)
            {
                // Cannot even keep the parseable prefix - refuse rather than emit an unpurgeable, date-less entry. Mirrors the fail-fast IOException the collision resolver throws when the directory budget is exhausted.
                throw new IOException(
                    $"Trash path directory is too deep to preserve the timestamp prefix for '{name}' "
                    + $"(budget {maxNameSize} <= required {trashPrefixLength}).");
            }

            var prefix = name[..trashPrefixLength];
            var originalName = name[trashPrefixLength..];
            var truncatedOriginal = TruncateToSize(originalName, maxNameSize - trashPrefixLength);
            return Path.Join(directory, prefix + truncatedOriginal);
        }

        var truncatedName = TruncateToSize(name, maxNameSize);
        return Path.Join(directory, truncatedName);
    }

    /// <summary>
    ///     Computes the maximum allowed size for a path component given its parent directory.
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
    ///     Computes the maximum allowed total path length for the current platform.
    ///     Windows caps at 259, macOS at 1023, and Linux at 4095.
    /// </summary>
    /// <returns>The maximum allowed path length for the current platform.</returns>
    private static int GetMaxPathLimit()
    {
        if (OperatingSystem.IsWindows())
        {
            return 259;
        }

        return OperatingSystem.IsMacOS() ? 1023 : 4095;
    }

    /// <summary>
    ///     Measures the size of a string in the platform-appropriate unit. On Unix (where filesystem limits are byte-based), returns the UTF-8 byte count.
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
    ///     Truncates a string so that its platform-measured size does not exceed maxSize. On Unix, truncates to fit within a UTF-8 byte budget without splitting multi-byte sequences.
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

        // Unix: limit is in UTF-8 bytes. Iterate through characters accumulating byte counts, stopping before we would exceed the budget.
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
                // Isolated surrogate or invalid - treat as replacement char (3 bytes in UTF-8)
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
    ///     Calculates the total size of all files in a directory tree using DirectoryInfo. This is a self-contained implementation for the trash module which operates outside the Jellyfin IFileSystem abstraction.
    /// </summary>
    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var dirInfo = new DirectoryInfo(path);

            // AttributesToSkip = ReparsePoint prunes directory symlinks/junctions DURING recursion, so the walk never descends INTO a linked tree.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };

            foreach (var fi in dirInfo.EnumerateFiles("*", options))
            {
                size += fi.Length;
            }
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
            _pluginLog.LogWarning(LogCategory, $"Path access check failed - invalid path: {path} ({ex.Message})", ex, logger);
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
            return CheckExistingDirectoryAccess(fullPath, logger);
        }

        // Path does not exist - walk up to the nearest existing parent and check if we can create there.
        return CheckCreatableAtParent(fullPath, logger);
    }

    /// <summary>
    ///     Checks read and write access on an existing directory and builds the corresponding
    ///     <see cref="TrashPathAccessResult"/>.
    /// </summary>
    private TrashPathAccessResult CheckExistingDirectoryAccess(string fullPath, ILogger logger)
    {
        var canRead = CanReadDirectory(fullPath);
        var canWrite = CanWriteDirectory(fullPath);

        if (!canRead || !canWrite)
        {
            string issue;
            if (!canRead && !canWrite)
            {
                issue = "read or write";
            }
            else
            {
                issue = !canRead ? "read" : "write";
            }

            var msg = $"Insufficient permissions: cannot {issue} path '{fullPath}'.";
            _pluginLog.LogWarning(LogCategory, msg, logger: logger);
            return new TrashPathAccessResult
            {
                Exists = true,
                CanRead = canRead,
                CanWrite = canWrite,
                ErrorMessage = msg
            };
        }

        _pluginLog.LogDebug(LogCategory, $"Path access check OK (exists, read+write): {fullPath}");
        return new TrashPathAccessResult { Exists = true, CanRead = true, CanWrite = true };
    }

    /// <summary>
    ///     Walks up to the nearest existing ancestor of a not-yet-created path and reports whether the path could be created there (i.e.
    /// </summary>
    private TrashPathAccessResult CheckCreatableAtParent(string fullPath, ILogger logger)
    {
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
                    _pluginLog.LogWarning(LogCategory, msg, logger: logger);
                    return new TrashPathAccessResult
                    {
                        Exists = false,
                        CanRead = true,
                        CanWrite = false,
                        ErrorMessage = msg
                    };
                }

                _pluginLog.LogDebug(LogCategory, $"Path access check OK (not yet created, parent writable): {fullPath}");
                return new TrashPathAccessResult { Exists = false, CanRead = true, CanWrite = true };
            }
        }

        // No existing ancestor found (should not happen on valid filesystems)
        var fallbackMsg = $"Cannot verify access for '{fullPath}': no existing parent directory found.";
        _pluginLog.LogWarning(LogCategory, fallbackMsg, logger: logger);
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
    ///     Attempts to create and immediately delete a temporary probe file inside the directory to verify write access.
    /// </summary>
    private static bool CanWriteDirectory(string directoryPath)
    {
        var probePath = Path.Join(directoryPath, $".jfh-access-probe-{Guid.NewGuid():N}");
        var created = false;
        try
        {
            using (File.Create(probePath))
            {
                // File created successfully - write access confirmed.
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

    // Filesystem seams (overridable for tests).

    /// <summary>
    ///     Determines whether <paramref name="path" /> is an existing reparse point
    ///     (symbolic link or junction).
    /// </summary>
    /// <param name="path">The directory path to inspect.</param>
    /// <returns><see langword="true" /> if the path is a reparse point; otherwise <see langword="false" />.</returns>
    internal virtual bool IsReparsePoint(string path) =>
        ReparsePointGuard.IsReparsePoint(path);

    /// <summary>
    ///     Deletes only the reparse-point link node at <paramref name="path" />, never following it
    ///     to (or deleting) its target.
    /// </summary>
    /// <param name="path">The reparse-point directory whose link node should be removed.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when <paramref name="path" /> is no longer a reparse point at deletion time
    ///     (concurrent replacement detected, fail closed to avoid deleting a real directory).
    /// </exception>
    internal virtual void DeleteReparsePointLinkNode(string path) =>
        ReparsePointGuard.DeleteLinkNode(path, InvokeDirectoryDelete);

    /// <summary>
    ///     Thin seam around Delete(). Zero-logic passthrough to a single BCL call with no branching of our own; the guard logic protecting it lives in DeleteLinkNode and is unit tested via this seam being overridden.
    /// </summary>
    /// <param name="info">The <see cref="DirectoryInfo" /> whose link node should be removed.</param>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal virtual void InvokeDirectoryDelete(DirectoryInfo info) => info.Delete();

    /// <summary>
    ///     Moves the directory at <paramref name="source" /> to <paramref name="destination" />.
    /// </summary>
    /// <param name="source">The source directory.</param>
    /// <param name="destination">The destination directory.</param>
    internal virtual void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);

    /// <summary>
    ///     Determines whether anything already occupies path, including a dangling symlink's link node.
    /// </summary>
    /// <param name="path">The path to test.</param>
    /// <returns>
    ///     <see langword="true" /> if a file, directory, or link node occupies the path; otherwise <see langword="false" />.
    /// </returns>
    internal virtual bool DestinationExists(string path) => Path.Exists(path);
}
