using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly ILogger<UserDiscoveryController> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="discovery">The discovery service.</param>
    /// <param name="feedbackStore">The discovery feedback store for training data collection.</param>
    /// <param name="logger">The logger instance.</param>
    public UserDiscoveryController(
        DiscoveryCacheService cache,
        ISeerrDiscoveryService discovery,
        IDiscoveryFeedbackStore feedbackStore,
        ILogger<UserDiscoveryController> logger)
    {
        _cache = cache;
        _discovery = discovery;
        _feedbackStore = feedbackStore;
        _logger = logger;
    }

    /// <summary>
    ///     Returns the cached discovery recommendations for the currently authenticated user.
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
        var userResult = results.FirstOrDefault(r => r.UserId.Equals(currentUserId));
        return Ok(userResult);
    }

    /// <summary>
    ///     Evaluates the current user's request permissions for a given service type and media type.
    /// </summary>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A permission result indicating what the user can do and which profiles are available.</returns>
    [HttpGet("RequestPermissions/{serviceType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRequestPermissionResult>> GetMyRequestPermissions(
        string serviceType,
        [FromQuery] string mediaType,
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

        var currentUserId = userId.Value;
        serviceType = serviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        mediaType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;

        if (serviceType is not ("radarr" or "sonarr"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "serviceType must be 'radarr' or 'sonarr'." });
        }

        if (mediaType is not ("movie" or "tv"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "mediaType must be 'movie' or 'tv'." });
        }

        var result = await _discovery.GetUserRequestPermissionsAsync(
            currentUserId, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>
    ///     Returns the configured Radarr or Sonarr service info from Seerr.
    /// </summary>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of configured services with profiles.</returns>
    [HttpGet("Services/{serviceType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SeerrServiceInfo>>> GetMyServiceInfo(
        string serviceType,
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

        serviceType = serviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (serviceType is not ("radarr" or "sonarr"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "serviceType must be 'radarr' or 'sonarr'." });
        }

        // Only expose service infrastructure to users who actually have request permission.
        // Prevents information disclosure of Radarr/Sonarr server names, paths, and profiles
        // to users without the Seerr REQUEST permission.
        var mediaType = serviceType == "radarr" ? "movie" : "tv";
        var permissions = await _discovery.GetUserRequestPermissionsAsync(
            userId.Value, mediaType, serviceType, cancellationToken).ConfigureAwait(false);
        if (!permissions.CanRequest)
        {
            return Ok(Array.Empty<SeerrServiceInfo>());
        }

        var services = await _discovery.GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return Ok(services);
    }

    /// <summary>
    ///     Serves the discovery sidebar JavaScript file as an embedded resource.
    /// </summary>
    /// <remarks>
    ///     AllowAnonymous is required because the script tag in index.html loads
    ///     before Jellyfin's authentication context is established. The script itself
    ///     uses authenticated API calls internally — no sensitive data is exposed here.
    /// </remarks>
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
            _logger.LogWarning("[Discovery] Embedded resource 'discovery-sidebar.js' could not be loaded. Verify the resource is configured in .csproj with the correct LogicalName");
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-cache";
        return new FileStreamResult(stream, "application/javascript");
    }

    /// <summary>
    ///     Submits a media request to the configured Seerr instance on behalf of the current user.
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

        if (!string.IsNullOrWhiteSpace(dto.RootFolder))
        {
            if (dto.RootFolder.Length > 512)
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path exceeds maximum length." });
            }

            if (dto.RootFolder.Contains("..", StringComparison.Ordinal) ||
                dto.RootFolder.TrimStart().StartsWith('~'))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Invalid root folder path." });
            }

            if (dto.RootFolder.Any(c => char.IsControl(c)))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path contains invalid characters." });
            }
        }

        var jellyfinUserId = GetCurrentUserId();
        if (!jellyfinUserId.HasValue)
        {
            return Unauthorized();
        }

        var currentJellyfinUserId = jellyfinUserId.Value;

        var seerrUserId = await _discovery.ResolveSeerrUserIdAsync(
            currentJellyfinUserId, cancellationToken).ConfigureAwait(false);

        if (seerrUserId == null)
        {
            return StatusCode(403, new RequestResult
            {
                Success = false,
                Message = "Your Jellyfin account is not linked to a Seerr account. Please contact your server administrator."
            });
        }

        var serviceType = mediaType == "movie" ? "radarr" : "sonarr";
        var permissions = await _discovery.GetUserRequestPermissionsAsync(
            currentJellyfinUserId, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

        if (!permissions.CanRequest)
        {
            return StatusCode(403, new RequestResult { Success = false, Message = "You do not have permission to submit requests." });
        }

        if (dto.ServerId.HasValue || dto.ProfileId.HasValue || !string.IsNullOrWhiteSpace(dto.RootFolder))
        {
            if (!dto.ServerId.HasValue || !dto.ProfileId.HasValue)
            {
                return BadRequest(new RequestResult { Success = false, Message = "Both ServerId and ProfileId must be specified together." });
            }

            var serverId = dto.ServerId.Value;
            var profileId = dto.ProfileId.Value;

            if (permissions.Profiles.Count == 0)
            {
                return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to override the default quality profile." });
            }

            var matchedProfile = permissions.Profiles.FirstOrDefault(profile =>
                profile.ServerId == serverId && profile.ProfileId == profileId);
            if (matchedProfile == null)
            {
                return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to use this quality profile." });
            }

            if (!string.IsNullOrWhiteSpace(dto.RootFolder) &&
                !string.Equals(dto.RootFolder, matchedProfile.RootFolder, StringComparison.Ordinal))
            {
                return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to use this root folder." });
            }
        }

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

        try
        {
            _cache.MarkAsRequested(dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort cache update.
        }

        try
        {
            _feedbackStore.RecordRequested(currentJellyfinUserId, dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort feedback recording.
        }

        return Ok(new RequestResult { Success = true, Message = message });
    }

    /// <summary>
    ///     Records that the current user has explicitly dismissed a discovery recommendation.
    /// </summary>
    /// <param name="dto">The dismiss request containing the TMDb ID and media type.</param>
    /// <returns>A result indicating success.</returns>
    [HttpPost("Dismiss")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<RequestResult> DismissItem([FromBody] DiscoveryDismissDto dto)
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

        try
        {
            _feedbackStore.RecordDismissed(currentUserId, dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort feedback recording.
        }

        // Remove from cached results so the item disappears immediately on page reload
        try
        {
            _cache.RemoveItem(dto.TmdbId, mediaType, currentUserId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort cache removal.
        }

        return Ok(new RequestResult { Success = true, Message = "Item dismissed." });
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
        if (claim != null && Guid.TryParse(claim.Value, out var parsedUserId))
        {
            return parsedUserId;
        }

        return null;
    }
}
