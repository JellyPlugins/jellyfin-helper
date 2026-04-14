using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for the library growth timeline.
/// Computes and caches historical growth data based on media file creation dates.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/GrowthTimeline")]
[Produces(MediaTypeNames.Application.Json)]
public class GrowthTimelineController : ControllerBase
{
    private readonly GrowthTimelineService _growthTimelineService;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrowthTimelineController"/> class.
    /// </summary>
    /// <param name="growthTimelineService">The growth timeline service.</param>
    public GrowthTimelineController(GrowthTimelineService growthTimelineService)
    {
        _growthTimelineService = growthTimelineService;
    }

    /// <summary>
    /// Gets the library growth timeline based on media file creation dates.
    /// Returns the cached timeline if available, otherwise computes it.
    /// The timeline uses automatic granularity (daily/weekly/monthly/quarterly/yearly)
    /// based on the age of the oldest media file.
    /// </summary>
    /// <param name="forceRefresh">Set to true to force recomputation instead of using cached data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The growth timeline with cumulative data points.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<GrowthTimelineResult>> GetGrowthTimelineAsync([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cached = await _growthTimelineService.LoadTimelineAsync(cancellationToken).ConfigureAwait(false);
            if (cached != null)
            {
                return Ok(cached);
            }
        }

        var result = await _growthTimelineService.ComputeTimelineAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
