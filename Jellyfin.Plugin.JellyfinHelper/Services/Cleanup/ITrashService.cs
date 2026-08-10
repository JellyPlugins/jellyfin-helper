using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Manages a trash/recycle bin for deleted media items instead of permanent deletion.
///     Items are moved to a timestamped trash folder and can be permanently purged after a retention period.
/// </summary>
public interface ITrashService
{
    /// <summary>
    ///     Moves a directory to the trash folder instead of permanently deleting it.
    /// </summary>
    /// <param name="sourcePath">The full path of the directory to trash.</param>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="utcNow">Optional fixed UTC timestamp for the trash entry. Defaults to <see cref="DateTime.UtcNow" />.</param>
    /// <returns>The total size in bytes of the trashed directory, or 0 if the operation failed.</returns>
    long MoveToTrash(string sourcePath, string trashBasePath, ILogger logger, DateTime? utcNow = null);

    /// <summary>
    ///     Moves a single file to the trash folder instead of permanently deleting it.
    /// </summary>
    /// <param name="sourceFilePath">The full path of the file to trash.</param>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="utcNow">Optional fixed UTC timestamp for the trash entry. Defaults to <see cref="DateTime.UtcNow" />.</param>
    /// <returns>The size in bytes of the trashed file, or 0 if the operation failed.</returns>
    long MoveFileToTrash(string sourceFilePath, string trashBasePath, ILogger logger, DateTime? utcNow = null);

    /// <summary>
    ///     Purges items from the trash folder that are older than the specified retention period.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="retentionDays">The number of days to retain items in the trash.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="utcNow">Optional fixed UTC timestamp for cutoff calculation. Defaults to <see cref="DateTime.UtcNow" />.</param>
    /// <returns>The total bytes freed and items purged.</returns>
    (long BytesFreed, int ItemsPurged) PurgeExpiredTrash(
        string trashBasePath,
        int retentionDays,
        ILogger logger,
        DateTime? utcNow = null);

    /// <summary>
    ///     Gets a summary of the current trash contents.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="logger">Optional logger for enumeration warnings.</param>
    /// <returns>A tuple of total size in bytes and item count, or (0, 0) if the trash does not exist.</returns>
    (long TotalSize, int ItemCount) GetTrashSummary(string trashBasePath, ILogger? logger = null);

    /// <summary>
    ///     Gets detailed contents of the trash folder, including item name, size, trashed date, and purge date.
    /// </summary>
    /// <param name="trashBasePath">The base path of the trash folder.</param>
    /// <param name="retentionDays">The configured retention days to calculate purge dates.</param>
    /// <param name="logger">Optional logger for enumeration warnings.</param>
    /// <returns>A list of trash item details.</returns>
    IReadOnlyList<TrashItemInfo> GetTrashContents(string trashBasePath, int retentionDays, ILogger? logger = null);

    /// <summary>
    ///     Relocates all trash contents from an old trash folder to a new trash folder.
    ///     Moves all top-level entries (files and directories) preserving their timestamp-prefixed names.
    ///     Creates the destination folder if it does not exist. Removes the old folder if it becomes empty.
    /// </summary>
    /// <param name="oldTrashPath">The full path of the old trash folder.</param>
    /// <param name="newTrashPath">The full path of the new trash folder.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>A tuple of items successfully moved and items that failed to move.</returns>
    (int Moved, int Failed) RelocateTrashContents(string oldTrashPath, string newTrashPath, ILogger logger);

    /// <summary>
    ///     Checks whether the Jellyfin process has read and write access to a given path.
    ///     If the path does not exist, checks whether the nearest existing parent directory
    ///     is writable (i.e., the path could be created).
    /// </summary>
    /// <param name="path">The absolute path to check.</param>
    /// <param name="logger">The logger for diagnostics.</param>
    /// <returns>
    ///     A <see cref="TrashPathAccessResult"/> indicating whether the path is readable,
    ///     writable, whether it already exists, and an optional error message.
    /// </returns>
    TrashPathAccessResult CheckPathAccess(string path, ILogger logger);
}