using System;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     User-facing API controller for Seerr Discovery.
///     Does NOT require admin elevation — any authenticated Jellyfin user can access these endpoints
///     (gated by the <c>DiscoveryUserAccessEnabled</c> configuration toggle).
/// </summary>
[ApiController]
[Authorize]
[Route("JellyfinHelper/Discovery/My")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class UserDiscoveryController : ControllerBase
{
    private readonly DiscoveryCacheService _cache;
    private readonly ISeerrDiscoveryService _discovery;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="discovery">The discovery service.</param>
    public UserDiscoveryController(DiscoveryCacheService cache, ISeerrDiscoveryService discovery)
    {
        _cache = cache;
        _discovery = discovery;
    }

    /// <summary>
    ///     Returns the cached discovery recommendations for the currently authenticated user.
    ///     Available to any authenticated user when DiscoveryUserAccessEnabled is true.
    /// </summary>
    /// <returns>The discovery result for the current user, or null if not available.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<DiscoveryResult?> GetMyDiscoveryResults()
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, null);
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var results = _cache.Load();
        var userResult = results.FirstOrDefault(r =>
            r.UserId.Equals(userId.Value));
        return Ok(userResult);
    }

    /// <summary>
    ///     Serves the discovery sidebar JavaScript file as an embedded resource.
    ///     This endpoint is referenced by the script tag injected into Jellyfin's index.html.
    ///     No admin requirement — the script itself checks access via the API.
    /// </summary>
    /// <returns>The discovery-sidebar.js content.</returns>
    [HttpGet("script")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.JellyfinHelper.js.discovery-sidebar.js");

        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return new FileStreamResult(stream, "application/javascript");
    }

    /// <summary>
    ///     Serves the standalone discovery page HTML as an embedded resource.
    ///     This endpoint is used by the sidebar link as a fallback when Custom Tabs is not configured.
    ///     AllowAnonymous because the page itself checks authentication via JS/ApiClient.
    /// </summary>
    /// <returns>The discoveryPage.html content.</returns>
    [HttpGet("~/JellyfinHelper/discoveryPage")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDiscoveryPage()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.JellyfinHelper.PluginPages.discoveryPage.html");

        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return new FileStreamResult(stream, "text/html");
    }

    /// <summary>
    ///     Submits a media request to the configured Seerr instance on behalf of the current user.
    ///     Available to any authenticated user when DiscoveryUserAccessEnabled is true.
    /// </summary>
    /// <param name="dto">The request data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure.</returns>
    [HttpPost("Request")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<RequestResult>> SubmitMyRequest(
        [FromBody] DiscoveryRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, new RequestResult { Success = false, Message = "Discovery user access is disabled by the administrator." });
        }

        if (dto == null || dto.TmdbId <= 0)
        {
            return BadRequest(new RequestResult { Success = false, Message = "Invalid TMDb ID." });
        }

        if (dto.MediaType is not ("movie" or "tv"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "mediaType must be 'movie' or 'tv'." });
        }

        // User requests use server defaults (no profile/server/rootFolder override for safety)
        var (success, message) = await _discovery.SubmitRequestAsync(
            dto.TmdbId,
            dto.MediaType,
            null, // No Seerr user override — uses API key owner
            null, // No server override — uses Seerr defaults
            null, // No profile override — uses Seerr defaults
            null, // No root folder override — uses Seerr defaults
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            return StatusCode(502, new RequestResult { Success = false, Message = message });
        }

        // Mark item as requested in cache so it doesn't reappear on page refresh
        _cache.MarkAsRequested(dto.TmdbId);

        return Ok(new RequestResult { Success = true, Message = message });
    }

    /// <summary>
    ///     Checks whether the admin has enabled user-level discovery access in plugin settings.
    /// </summary>
    private static bool IsDiscoveryUserAccessEnabled()
    {
        return Plugin.Instance?.Configuration.DiscoveryUserAccessEnabled == true;
    }

    /// <summary>
    ///     Extracts the current user's Jellyfin ID from the authentication claims.
    /// </summary>
    private Guid? GetCurrentUserId()
    {
        var claim = User?.FindFirst("Jellyfin-UserId")
            ?? User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var userId))
        {
            return userId;
        }

        return null;
    }
}