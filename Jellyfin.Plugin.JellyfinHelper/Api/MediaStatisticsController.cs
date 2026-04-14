using System;
using System.Net.Mime;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for media statistics.
/// Provides library scanning and latest cached results.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/MediaStatistics")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaStatisticsController : ControllerBase
{
    private const string StatsCacheKey = "JellyfinHelper_Statistics";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MinScanInterval = TimeSpan.FromSeconds(30);
    private static readonly Lock RateLimitLock = new();

    // Simple in-memory rate limiting (single-instance only; not effective in clustered/multi-pod deployments).
    // Static field + lock is intentional: ASP.NET creates a new controller instance per request,
    // so the rate-limit state must be shared across all instances via a static field.
    private static DateTime _lastScanTime = DateTime.MinValue;

    private readonly MediaStatisticsService _statisticsService;
    private readonly StatisticsCacheService _cacheService;
    private readonly IPluginLogService _pluginLog;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MediaStatisticsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStatisticsController"/> class.
    /// </summary>
    /// <param name="cache">The memory cache.</param>
    /// <param name="statisticsService">The media statistics service.</param>
    /// <param name="statisticsCacheService">The statistics cache service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    public MediaStatisticsController(
        IMemoryCache cache,
        MediaStatisticsService statisticsService,
        StatisticsCacheService statisticsCacheService,
        IPluginLogService pluginLog,
        ILogger<MediaStatisticsController> logger)
    {
        _statisticsService = statisticsService;
        _cacheService = statisticsCacheService;
        _pluginLog = pluginLog;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Gets media statistics for all libraries. Results are cached for 5 minutes.
    /// </summary>
    /// <returns>The media statistics.</returns>
    [HttpGet("ScanLibraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<MediaStatisticsResult> ScanLibraries()
    {
        lock (RateLimitLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastScanTime < MinScanInterval)
            {
                _pluginLog.LogWarning("API", "Rate limit exceeded for statistics scan", logger: _logger);
                return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Please wait before requesting another scan." });
            }
        }

        var result = _statisticsService.CalculateStatistics();

        // Set rate-limit timestamp AFTER the scan so a failed scan does not block retries for 30s
        lock (RateLimitLock)
        {
            SetLastScanTime(DateTime.UtcNow);
        }

        _cache.Set(StatsCacheKey, result, CacheDuration);
        _cacheService.SaveLatestResult(result);

        return Ok(result);
    }

    /// <summary>
    /// Gets the latest persisted statistics without triggering a new scan.
    /// Returns the most recent scan result that was saved to disk, surviving server restarts.
    /// </summary>
    /// <returns>The latest statistics, or 204 No Content if no scan has been performed yet.</returns>
    [HttpGet("Latest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<MediaStatisticsResult> GetLatestStatistics()
    {
        if (_cache.TryGetValue(StatsCacheKey, out MediaStatisticsResult? cached) && cached != null)
        {
            _pluginLog.LogDebug("API", "Returning cached statistics for /Latest", _logger);
            return Ok(cached);
        }

        var persisted = _cacheService.LoadLatestResult();
        if (persisted == null)
        {
            return NoContent();
        }

        _cache.Set(StatsCacheKey, persisted, CacheDuration);
        _pluginLog.LogDebug("API", "Loaded persisted statistics from disk for /Latest", _logger);
        return Ok(persisted);
    }

    private static void SetLastScanTime(DateTime value)
    {
        _lastScanTime = value;
    }
}
