using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for the library growth timeline.
///     Computes and caches historical growth data based on media file creation dates.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/GrowthTimeline")]
[Produces(MediaTypeNames.Application.Json)]
public class GrowthTimelineController : ControllerBase
{
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    // SemaphoreSlim(1,1) instead of a plain Lock so the async method does not block
    // a thread-pool thread while holding the rate-limit guard.
    private static readonly SemaphoreSlim RateLimitSemaphore = new(1, 1);
    private static DateTime _lastRefreshTime = DateTime.MinValue;

    private readonly IGrowthTimelineService _growthTimelineService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GrowthTimelineController" /> class.
    /// </summary>
    /// <param name="growthTimelineService">The growth timeline service.</param>
    public GrowthTimelineController(IGrowthTimelineService growthTimelineService)
    {
        _growthTimelineService = growthTimelineService;
    }

    /// <summary>
    ///     Gets the library growth timeline based on media file creation dates. Returns the cached timeline if available, otherwise computes it.
    /// </summary>
    /// <param name="forceRefresh">Set to true to force recomputation instead of using cached data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The growth timeline with cumulative data points.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GrowthTimelineResult>> GetGrowthTimelineAsync(
        [FromQuery] bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cached = await _growthTimelineService.LoadTimelineAsync(cancellationToken).ConfigureAwait(false);
            if (cached != null)
            {
                return Ok(cached);
            }
        }

        await RateLimitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastRefreshTime < MinRefreshInterval)
            {
                var retryAfter = (int)Math.Ceiling((MinRefreshInterval - (now - _lastRefreshTime)).TotalSeconds);
                if (Response != null)
                {
                    Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { message = "Please wait before requesting another timeline computation." });
            }

            var previousRefreshTime = _lastRefreshTime;
            SetLastRefreshTime(now);
            try
            {
                var result = await _growthTimelineService.ComputeTimelineAsync(cancellationToken).ConfigureAwait(false);
                return Ok(result);
            }
            catch
            {
                SetLastRefreshTime(previousRefreshTime);
                throw;
            }
        }
        finally
        {
            RateLimitSemaphore.Release();
        }
    }

    private static void SetLastRefreshTime(DateTime value)
    {
        _lastRefreshTime = value;
    }
}