using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
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
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> TestConnection([FromBody] SeerrTestRequest request)
    {
        if (request is null)
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "URL and API Key are required." });
        }

        if (string.IsNullOrWhiteSpace(request.Url) || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "URL and API Key are required." });
        }

        // Scheme guard only: reject non-HTTP(S) schemes. We deliberately do NOT block loopback/
        // private/link-local hosts: Seerr/Jellyseerr typically runs on the same host or LAN as
        // Jellyfin, so an internal-IP block would break the plugin's normal configuration. The
        // endpoint is admin-only, does not follow redirects, caps response size, and (below) returns
        // a generic failure message rather than reflecting upstream status — keeping the residual
        // internal-reachability-oracle risk low and accepted for a LAN-integration tool.
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A valid HTTP(S) URL is required." });
        }

        // Block well-known cloud metadata endpoints (AWS/Azure IMDS, GCP, Alibaba).
        // Internal LAN addresses are intentionally NOT blocked since Seerr typically runs
        // on the same host or LAN as Jellyfin.
        if (SsrfGuard.IsCloudMetadataHost(parsedUrl.Host))
        {
            _pluginLog.LogWarning("API", $"Blocked connection test to cloud metadata endpoint: {parsedUrl.Host}", logger: _logger);
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A valid HTTP(S) URL is required." });
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(timeout);
            var (success, message) = await _seerrService.TestConnectionAsync(request.Url, request.ApiKey, cts.Token)
                .ConfigureAwait(false);

            if (success)
            {
                _pluginLog.LogInfo("API", $"Connection test OK for Seerr: {message}", _logger);
                return Ok(new ConnectionTestResponse { Success = success, Message = message });
            }
            else
            {
                // Log the detailed upstream message server-side, but return a GENERIC message to the
                // client. Reflecting the raw upstream status/reason (e.g. "HTTP 401" vs "connection
                // refused" vs "no such host") turns this endpoint into an internal-reachability oracle.
                _pluginLog.LogWarning("API", $"Connection test failed for Seerr: {message}", logger: _logger);
                return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = "Connection failed. Please verify URL and API Key and try again." });
            }
        }
        catch (HttpRequestException ex)
        {
            _pluginLog.LogWarning("API", $"Connection test failed for Seerr: {ex.Message}", ex, _logger);
            return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = "Connection failed. Please verify URL and API Key and try again." });
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            _pluginLog.LogWarning("API", "Connection test timed out for Seerr after 10 seconds.", logger: _logger);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ConnectionTestResponse { Success = false, Message = "Connection timed out after 10 seconds." });
        }
    }
}
