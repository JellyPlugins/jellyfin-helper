using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
/// Provides access to cleanup-related plugin configuration.
/// Applies configuration rules like library filtering, orphan age checking,
/// trash/delete resolution, and task mode queries.
/// </summary>
public interface ICleanupConfigHelper
{
    /// <summary>
    /// Gets the current plugin configuration, applying migration if needed.
    /// </summary>
    /// <returns>The plugin configuration.</returns>
    PluginConfiguration GetConfig();

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Trickplay Folder Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    TaskMode GetTrickplayTaskMode();

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Empty Media Folder Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    TaskMode GetEmptyMediaFolderTaskMode();

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Orphaned Subtitle Cleaner.
    /// </summary>
    /// <returns>The configured task mode.</returns>
    TaskMode GetOrphanedSubtitleTaskMode();

    /// <summary>
    /// Gets the <see cref="TaskMode"/> for the Link Repair task (.strm files and symlinks).
    /// </summary>
    /// <returns>The configured task mode.</returns>
    TaskMode GetLinkRepairTaskMode();

    /// <summary>
    /// Determines whether the Trickplay Folder Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    bool IsDryRunTrickplay();

    /// <summary>
    /// Determines whether the Empty Media Folder Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    bool IsDryRunEmptyMediaFolders();

    /// <summary>
    /// Determines whether the Orphaned Subtitle Cleaner should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    bool IsDryRunOrphanedSubtitles();

    /// <summary>
    /// Determines whether the Link Repair task should run in dry-run mode.
    /// </summary>
    /// <returns>True if the operation should be a dry run.</returns>
    bool IsDryRunLinkRepair();

    /// <summary>
    /// Gets the filtered library locations based on the allow list/exclude list configuration.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>A filtered, deduplicated list of library root paths.</returns>
    IReadOnlyList<string> GetFilteredLibraryLocations(ILibraryManager libraryManager);

    /// <summary>
    /// Checks whether a directory is old enough to be considered an orphan based on the configured minimum age.
    /// </summary>
    /// <param name="directoryPath">The path to the directory.</param>
    /// <returns>True if the directory is old enough (or age check is disabled), false if it's too new.</returns>
    bool IsOldEnoughForDeletion(string directoryPath);

    /// <summary>
    /// Checks whether a file is old enough to be considered an orphan based on the configured minimum age.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <returns>True if the file is old enough (or age check is disabled), false if it's too new.</returns>
    bool IsFileOldEnoughForDeletion(string filePath);

    /// <summary>
    /// Gets the resolved trash folder path for a given library root.
    /// If the configured path is relative, it is resolved relative to the library root.
    /// </summary>
    /// <param name="libraryRootPath">The library root path.</param>
    /// <returns>The full trash folder path.</returns>
    string GetTrashPath(string libraryRootPath);
}