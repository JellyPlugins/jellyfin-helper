using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services;
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
    [ProducesResponseType(typeof(TrashSizeResponse), StatusCodes.Status200OK)]
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

        return Ok(new TrashSizeResponse { TotalSize = totalSize, TotalItems = totalItems });
    }

    /// <summary>
    /// Gets the list of existing trash folder paths on disk.
    /// Used by the UI to show which folders would be affected when disabling trash.
    /// For a relative trash path (default), returns one folder per library.
    /// For an absolute trash path, returns at most one folder.
    /// </summary>
    /// <returns>An object containing the list of existing trash folder paths.</returns>
    [HttpGet("Folders")]
    [ProducesResponseType(typeof(TrashFoldersResponse), StatusCodes.Status200OK)]
    public ActionResult GetTrashFolders()
    {
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var existingPaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathFullyQualified(config.TrashFolderPath))
        {
            // Absolute path: only one trash folder
            var normalizedPath = Path.GetFullPath(config.TrashFolderPath);
            if (Directory.Exists(normalizedPath))
            {
                existingPaths.Add(normalizedPath);
            }
        }
        else
        {
            // Relative path: one trash folder per library
            existingPaths.AddRange(libraryFolders.Select(f => _configHelper.GetTrashPath(f)).Where(Directory.Exists));
        }

        return Ok(new TrashFoldersResponse
        {
            Paths = existingPaths,
            IsAbsolute = !string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathFullyQualified(config.TrashFolderPath),
        });
    }

    /// <summary>
    /// Deletes all existing trash folders from disc.
    /// Called when the user disables trash and chooses to delete the folders.
    /// </summary>
    /// <returns>A result indicating how many folders were deleted.</returns>
    [HttpDelete("Folders")]
    [ProducesResponseType(typeof(TrashDeleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DeleteTrashFolders()
    {
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var deleted = new List<string>();
        var failed = new List<string>();

        var pathsToDelete = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.TrashFolderPath) && Path.IsPathFullyQualified(config.TrashFolderPath))
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
                if (!trashPath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
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

        return Ok(new TrashDeleteResponse { Deleted = deleted.Count, Failed = failed.Count });
    }

    /// <summary>
    /// Gets the detailed contents of all trash folders across libraries.
    /// Each item includes its original name, size, trashed date, and expected purge date.
    /// </summary>
    /// <returns>The trash contents grouped by library.</returns>
    [HttpGet("Contents")]
    [ProducesResponseType(typeof(TrashConfigResponse), StatusCodes.Status200OK)]
    public ActionResult GetTrashContents()
    {
        var config = _configHelper.GetConfig();
        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var libraries = new List<TrashLibraryInfo>();

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
                libraries.Add(new TrashLibraryInfo
                {
                    LibraryPath = folder,
                    LibraryName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    Items = items,
                });
            }
        }

        return Ok(new TrashConfigResponse
        {
            UseTrash = config.UseTrash,
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
    [ProducesResponseType(typeof(TrashFoldersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetTrashFoldersForPath([FromBody] TrashPathQueryRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.TrashFolderPath))
        {
            return BadRequest(new { Error = "TrashFolderPath is required." });
        }

        var queryPath = request.TrashFolderPath.Trim();

        // Basic input sanity: cap length and reject obvious path-traversal sequences.
        // Full path-safety validation (library-root containment, filesystem-root rejection)
        // is enforced by GetExistingTrashFoldersForPath itself - this check is a
        // defence-in-depth guard only.
        if (HasTraversalSegment(queryPath) || queryPath.Length > 512)
        {
            return BadRequest(new { Error = "TrashFolderPath must not contain path-traversal sequences." });
        }

        var existingPaths = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, queryPath);

        return Ok(new TrashFoldersResponse
        {
            Paths = existingPaths,
            IsAbsolute = Path.IsPathFullyQualified(queryPath),
        });
    }

    /// <summary>
    /// Relocates trash contents from old trash folder(s) to new trash folder(s).
    /// Called when the user changes the trash path and chooses to move existing content.
    /// </summary>
    /// <param name="request">The request containing old and new trash paths.</param>
    /// <returns>A result indicating how many items were moved/failed.</returns>
    [HttpPost("Relocate")]
    [ProducesResponseType(typeof(TrashRelocateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RelocateTrash([FromBody] TrashRelocateRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.OldTrashPath) || string.IsNullOrWhiteSpace(request.NewTrashPath))
        {
            return BadRequest(new { Error = "Both OldTrashPath and NewTrashPath are required." });
        }

        // Sanitize user input via Path.GetFullPath to break CA3003 taint chain.
        // Both paths are further validated by IsPathSafeForDeletion / ResolveRelativeTrashPath.
        var oldPath = request.OldTrashPath.Trim();
        var newPath = request.NewTrashPath.Trim();

        if (HasTraversalSegment(oldPath) || HasTraversalSegment(newPath))
        {
            return BadRequest("Path traversal not allowed");
        }

        string resolvedOld;
        string resolvedNew;
        try
        {
            resolvedOld = Path.IsPathFullyQualified(oldPath) ? Path.GetFullPath(oldPath) : oldPath;
            resolvedNew = Path.IsPathFullyQualified(newPath) ? Path.GetFullPath(newPath) : newPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest(new { Error = "One or both trash paths are invalid." });
        }

        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var totalMoved = 0;
        var totalFailed = 0;

        if (Path.IsPathFullyQualified(oldPath) && Path.IsPathFullyQualified(newPath))
        {
            // Both absolute: single relocation
            if (!IsPathSafeForDeletion(resolvedOld, libraryFolders))
            {
                return BadRequest(new { Error = "Old trash path is unsafe for relocation." });
            }

            if (!IsPathSafeForDeletion(resolvedNew, libraryFolders))
            {
                return BadRequest(new { Error = "New trash path is unsafe for relocation." });
            }

            var (moved, failed) = _trashService.RelocateTrashContents(resolvedOld, resolvedNew, _logger);
            totalMoved += moved;
            totalFailed += failed;
        }
        else if (!Path.IsPathFullyQualified(oldPath) && !Path.IsPathFullyQualified(newPath))
        {
            // Both relative: relocate per library using existing trash folder lookup
            var existingOldFolders = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, oldPath);
            foreach (var folder in libraryFolders)
            {
                var perLibraryOld = ResolveRelativeTrashPath(folder, oldPath);
                var perLibraryNew = ResolveRelativeTrashPath(folder, newPath);

                if (perLibraryOld == null || perLibraryNew == null)
                {
                    continue;
                }

                // Only relocate if old trash folder actually exists on disk
                if (!existingOldFolders.Contains(perLibraryOld, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(perLibraryOld, perLibraryNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
            }
        }
        else if (Path.IsPathFullyQualified(oldPath) && !Path.IsPathFullyQualified(newPath))
        {
            // Old is absolute, new is relative: move the single absolute source into
            // ONE library's resolved new path.
            if (!IsPathSafeForDeletion(resolvedOld, libraryFolders))
            {
                return BadRequest(new { Error = "Old trash path is unsafe for relocation." });
            }

            // Choose the target library deterministically and meaningfully: prefer the
            // library that actually CONTAINS the absolute source, so e.g.
            // /media/Movies/.abs-old drains into /media/Movies/<newRelative>. Only when
            // no library contains the source (a trash dir on a separate volume) do we
            // fall back to the first library whose relative path resolves. Library
            // enumeration order (Jellyfin's GetVirtualFolders) is otherwise unsorted, so
            // relying on "the first one" alone was non-deterministic.
            var containingLibrary = FindContainingLibrary(resolvedOld, libraryFolders);
            var candidateLibraries = containingLibrary != null
                ? new[] { containingLibrary }.Concat(libraryFolders.Where(f => !string.Equals(f, containingLibrary, StringComparison.Ordinal)))
                : libraryFolders;

            foreach (var folder in candidateLibraries)
            {
                var perLibraryNew = ResolveRelativeTrashPath(folder, newPath);
                if (perLibraryNew == null)
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(resolvedOld, perLibraryNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
                break; // Only move once from the absolute source
            }
        }
        else
        {
            // Old is relative, new is absolute: merge all library trash folders into one
            if (!IsPathSafeForDeletion(resolvedNew, libraryFolders))
            {
                return BadRequest(new { Error = "New trash path is unsafe for relocation." });
            }

            var existingOldFoldersForMerge = _configHelper.GetExistingTrashFoldersForPath(_libraryManager, oldPath);
            foreach (var folder in libraryFolders)
            {
                var perLibraryOld = ResolveRelativeTrashPath(folder, oldPath);
                if (perLibraryOld == null || !existingOldFoldersForMerge.Contains(perLibraryOld, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (moved, failed) = _trashService.RelocateTrashContents(perLibraryOld, resolvedNew, _logger);
                totalMoved += moved;
                totalFailed += failed;
            }
        }

        _pluginLog.LogInfo("API", $"Trash relocation complete: {totalMoved} moved, {totalFailed} failed.", _logger);

        return Ok(new TrashRelocateResponse { Moved = totalMoved, Failed = totalFailed });
    }

    // === Private helpers ===

    /// <summary>
    /// Segment-aware path-traversal check shared by every body-taking trash endpoint
    /// (CheckAccess, FoldersForPath, Relocate) so they enforce identical input rules.
    /// Splits on both path separators and rejects only whole "." / ".." segments, so a
    /// legitimate directory name that merely contains ".." (e.g. "my..archive") is allowed
    /// while real traversal components are rejected. Downstream containment / sensitive-path
    /// checks remain as defence in depth.
    /// </summary>
    /// <param name="path">The caller-supplied path to screen.</param>
    /// <returns><c>true</c> if any segment is "." or ".."; otherwise <c>false</c>.</returns>
    private static bool HasTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(s => s is "." or "..");

    /// <summary>
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
    /// Validates that a path is safe for recursive deletion / relocation.
    /// A path is safe when EITHER it lies strictly inside a configured library root,
    /// OR it is a dedicated absolute directory that is not a filesystem root, not a
    /// known system/sensitive directory (e.g. <c>/config</c>, <c>/etc</c>,
    /// <c>C:\Windows</c>), and does not itself contain a library root. This keeps the
    /// intended "absolute trash folder outside the library" feature working while
    /// making it impossible for trash delete/relocate to touch Jellyfin's own config,
    /// OS directories, or a whole library.
    /// </summary>
    private static bool IsPathSafeForDeletion(string fullPath, IReadOnlyList<string> libraryFolders)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Reject filesystem roots (e.g., "/", "C:\") outright.
        var root = Path.GetPathRoot(fullPath);
        var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalizedPath) || string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return false;
        }

        var libraryRoots = libraryFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        // REJECT pass FIRST, over ALL libraries: never allow a library root itself, nor a
        // path that CONTAINS a library root (deleting/moving it would take the library
        // with it). This must run to completion before any allow, otherwise an early
        // "strictly inside library A" allow could short-circuit a later "contains library
        // B" reject for a nested library - approving a delete/relocate that wipes B.
        foreach (var libraryRoot in libraryRoots)
        {
            if (string.Equals(normalizedPath, libraryRoot, comparison)
                || libraryRoot.StartsWith(normalizedPath + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }
        }

        // ALLOW pass: strictly inside a library root → safe (a dedicated trash sub-folder).
        foreach (var libraryRoot in libraryRoots)
        {
            if (normalizedPath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        // Outside every library root: allow only a dedicated absolute directory that is
        // NOT a known system/sensitive location. This preserves the "trash folder on a
        // separate volume" admin setup while blocking /config, OS dirs, etc.
        return !IsSensitiveSystemPath(normalizedPath);
    }

    /// <summary>
    /// Returns the library root that strictly CONTAINS <paramref name="fullPath"/>, or
    /// <c>null</c> when the path lies outside every library (e.g. a trash directory on a
    /// separate volume). Used to relocate an absolute trash source into its own library's
    /// relative target deterministically, rather than into whichever library Jellyfin's
    /// unsorted enumeration happened to return first.
    /// </summary>
    /// <param name="fullPath">The already-resolved absolute path to locate.</param>
    /// <param name="libraryFolders">The known library root folders.</param>
    /// <returns>The containing library root (as provided in <paramref name="libraryFolders"/>), or null.</returns>
    private static string? FindContainingLibrary(string fullPath, IReadOnlyList<string> libraryFolders)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedPath = Path.GetFullPath(fullPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Pick the LONGEST (most specific) containing library root, not the first one
        // enumerated. With nested libraries (e.g. /media and /media/movies both
        // registered) a source could be strictly inside both; returning whichever the
        // unsorted GetVirtualFolders enumerated first would be non-deterministic - the
        // same order-dependence just fixed in IsPathSafeForDeletion. For a single or
        // sibling-only library layout this is a no-op (exactly one match).
        string? best = null;
        var bestLength = -1;
        foreach (var folder in libraryFolders)
        {
            var libraryRoot = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Strictly inside (not equal to) the library root.
            if (normalizedPath.StartsWith(libraryRoot + Path.DirectorySeparatorChar, comparison)
                && libraryRoot.Length > bestLength)
            {
                best = folder; // return the original string so the caller's de-dup by value holds
                bestLength = libraryRoot.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// True when the path is (or is inside) a well-known system / application directory
    /// that must never be a trash-deletion or relocation target - most importantly
    /// Jellyfin's own <c>/config</c>, plus common OS directories on Linux and Windows.
    /// Delegates to the shared <see cref="PathValidator.IsSensitiveSystemPath"/> (single
    /// source of truth; the OS-appropriate comparison is chosen there).
    /// </summary>
    private static bool IsSensitiveSystemPath(string normalizedPath)
        => PathValidator.IsSensitiveSystemPath(normalizedPath);

    /// <summary>
    /// Checks whether the Jellyfin process has read/write access to a given trash path.
    /// Used by the UI to proactively warn the user before attempting relocation or deletion
    /// on a path where permissions are insufficient.
    /// </summary>
    /// <param name="request">The request containing the trash folder path to check.</param>
    /// <returns>An object indicating access status and any error message.</returns>
    [HttpPost("CheckAccess")]
    [ProducesResponseType(typeof(TrashAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult CheckAccess([FromBody] TrashPathQueryRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { Error = "Request body is required." });
        }

        if (string.IsNullOrWhiteSpace(request.TrashFolderPath))
        {
            return BadRequest(new { Error = "TrashFolderPath is required." });
        }

        var queryPath = request.TrashFolderPath.Trim();

        // Segment-aware traversal check (shared with the other body-taking trash endpoints). A plain
        // Contains("..") would also reject legitimate names like "my..archive"; splitting on the path
        // separators and matching whole segments rejects only real "." / ".." traversal components.
        if (HasTraversalSegment(queryPath) || queryPath.Length > 512)
        {
            return BadRequest("Invalid path");
        }

        var libraryFolders = _configHelper.GetFilteredLibraryLocations(_libraryManager);
        var results = new List<TrashAccessEntry>();
        var allAccessible = true;

        if (Path.IsPathFullyQualified(queryPath))
        {
            // Absolute path: enforce the same library-root containment guard used by Delete
            // and Relocate. Without this check an admin could enumerate arbitrary host paths
            // or cause the Jellyfin process to create a probe file anywhere it can write.
            // IsPathFullyQualified (not IsPathRooted) is required: on Windows, IsPathRooted
            // returns true for root-relative paths like \Windows\System32, which would bypass
            // the containment check below because they are not fully qualified library roots.
            if (!IsPathSafeForDeletion(Path.GetFullPath(queryPath), libraryFolders))
            {
                return BadRequest("Path is outside of the permitted library trash directories.");
            }

            // Absolute path: check it directly
            var accessResult = _trashService.CheckPathAccess(queryPath, _logger);
            _pluginLog.LogInfo(
                "API",
                accessResult.HasFullAccess
                    ? $"Trash path access check passed: {queryPath}"
                    : $"Trash path access check FAILED: {queryPath} - {accessResult.ErrorMessage}",
                _logger);
            allAccessible &= accessResult.HasFullAccess;
            results.Add(new TrashAccessEntry
            {
                Path = queryPath,
                Exists = accessResult.Exists,
                CanRead = accessResult.CanRead,
                CanWrite = accessResult.CanWrite,
                HasFullAccess = accessResult.HasFullAccess,
                ErrorMessage = accessResult.ErrorMessage,
            });
        }
        else
        {
            // Relative path: resolve the submitted path against each library root and check access.
            // ResolveRelativeTrashPath enforces root-containment so the submitted path cannot
            // escape its library boundary via traversal sequences.
            foreach (var folder in libraryFolders)
            {
                var resolvedPath = ResolveRelativeTrashPath(folder, queryPath);
                if (resolvedPath == null)
                {
                    continue;
                }

                var accessResult = _trashService.CheckPathAccess(resolvedPath, _logger);
                _pluginLog.LogInfo(
                    "API",
                    accessResult.HasFullAccess
                        ? $"Trash path access check passed: {resolvedPath} (library: {folder})"
                        : $"Trash path access check FAILED: {resolvedPath} (library: {folder}) - {accessResult.ErrorMessage}",
                    _logger);
                allAccessible &= accessResult.HasFullAccess;
                results.Add(new TrashAccessEntry
                {
                    Path = resolvedPath,
                    LibraryRoot = folder,
                    Exists = accessResult.Exists,
                    CanRead = accessResult.CanRead,
                    CanWrite = accessResult.CanWrite,
                    HasFullAccess = accessResult.HasFullAccess,
                    ErrorMessage = accessResult.ErrorMessage,
                });
            }
        }

        var allOk = results.Count > 0 && allAccessible;
        return Ok(new TrashAccessResponse { AllAccessible = allOk, Results = results });
    }
}
