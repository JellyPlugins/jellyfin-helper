using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for server-side folder browsing.
///     Used by the settings UI to provide a folder picker dialog for selecting
///     the trash folder path. Only accessible by admin users.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Configuration")]
[Produces(MediaTypeNames.Application.Json)]
public class FolderBrowserController : ControllerBase
{
    private readonly IFolderBrowserService _folderBrowser;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FolderBrowserController" /> class.
    /// </summary>
    /// <param name="folderBrowser">The folder browser service.</param>
    /// <param name="libraryManager">The Jellyfin library manager for listing library root paths.</param>
    public FolderBrowserController(
        IFolderBrowserService folderBrowser,
        ILibraryManager libraryManager)
    {
        _folderBrowser = folderBrowser;
        _libraryManager = libraryManager;
    }

    /// <summary>
    ///     Browses server-side directories for the folder picker UI.
    ///     When no path is provided, returns filesystem roots.
    ///     When a path is provided, returns its immediate subdirectories.
    ///     Only returns directories the server process can read.
    /// </summary>
    /// <param name="path">The parent path to list children of. Empty or null returns filesystem roots.</param>
    /// <returns>A browse result with the current path, parent path, and list of subdirectories.</returns>
    [HttpGet("BrowseFolders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BrowseFolders([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Ok(_folderBrowser.GetRoots());
        }

        return Ok(_folderBrowser.GetChildren(path));
    }

    /// <summary>
    ///     Gets the library root paths configured in Jellyfin.
    ///     Used as quick-navigation targets in the folder browser dialog.
    ///     Returns the physical filesystem paths of all configured library folders.
    /// </summary>
    /// <returns>A list of library root paths with their names.</returns>
    [HttpGet("LibraryPaths")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLibraryPaths()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var paths = virtualFolders
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .SelectMany(f => (f.Locations ?? []).Select(loc => new { name = f.Name, path = loc }))
            .Where(x => !string.IsNullOrWhiteSpace(x.path))
            .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { libraryPaths = paths });
    }
}