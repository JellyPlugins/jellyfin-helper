using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Manages a trash/recycle bin for deleted media items instead of permanent deletion.
/// Items are moved to a timestamped trash folder and can be permanently purged after a retention period.
/// </summary>
public static class TrashService
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Moves a directory to the trash folder instead of permanently deleting it.
    /// </summary>
    /// <param name="sourcePath">The full path of the directory to trash.</param>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The total size in bytes of the trashed directory, or 0 if the operation failed.</returns>
    public static long MoveToTrash(string sourcePath, string trashBasePath, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(sourcePath))
            {
                logger.LogWarning("Source path does not exist for trash: {Path}", sourcePath);
                return 0;
            }

            var dirName = Path.GetFileName(sourcePath);
            var timestamp = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{dirName}";
            var trashItemPath = Path.Combine(trashBasePath, trashItemName);

            // Ensure trash folder exists
            Directory.CreateDirectory(trashBasePath);

            // Calculate size before moving
            long size = CalculateDirectorySize(sourcePath);

            // Move to trash
            Directory.Move(sourcePath, trashItemPath);

            logger.LogInformation("Moved to trash: {Source} → {Destination} ({Size} bytes)", sourcePath, trashItemPath, size);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to move directory to trash: {Path}", sourcePath);
            return 0;
        }
    }

    /// <summary>
    /// Moves a single file to the trash folder instead of permanently deleting it.
    /// </summary>
    /// <param name="sourceFilePath">The full path of the file to trash.</param>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The size in bytes of the trashed file, or 0 if the operation failed.</returns>
    public static long MoveFileToTrash(string sourceFilePath, string trashBasePath, ILogger logger)
    {
        try
        {
            if (!File.Exists(sourceFilePath))
            {
                logger.LogWarning("Source file does not exist for trash: {Path}", sourceFilePath);
                return 0;
            }

            var fileName = Path.GetFileName(sourceFilePath);
            var timestamp = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{fileName}";
            var trashItemPath = Path.Combine(trashBasePath, trashItemName);

            // Ensure trash folder exists
            Directory.CreateDirectory(trashBasePath);

            // Get size before moving
            long size = new FileInfo(sourceFilePath).Length;

            // Move to trash
            File.Move(sourceFilePath, trashItemPath);

            logger.LogInformation("Moved file to trash: {Source} → {Destination} ({Size} bytes)", sourceFilePath, trashItemPath, size);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to move file to trash: {Path}", sourceFilePath);
            return 0;
        }
    }

    /// <summary>
    /// Purges items from the trash folder that are older than the specified retention period.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="retentionDays">The number of days to retain items in the trash.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The total bytes freed and items purged.</returns>
    public static (long BytesFreed, int ItemsPurged) PurgeExpiredTrash(string trashBasePath, int retentionDays, ILogger logger)
    {
        long totalBytesFreed = 0;
        int itemsPurged = 0;

        if (!Directory.Exists(trashBasePath))
        {
            return (0, 0);
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        try
        {
            // Purge old directories
            foreach (var dir in Directory.GetDirectories(trashBasePath))
            {
                var dirName = Path.GetFileName(dir);
                if (TryParseTrashTimestamp(dirName, out var timestamp) && timestamp < cutoff)
                {
                    try
                    {
                        long size = CalculateDirectorySize(dir);
                        Directory.Delete(dir, true);
                        totalBytesFreed += size;
                        itemsPurged++;
                        logger.LogInformation("Purged expired trash directory: {Path} ({Size} bytes, created {Timestamp})", dir, size, timestamp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError(ex, "Failed to purge trash directory: {Path}", dir);
                    }
                }
            }

            // Purge old files
            foreach (var file in Directory.GetFiles(trashBasePath))
            {
                var fileName = Path.GetFileName(file);
                if (TryParseTrashTimestamp(fileName, out var timestamp) && timestamp < cutoff)
                {
                    try
                    {
                        long size = new FileInfo(file).Length;
                        File.Delete(file);
                        totalBytesFreed += size;
                        itemsPurged++;
                        logger.LogInformation("Purged expired trash file: {Path} ({Size} bytes, created {Timestamp})", file, size, timestamp);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError(ex, "Failed to purge trash file: {Path}", file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to enumerate trash folder: {Path}", trashBasePath);
        }

        return (totalBytesFreed, itemsPurged);
    }

    /// <summary>
    /// Gets a summary of the current trash contents.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <returns>A tuple of total size in bytes and item count, or (0, 0) if the trash does not exist.</returns>
    public static (long TotalSize, int ItemCount) GetTrashSummary(string trashBasePath)
    {
        if (!Directory.Exists(trashBasePath))
        {
            return (0, 0);
        }

        long totalSize = 0;
        int itemCount = 0;

        try
        {
            var dirs = Directory.GetDirectories(trashBasePath);
            itemCount += dirs.Length;
            totalSize += dirs.Sum(d => CalculateDirectorySize(d));

            var files = Directory.GetFiles(trashBasePath);
            itemCount += files.Length;
            totalSize += files.Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Access errors are expected for inaccessible trash directories
        }

        return (totalSize, itemCount);
    }

    /// <summary>
    /// Gets detailed contents of the trash folder, including item name, size, trashed date, and purge date.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="retentionDays">The configured retention days to calculate purge dates.</param>
    /// <returns>A list of trash item details.</returns>
    public static IReadOnlyList<TrashItemInfo> GetTrashContents(string trashBasePath, int retentionDays)
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
                long size = CalculateDirectorySize(dir);

                DateTime? trashedAt = null;
                DateTime? purgesAt = null;
                if (TryParseTrashTimestamp(dirName, out var timestamp))
                {
                    trashedAt = timestamp;
                    purgesAt = timestamp.AddDays(retentionDays);
                }

                items.Add(new TrashItemInfo
                {
                    Name = originalName,
                    FullName = dirName,
                    Size = size,
                    IsDirectory = true,
                    TrashedAt = trashedAt,
                    PurgesAt = purgesAt,
                });
            }

            // Files
            foreach (var file in Directory.GetFiles(trashBasePath))
            {
                var fileName = Path.GetFileName(file);
                var originalName = ExtractOriginalName(fileName);
                long size = new FileInfo(file).Length;

                DateTime? trashedAt = null;
                DateTime? purgesAt = null;
                if (TryParseTrashTimestamp(fileName, out var timestamp))
                {
                    trashedAt = timestamp;
                    purgesAt = timestamp.AddDays(retentionDays);
                }

                items.Add(new TrashItemInfo
                {
                    Name = originalName,
                    FullName = fileName,
                    Size = size,
                    IsDirectory = false,
                    TrashedAt = trashedAt,
                    PurgesAt = purgesAt,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Access errors are expected for inaccessible trash directories
        }

        // Sort by trashed date descending (newest first)
        items.Sort((a, b) => (b.TrashedAt ?? DateTime.MinValue).CompareTo(a.TrashedAt ?? DateTime.MinValue));

        return items;
    }

    /// <summary>
    /// Extracts the original name from a timestamped trash item name.
    /// Format: "yyyyMMdd-HHmmss_originalname" → "originalname".
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
            return trashItemName[(TimestampFormat.Length + 1)..];
        }

        return trashItemName;
    }

    /// <summary>
    /// Tries to parse the timestamp prefix from a trash item name.
    /// Format: "yyyyMMdd-HHmmss_originalname".
    /// </summary>
    /// <param name="name">The trash item name including timestamp prefix.</param>
    /// <param name="timestamp">When this method returns, contains the parsed timestamp, or <see cref="DateTime.MinValue"/> if parsing failed.</param>
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
    /// Calculates the total size of all files in a directory tree using <see cref="DirectoryInfo"/>.
    /// This is a self-contained implementation for the trash module which operates outside the
    /// Jellyfin <c>IFileSystem</c> abstraction. For library paths, prefer
    /// <see cref="FileSystemHelper.CalculateDirectorySize"/> instead.
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
}