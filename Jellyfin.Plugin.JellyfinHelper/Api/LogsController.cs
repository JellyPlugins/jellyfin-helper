using System;
using System.Collections.Generic;
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
///     API controller for the plugin logs.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Logs")]
[Produces(MediaTypeNames.Application.Json)]
public class LogsController : ControllerBase
{
    private static readonly HashSet<string> ValidLogLevels =
        new(StringComparer.OrdinalIgnoreCase) { "DEBUG", "INFO", "WARN", "ERROR" };

    private readonly ILogger<LogsController> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LogsController" /> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    public LogsController(IPluginLogService pluginLog, ILogger<LogsController> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Gets the plugin-specific log entries from the in-memory ring buffer.
    /// </summary>
    /// <param name="minLevel">Optional minimum log level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source component filter (partial match, max 200 chars).</param>
    /// <param name="limit">Maximum number of entries to return (default 500, max 2000).</param>
    /// <returns>A list of log entries, newest first.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetLogs(
        [FromQuery] string? minLevel = null,
        [FromQuery] string? source = null,
        [FromQuery] int limit = 500)
    {
        var validationError = ValidateLogQueryParams(minLevel, source);
        if (validationError != null)
        {
            return BadRequest(new { message = validationError });
        }

        limit = Math.Clamp(limit, 1, PluginLogService.MaxEntries);

        var entries = _pluginLog.GetEntries(minLevel, source, limit);
        return Ok(
            new
            {
                TotalBuffered = _pluginLog.GetCount(),
                Returned = entries.Count,
                Entries = entries
            });
    }

    /// <summary>
    ///     Downloads the plugin logs as a plain-text file.
    /// </summary>
    /// <param name="minLevel">Optional minimum log level filter (DEBUG, INFO, WARN, ERROR).</param>
    /// <param name="source">Optional source filter (partial match, max 200 chars).</param>
    /// <returns>A text file containing the log entries.</returns>
    [HttpGet("Download")]
    [Produces("text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult DownloadLogs([FromQuery] string? minLevel = null, [FromQuery] string? source = null)
    {
        var validationError = ValidateLogQueryParams(minLevel, source);
        if (validationError != null)
        {
            return BadRequest(new { message = validationError });
        }

        var text = _pluginLog.ExportAsText(minLevel, source);
        var bytes = Encoding.UTF8.GetBytes(text);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "text/plain", $"jellyfin-helper-logs-{timestamp}.txt");
    }

    /// <summary>
    ///     Clears all plugin log entries from the in-memory buffer.
    /// </summary>
    /// <returns>204 No Content.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearLogs()
    {
        _logger.LogDebug("Plugin log buffer cleared by admin");
        _pluginLog.Clear();
        return NoContent();
    }

    /// <summary>
    ///     Validates the shared and query parameters used by both GetLogs and DownloadLogs.
    /// </summary>
    /// <returns>An error message string when validation fails, or <c>null</c> when the parameters are valid.</returns>
    private static string? ValidateLogQueryParams(string? minLevel, string? source)
    {
        if (minLevel != null && !ValidLogLevels.Contains(minLevel))
        {
            return "Invalid minLevel. Allowed values: DEBUG, INFO, WARN, ERROR.";
        }

        if (source?.Length > 200)
        {
            return "source parameter too long.";
        }

        return null;
    }
}