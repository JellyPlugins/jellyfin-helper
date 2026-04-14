using System;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for the plugin logs.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Logs")]
[Produces(MediaTypeNames.Application.Json)]
public class LogsController : ControllerBase
{
    private readonly ILogger<MediaStatisticsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsController"/> class.
    /// </summary>
    /// <param name="logger">The controller logger.</param>
    public LogsController(ILogger<MediaStatisticsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the plugin-specific log entries from the in-memory ring buffer.
    /// </summary>
    /// <param name="minLevel">Optional minimum log level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source component filter (partial match).</param>
    /// <param name="limit">Maximum number of entries to return (default 500, max 2000).</param>
    /// <returns>A list of log entries, newest first.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] string? minLevel = null, [FromQuery] string? source = null, [FromQuery] int limit = 500)
    {
        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > PluginLogService.MaxEntries)
        {
            limit = PluginLogService.MaxEntries;
        }

        var entries = PluginLogService.GetEntries(minLevel, source, limit);
        return Ok(new
        {
            TotalBuffered = PluginLogService.GetCount(),
            Returned = entries.Count,
            Entries = entries,
        });
    }

    /// <summary>
    /// Downloads the plugin logs as a plain-text file.
    /// </summary>
    /// <param name="minLevel">Optional minimum log level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source filter (partial match).</param>
    /// <returns>A text file containing the log entries.</returns>
    [HttpGet("Download")]
    [Produces("text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DownloadLogs([FromQuery] string? minLevel = null, [FromQuery] string? source = null)
    {
        var text = PluginLogService.ExportAsText(minLevel, source);
        var bytes = Encoding.UTF8.GetBytes(text);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "text/plain", $"jellyfin-helper-logs-{timestamp}.txt");
    }

    /// <summary>
    /// Clears all plugin log entries from the in-memory buffer.
    /// </summary>
    /// <returns>A status result.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ClearLogs()
    {
        PluginLogService.Clear();
        PluginLogService.LogInfo("API", "Plugin log buffer cleared by admin", _logger);
        return Ok(new { message = "Logs cleared." });
    }
}
