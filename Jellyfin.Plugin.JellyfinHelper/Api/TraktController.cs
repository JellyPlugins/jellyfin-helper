using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Admin-only Trakt configuration endpoints. The connection test validates the shared OAuth application's
///     client id; per-user linking happens on the Discovery page via the device flow, not here.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Trakt")]
[Produces(MediaTypeNames.Application.Json)]
public class TraktController : ControllerBase
{
    private readonly ITraktAuthService _authService;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktController> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktController" /> class.
    /// </summary>
    /// <param name="authService">The Trakt auth service used to validate the client id.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger. Never receives the client id value.</param>
    public TraktController(
        ITraktAuthService authService,
        IPluginLogService pluginLog,
        ILogger<TraktController> logger)
    {
        _authService = authService;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Validates the Trakt client id by issuing the client-id-only trending request.
    /// </summary>
    /// <param name="request">The connection test request carrying the client id.</param>
    /// <returns>Connection test result.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TestConnection([FromBody] TraktTestRequest request)
    {
        // The client id is not a masked secret (it is returned to the admin as-is), so there is no sentinel to
        // resolve here - the request carries the real value typed into the form.
        if (request is null || string.IsNullOrWhiteSpace(request.ClientId))
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A Trakt Client ID is required." });
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var (success, message) = await _authService.TestClientIdAsync(request.ClientId.Trim(), cts.Token)
            .ConfigureAwait(false);

        if (success)
        {
            _pluginLog.LogInfo("API", "Trakt client id connection test OK.", _logger);
            return Ok(new ConnectionTestResponse { Success = true, Message = message });
        }

        return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = message });
    }
}
