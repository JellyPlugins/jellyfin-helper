using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TrashService" /> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    public TrashService(IPluginLogService pluginLog)
    {
        _pluginLog = pluginLog;
    }

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

            var dirName = Path.GetFileName(sourcePath);
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

            var fileName = Path.GetFileName(sourceFilePath);
            var timestamp = (utcNow ?? DateTime.UtcNow).ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var trashItemName = $"{timestamp}_{fileName}";
            var trashItemPath = Path.Join(trashBasePath, trashItemName);

            // Avoid collision if an item with the same name was already trashed in the same second
            trashItemPath = ResolveCollision(trashItemPath);

            // Ensure trash folder exists
            Directory.CreateDirectory(trashBasePath);

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
                    Directory.Delete(dir, true);
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
    public (long TotalSize, int ItemCount) GetTrashSummary(string trashBasePath)
    {
        if (!Directory.Exists(trashBasePath))
        {
            return (0, 0);
        }

        long totalSize = 0;
        var itemCount = 0;

        try
        {
            var dirs = Directory.GetDirectories(trashBasePath);
            itemCount += dirs.Length;
            totalSize += dirs.Sum(CalculateDirectorySize);

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

    /// <inheritdoc />
    public IReadOnlyList<TrashItemInfo> GetTrashContents(string trashBasePath, int retentionDays)
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
                    purgesAt = timestamp.AddDays(retentionDays);
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
                    purgesAt = timestamp.AddDays(retentionDays);
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
            // Access errors are expected for inaccessible trash directories
        }

        // Sort by trashed date descending (newest first)
        items.Sort((a, b) => (b.TrashedAt ?? DateTime.MinValue).CompareTo(a.TrashedAt ?? DateTime.MinValue));

        return items;
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
            return trashItemName[(TimestampFormat.Length + 1)..];
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
    /// </summary>
    /// <param name="desiredPath">The initially desired trash path.</param>
    /// <returns>A path that does not yet exist on disk.</returns>
    private static string ResolveCollision(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileName(desiredPath);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Join(directory, $"{name}_{i}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // Extremely unlikely fallback: append a GUID
        return Path.Join(directory, $"{name}_{Guid.NewGuid():N}");
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
}