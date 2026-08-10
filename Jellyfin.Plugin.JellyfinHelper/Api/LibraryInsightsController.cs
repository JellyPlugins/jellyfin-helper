using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for library insights (largest directories, recently added/changed media).
///     Results are cached in-memory to avoid repeated filesystem scans on page refresh.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/LibraryInsights")]
[Produces(MediaTypeNames.Application.Json)]
public class LibraryInsightsController : ControllerBase
{
    internal const string InsightsCacheKey = "JellyfinHelper_LibraryInsights";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim CacheFillLock = new(1, 1);

    private readonly IMemoryCache _cache;
    private readonly ILibraryInsightsService _insightsService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LibraryInsightsController" /> class.
    /// </summary>
    /// <param name="cache">The memory cache.</param>
    /// <param name="insightsService">The library insights service.</param>
    public LibraryInsightsController(IMemoryCache cache, ILibraryInsightsService insightsService)
    {
        _cache = cache;
        _insightsService = insightsService;
    }

    /// <summary>
    ///     Gets library insights including the largest media directories and recently added/changed items.
    ///     Results are cached for 15 minutes to avoid repeated filesystem scans.
    ///     Uses double-check locking to prevent concurrent cache misses from triggering
    ///     duplicate expensive filesystem scans.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The library insights result.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<LibraryInsightsResult>> GetInsightsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(InsightsCacheKey, out LibraryInsightsResult? cached) && cached != null)
        {
            return Ok(cached);
        }

        var acquired = false;
        try
        {
            await CacheFillLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            // Double-check: another thread may have populated the cache while we waited
            if (_cache.TryGetValue(InsightsCacheKey, out cached) && cached != null)
            {
                return Ok(cached);
            }

            var result = await _insightsService.ComputeInsightsAsync(cancellationToken).ConfigureAwait(false);
            _cache.Set(InsightsCacheKey, result, CacheDuration);
            return Ok(result);
        }
        finally
        {
            if (acquired)
            {
                CacheFillLock.Release();
            }
        }
    }
}