using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
            return StatusCode(403, new RequestResult { Success = false, Message = "Discovery user access is disabled by the administrator." });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var currentUserId = userId.Value;
        var results = _cache.Load();
        var userResult = results.FirstOrDefault(r =>
            r.UserId.Equals(currentUserId));
        return Ok(userResult);
    }

    /// <summary>
    ///     Evaluates the current user's request permissions for a given service type (radarr/sonarr)
    ///     and media type (movie/tv). Returns only the quality profiles the user is authorized to use.
    ///     This is the primary endpoint the frontend calls before showing the request popup.
    /// </summary>
    /// <remarks>
    ///     <para>The logic follows the Overseerr/Jellyseerr permission model:</para>
    ///     <list type="bullet">
    ///         <item>If the user has no Seerr account → <c>CanRequest = false</c>.</item>
    ///         <item>If the user lacks REQUEST permission → <c>CanRequest = false</c>.</item>
    ///         <item>If the user has REQUEST_ADVANCED or MANAGE_REQUESTS → all profiles returned.</item>
    ///         <item>Normal users → only the server's default profile (no popup needed).</item>
    ///     </list>
    /// </remarks>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A permission result indicating what the user can do and which profiles are available.</returns>
    [HttpGet("RequestPermissions/{serviceType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRequestPermissionResult>> GetMyRequestPermissions(
        [RegularExpression("^(radarr|sonarr)$")] string serviceType,
        [FromQuery][RegularExpression("^(movie|tv)$")] string mediaType,
        CancellationToken cancellationToken)
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, new RequestResult { Success = false, Message = "Discovery user access is disabled by the administrator." });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(mediaType) || (mediaType is not ("movie" or "tv")))
        {
            return BadRequest(new RequestResult { Success = false, Message = "mediaType query parameter must be 'movie' or 'tv'." });
        }

        var result = await _discovery.GetUserRequestPermissionsAsync(
            userId.Value, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    ///     Returns the configured Radarr or Sonarr service info from Seerr,
    ///     including available quality profiles and root folders.
    ///     Available to any authenticated user when DiscoveryUserAccessEnabled is true.
    /// </summary>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of configured services with profiles.</returns>
    [HttpGet("Services/{serviceType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SeerrServiceInfo>>> GetMyServiceInfo(
        [RegularExpression("^(radarr|sonarr)$")] string serviceType,
        CancellationToken cancellationToken)
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, new RequestResult { Success = false, Message = "Discovery user access is disabled by the administrator." });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var services = await _discovery.GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return Ok(services);
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
    ///     Submits a media request to the configured Seerr instance on behalf of the current user.
    ///     Resolves the Jellyfin user ID to the corresponding Seerr user ID so the request
    ///     appears under the correct user in Seerr. If no Seerr account mapping exists,
    ///     the request is rejected with HTTP 403 — the user must be registered in Seerr first.
    ///     Supports optional quality profile, server, and root folder overrides when provided.
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

        if (dto == null)
        {
            return BadRequest(new RequestResult { Success = false, Message = "Request body is required." });
        }

        if (dto.TmdbId <= 0)
        {
            return BadRequest(new RequestResult { Success = false, Message = "Invalid TMDb ID." });
        }

        var mediaType = dto.MediaType?.Trim().ToLowerInvariant();
        if (mediaType is not ("movie" or "tv"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "mediaType must be 'movie' or 'tv'." });
        }

        // Block path traversal attempts, control characters, and excessive length in rootFolder
        if (!string.IsNullOrWhiteSpace(dto.RootFolder))
        {
            if (dto.RootFolder.Length > 512)
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path exceeds maximum length." });
            }

            if (dto.RootFolder.Contains("..", StringComparison.Ordinal) ||
                dto.RootFolder.Contains('~', StringComparison.Ordinal))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Invalid root folder path." });
            }

            if (dto.RootFolder.Any(c => char.IsControl(c)))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path contains invalid characters." });
            }
        }

        // Resolve the current Jellyfin user to their Seerr user ID
        // so the request appears under their name in Seerr (not as admin).
        // If no mapping exists, the request is rejected — users must be linked in Seerr.
        int? seerrUserId = null;
        var jellyfinUserId = GetCurrentUserId();
        if (jellyfinUserId.HasValue)
        {
            seerrUserId = await _discovery.ResolveSeerrUserIdAsync(
                jellyfinUserId.Value, cancellationToken).ConfigureAwait(false);
        }

        if (seerrUserId == null)
        {
            return StatusCode(403, new RequestResult
            {
                Success = false,
                Message = "Your Jellyfin account is not linked to a Seerr account. Please contact your server administrator."
            });
        }

        // Validate profile overrides against user permissions (server-side enforcement)
        if (dto.ServerId.HasValue || dto.ProfileId.HasValue)
        {
            var serviceType = mediaType == "movie" ? "radarr" : "sonarr";
            var permissions = await _discovery.GetUserRequestPermissionsAsync(
                jellyfinUserId!.Value, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

            if (!permissions.CanRequest)
            {
                return StatusCode(403, new RequestResult { Success = false, Message = "You do not have permission to submit requests." });
            }

            // If user has restricted profiles, validate the request matches an allowed one
            if (permissions.Profiles.Count > 0 && dto.ServerId.HasValue && dto.ProfileId.HasValue)
            {
                var isAllowed = permissions.Profiles.Any(profile =>
                    profile.ServerId == dto.ServerId.Value && profile.ProfileId == dto.ProfileId.Value);
                if (!isAllowed)
                {
                    return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to use this quality profile." });
                }
            }
        }

        // Pass through profile overrides if provided by the user (from quality profile popup)
        var (success, message) = await _discovery.SubmitRequestAsync(
            dto.TmdbId,
            mediaType,
            seerrUserId,
            dto.ServerId,
            dto.ProfileId,
            dto.RootFolder,
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