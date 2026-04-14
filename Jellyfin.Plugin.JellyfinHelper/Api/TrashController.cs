using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for trash management.
/// Handles trash summary, listing, and folder deletion.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Trash")]
[Produces(MediaTypeNames.Application.Json)]
public class TrashController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TrashController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrashController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    public TrashController(
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<TrashController> logger)
    {
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    /// Gets a summary of all trash folders across libraries.
    /// </summary>
    /// <returns>The trash summary.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTrashSummary()
    {
        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);
        long totalSize = 0;
        var totalItems = 0;

        // Deduplicate trash paths so absolute paths are not counted once per library
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trashPath in libraryFolders.Select(CleanupConfigHelper.GetTrashPath))
        {
            if (!seenPaths.Add(trashPath))
            {
                continue;
            }

            var (size, count) = TrashService.GetTrashSummary(trashPath);
            totalSize += size;
            totalItems += count;
        }

        return Ok(new
        {
            TotalSize = totalSize,
            TotalItems = totalItems,
        });
    }

    /// <summary>
    /// Gets the list of existing trash folder paths on disk.
    /// Used by the UI to show which folders would be affected when disabling trash.
    /// For a relative trash path (default), returns one folder per library.
    /// For an absolute trash path, returns at most one folder.
    /// </summary>
    /// <returns>An object containing the list of existing trash folder paths.</returns>
    [HttpGet("Folders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTrashFolders()
    {
        var config = CleanupConfigHelper.GetConfig();
        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);
        var existingPaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathRooted(config.TrashFolderPath))
        {
            // Absolute path: only one trash folder
            if (Directory.Exists(config.TrashFolderPath))
            {
                existingPaths.Add(config.TrashFolderPath);
            }
        }
        else
        {
            // Relative path: one trash folder per library
            existingPaths.AddRange(libraryFolders.Select(CleanupConfigHelper.GetTrashPath).Where(Directory.Exists));
        }

        return Ok(new
        {
            Paths = existingPaths,
            IsAbsolute = !string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathRooted(config.TrashFolderPath),
        });
    }

    /// <summary>
    /// Deletes all existing trash folders from disc.
    /// Called when the user disables trash and chooses to delete the folders.
    /// </summary>
    /// <returns>A result indicating how many folders were deleted.</returns>
    [HttpDelete("Folders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteTrashFolders()
    {
        var config = CleanupConfigHelper.GetConfig();
        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);
        var deleted = new List<string>();
        var failed = new List<string>();

        var pathsToDelete = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathRooted(config.TrashFolderPath))
        {
            var fullPath = Path.GetFullPath(config.TrashFolderPath);
            if (!IsPathSafeForDeletion(fullPath, libraryFolders))
            {
                _pluginLog.LogWarning("API", $"Refusing to delete unsafe trash path: {fullPath}", logger: _logger);
                return BadRequest(new { Error = "Configured trash path is unsafe for deletion (filesystem root or library root)." });
            }

            if (Directory.Exists(fullPath))
            {
                pathsToDelete.Add(fullPath);
            }
        }
        else
        {
            foreach (var folder in libraryFolders)
            {
                var trashPath = Path.GetFullPath(CleanupConfigHelper.GetTrashPath(folder));
                var libraryRoot = Path.GetFullPath(folder);
                if (!trashPath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _pluginLog.LogWarning("API", $"Refusing to delete trash path {trashPath}: it escapes library root {libraryRoot}.", logger: _logger);
                    continue;
                }

                if (Directory.Exists(trashPath))
                {
                    pathsToDelete.Add(trashPath);
                }
            }
        }

        foreach (var path in pathsToDelete)
        {
            try
            {
                Directory.Delete(path, true);
                deleted.Add(path);
                _pluginLog.LogInfo("API", $"Deleted trash folder: {path}", _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(path);
                _pluginLog.LogError("API", $"Failed to delete trash folder: {path}", ex, _logger);
            }
        }

        return Ok(new
        {
            Deleted = deleted,
            Failed = failed,
        });
    }

    /// <summary>
    /// Gets the detailed contents of all trash folders across libraries.
    /// Each item includes its original name, size, trashed date, and expected purge date.
    /// </summary>
    /// <returns>The trash contents grouped by library.</returns>
    [HttpGet("Contents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTrashContents()
    {
        var config = CleanupConfigHelper.GetConfig();
        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);
        var libraries = new List<object>();

        var seenTrashPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in libraryFolders)
        {
            var trashPath = CleanupConfigHelper.GetTrashPath(folder);
            if (!seenTrashPaths.Add(Path.GetFullPath(trashPath)))
            {
                continue;
            }

            var items = TrashService.GetTrashContents(trashPath, config.TrashRetentionDays);

            if (items.Count > 0)
            {
                libraries.Add(new
                {
                    LibraryPath = folder,
                    LibraryName = Path.GetFileName(folder),
                    Items = items,
                });
            }
        }

        return Ok(new
        {
            config.UseTrash,
            RetentionDays = config.TrashRetentionDays,
            Libraries = libraries,
        });
    }

    // === Private helpers ===

    /// <summary>
    /// Validates that a path is safe for recursive deletion.
    /// Rejects filesystem roots and paths that match or contain library root folders.
    /// </summary>
    private static bool IsPathSafeForDeletion(string fullPath, IReadOnlyList<string> libraryFolders)
    {
        // Reject filesystem roots (e.g., "/", "C:\")
        var root = Path.GetPathRoot(fullPath);
        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reject if the path equals any library root
        foreach (var folder in libraryFolders)
        {
            var libraryRoot = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(candidate, libraryRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Reject if a library root is inside the trash path (would delete library contents)
            if (libraryRoot.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
