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
    private readonly ICleanupConfigHelper _configHelper;
    private readonly ITrashService _trashService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrashController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="trashService">The trash service.</param>
    public TrashController(
        ILibraryManager libraryManager,
        IPluginLogService pluginLog,
        ILogger<TrashController> logger,
        ICleanupConfigHelper configHelper,
        ITrashService trashService)
    {
        _libraryManager = libraryManager;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
        _trashService = trashService;
    }

    /// <summary>
    /// Gets a summary of all trash folders across libraries.
    /// </summary>
    /// <returns>The trash summary.</returns>
    [HttpGet("Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTrashSummary()
    {
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        long totalSize = 0;
        var totalItems = 0;

        // Deduplicate trash paths so absolute paths are not counted once per library
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trashPath in libraryFolders.Select(f => _configHelper.GetTrashPath(f)))
        {
            if (!seenPaths.Add(trashPath))
            {
                continue;
            }

            var (size, count) = _trashService.GetTrashSummary(trashPath);
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
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
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
            existingPaths.AddRange(libraryFolders.Select(f => _configHelper.GetTrashPath(f)).Where(Directory.Exists));
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
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
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
                var trashPath = Path.GetFullPath(_configHelper.GetTrashPath(folder));
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
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var libraries = new List<object>();

        var seenTrashPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in libraryFolders)
        {
            var trashPath = _configHelper.GetTrashPath(folder);
            if (!seenTrashPaths.Add(Path.GetFullPath(trashPath)))
            {
                continue;
            }

            var items = _trashService.GetTrashContents(trashPath, config.TrashRetentionDays);

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

    /// <summary>
    /// Gets the list of existing trash folder paths for a specific (possibly non-current) trash path.
    /// Used by the UI to check whether trash content exists at the OLD path before a path change is saved.
    /// </summary>
    /// <param name="request">The request containing the trash folder path to query.</param>
    /// <returns>An object containing the list of existing trash folder paths.</returns>
    [HttpPost("FoldersForPath")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetTrashFoldersForPath([FromBody] TrashPathQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TrashFolderPath))
        {
            return BadRequest(new { Error = "TrashFolderPath is required." });
        }

        var queryPath = request.TrashFolderPath.Trim();
        var existingPaths = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, queryPath);

        return Ok(new
        {
            Paths = existingPaths,
            IsAbsolute = Path.IsPathRooted(queryPath),
        });
    }

    /// <summary>
    /// Relocates trash contents from old trash folder(s) to new trash folder(s).
    /// Called when the user changes the trash path and chooses to move existing content.
    /// </summary>
    /// <param name="request">The request containing old and new trash paths.</param>
    /// <returns>A result indicating how many items were moved/failed.</returns>
    [HttpPost("Relocate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RelocateTrash([FromBody] TrashRelocateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OldTrashPath) || string.IsNullOrWhiteSpace(request.NewTrashPath))
        {
            return BadRequest(new { Error = "Both OldTrashPath and NewTrashPath are required." });
        }

        // Sanitize user input via Path.GetFullPath to break CA3003 taint chain.
        // Both paths are further validated by IsPathSafeForDeletion / ResolveRelativeTrashPath.
        var oldPath = request.OldTrashPath.Trim();
        var newPath = request.NewTrashPath.Trim();

        string sanitizedOld;
        string sanitizedNew;
        try
        {
            sanitizedOld = Path.IsPathRooted(oldPath) ? Path.GetFullPath(oldPath) : oldPath;
            sanitizedNew = Path.IsPathRooted(newPath) ? Path.GetFullPath(newPath) : newPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest(new { Error = "One or both trash paths are invalid." });
        }

        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var totalMoved = 0;
        var totalFailed = 0;

        if (Path.IsPathRooted(oldPath) && Path.IsPathRooted(newPath))
        {
            // Both absolute: single relocation
            if (!IsPathSafeForDeletion(sanitizedOld, libraryFolders))
            {
                return BadRequest(new { Error = "Old trash path is unsafe for relocation." });
            }

            if (!IsPathSafeForDeletion(sanitizedNew, libraryFolders))
            {
                return BadRequest(new { Error = "New trash path is unsafe for relocation." });
            }

            var (moved, failed) = _trashService.RelocateTrashContents(sanitizedOld, sanitizedNew, _logger);
            totalMoved += moved;
            totalFailed += failed;
        }
        else if (!Path.IsPathRooted(oldPath) && !Path.IsPathRooted(newPath))
        {
            // Both relative: relocate per library using existing trash folder lookup
            var existingOldFolders = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, oldPath);
            foreach (var folder in libraryFolders)
            {
                var resolvedOld = ResolveRelativeTrashPath(folder, oldPath);
                var resolvedNew = ResolveRelativeTrashPath(folder, newPath);

                if (resolvedOld == null || resolvedNew == null)
                {
                    continue;
                }

                // Only relocate if old trash folder actually exists on disk
                if (!existingOldFolders.Contains(resolvedOld))
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(resolvedOld, resolvedNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
            }
        }
        else if (Path.IsPathRooted(oldPath) && !Path.IsPathRooted(newPath))
        {
            // Old is absolute, new is relative: move from single old to first library's new path
            if (!IsPathSafeForDeletion(sanitizedOld, libraryFolders))
            {
                return BadRequest(new { Error = "Old trash path is unsafe for relocation." });
            }

            // Distribute to each library's resolved new path (split is not practical;
            // move all to the first library that resolves successfully)
            foreach (var folder in libraryFolders)
            {
                var resolvedNew = ResolveRelativeTrashPath(folder, newPath);
                if (resolvedNew == null)
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(sanitizedOld, resolvedNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
                break; // Only move once from absolute source
            }
        }
        else
        {
            // Old is relative, new is absolute: merge all library trash folders into one
            if (!IsPathSafeForDeletion(sanitizedNew, libraryFolders))
            {
                return BadRequest(new { Error = "New trash path is unsafe for relocation." });
            }

            var existingOldFoldersForMerge = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, oldPath);
            foreach (var folder in libraryFolders)
            {
                var resolvedOld = ResolveRelativeTrashPath(folder, oldPath);
                if (resolvedOld == null || !existingOldFoldersForMerge.Contains(resolvedOld))
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(resolvedOld, sanitizedNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
            }
        }

        _pluginLog.LogInfo("API", $"Trash relocation complete: {totalMoved} moved, {totalFailed} failed.", _logger);

        return Ok(new
        {
            Moved = totalMoved,
            Failed = totalFailed,
        });
    }

    // === Private helpers ===

    /// <summary>
    /// Resolves a relative trash path against a library root folder.
    /// Returns null if the resolved path escapes the library root or is invalid.
    /// </summary>
    /// <param name="libraryRoot">The library root folder.</param>
    /// <param name="relativePath">The relative trash path to resolve.</param>
    /// <returns>The resolved absolute path, or null if invalid.</returns>
    private static string? ResolveRelativeTrashPath(string libraryRoot, string relativePath)
    {
        try
        {
            var resolved = Path.GetFullPath(Path.Join(libraryRoot, relativePath));
            var normalizedRoot = Path.GetFullPath(libraryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // Must stay within the library root
            if (!resolved.StartsWith(rootPrefix, comparison))
            {
                return null;
            }

            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

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
