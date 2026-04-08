using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for media statistics.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinCleaner")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaStatisticsController : ControllerBase
{
    private readonly MediaStatisticsService _statisticsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStatisticsController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public MediaStatisticsController(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<MediaStatisticsService> logger)
    {
        _statisticsService = new MediaStatisticsService(libraryManager, fileSystem, logger);
    }

    /// <summary>
    /// Gets media statistics for all libraries.
    /// </summary>
    /// <returns>The media statistics.</returns>
    [HttpGet("Statistics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MediaStatisticsResult> GetStatistics()
    {
        return Ok(_statisticsService.CalculateStatistics());
    }
}