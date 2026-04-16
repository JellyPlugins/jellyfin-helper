using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for Seerr integration endpoints.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Seerr")]
[Produces(MediaTypeNames.Application.Json)]
public class SeerrController : ControllerBase
{
    private readonly ILogger<SeerrController> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ISeerrIntegrationService _seerrService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrController" /> class.
    /// </summary>
    /// <param name="seerrService">The Seerr integration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    public SeerrController(
        ISeerrIntegrationService seerrService,
        IPluginLogService pluginLog,
        ILogger<SeerrController> logger)
    {
        _seerrService = seerrService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Tests connectivity to a Seerr instance.
    /// </summary>
    /// <param name="request">The connection test request.</param>
    /// <returns>Connection test result.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestConnection([FromBody] SeerrTestRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { success = false, message = "URL and API Key are required." });
        }

        if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new { success = false, message = "URL and API Key are required." });
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (success, message) = await _seerrService.TestConnectionAsync(request.Url, request.ApiKey, cts.Token)
                .ConfigureAwait(false);

            if (success)
            {
                _pluginLog.LogInfo("API", $"Seerr connection test OK: {message}", _logger);
            }
            else
            {
                _pluginLog.LogWarning("API", $"Seerr connection test failed: {message}", logger: _logger);
            }

            return Ok(new { success, message });
        }
        catch (HttpRequestException ex)
        {
            _pluginLog.LogWarning("API", $"Seerr connection test failed: {ex.Message}", ex, _logger);
            return Ok(new { success = false, message = $"Connection failed: {ex.Message}" });
        }
        catch (TaskCanceledException)
        {
            _pluginLog.LogWarning("API", "Seerr connection test timed out after 10 seconds.", logger: _logger);
            return Ok(new { success = false, message = "Connection timed out after 10 seconds." });
        }
    }
}
