using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for cleanup statistics.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/CleanupStatistics")]
[Produces(MediaTypeNames.Application.Json)]
public class CleanupStatisticsController : ControllerBase
{
    /// <summary>
    /// Gets the accumulated cleanup statistics (total bytes freed, items deleted, last cleanup time).
    /// </summary>
    /// <returns>The cleanup statistics.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCleanupStatistics()
    {
        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = CleanupTrackingService.GetStatistics();
        return Ok(new
        {
            TotalBytesFreed = totalBytesFreed,
            TotalItemsDeleted = totalItemsDeleted,
            LastCleanupTimestamp = lastCleanupTimestamp,
        });
    }
}
