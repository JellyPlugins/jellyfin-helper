using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
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
    private readonly ICleanupConfigHelper _configHelper;
    private readonly ILogger<SeerrController> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ISeerrIntegrationService _seerrService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrController" /> class.
    /// </summary>
    /// <param name="seerrService">The Seerr integration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper, used to resolve the masked API-key sentinel.</param>
    public SeerrController(
        ISeerrIntegrationService seerrService,
        IPluginLogService pluginLog,
        ILogger<SeerrController> logger,
        ICleanupConfigHelper configHelper)
    {
        _seerrService = seerrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
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
        // a generic failure message rather than reflecting upstream status, keeping the residual
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

        // Resolve the masked-key sentinel to the real stored Seerr key BEFORE the live call. After a
        // reload the API-key input holds the fixed-length mask (the real key never leaves the server),
        // so testing the stored instance must probe Seerr with the REAL key to report true reachability
        // - not send the mask, which would always 401 and show a misleading failure. Any non-mask value
        // is a new key the admin is entering and is tested as-is. The stored key is only substituted
        // when the request URL matches the persisted SeerrUrl, so a mask against an unknown URL cannot
        // borrow an unrelated credential.
        var apiKey = request.ApiKey;
        if (ApiKeyMaskResolver.IsMask(apiKey))
        {
            var config = _configHelper.GetConfig();
            var urlMatches = string.Equals(
                config.SeerrUrl?.Trim(),
                request.Url.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (urlMatches && !string.IsNullOrWhiteSpace(config.SeerrApiKey))
            {
                apiKey = config.SeerrApiKey;
            }
            else
            {
                // Mask sent but no matching stored key. Do NOT forward the mask upstream; return the
                // same generic failure the live path uses so the client shows a truthful "not reachable".
                _pluginLog.LogWarning("API", "Seerr connection test received the masked key sentinel but no stored key matched the URL; cannot resolve a real key.", logger: _logger);
                return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = "Connection failed. Please verify URL and API Key and try again." });
            }
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(timeout);
            var (success, message) = await _seerrService.TestConnectionAsync(request.Url, apiKey, cts.Token)
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
