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
        if (userResult == null)
        {
            return Ok(null);
        }

        // Filter persisted pool: exclude dismissed + requested, serve only next N visible.
        // Normalize MediaType to lowercase for comparison because the feedback store
        // persists normalized lowercase values while cache data may retain original API casing.
        var excluded = BuildExcludedItemKeys(currentUserId);
        var visible = userResult.Recommendations
            .Where(r => !r.AlreadyRequested && !excluded.Contains((r.TmdbId, r.MediaType?.ToLowerInvariant() ?? string.Empty)))
            .Take(SeerrDiscoveryService.MaxVisiblePerUser)
            .ToList();

        return Ok(new DiscoveryResult
        {
            UserId = userResult.UserId,
            UserName = userResult.UserName,
            Recommendations = visible,
            GeneratedAt = userResult.GeneratedAt
        });
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

        // Return only the servers and profiles this user is permitted to use.
        // GetUserRequestPermissionsAsync already evaluates CanSelectQualityProfile —
        // normal users get only default profiles, advanced users get all profiles.
        // This prevents non-advanced users from enumerating the full Seerr topology.
        var services = await _discovery.GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);
        var filteredServices = FilterServicesByAllowedProfiles(services, permissions.Profiles);
        return Ok(filteredServices);
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
        return new FileStreamResult(stream, "text/javascript");
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

        // Normalize RootFolder: trim whitespace and coalesce whitespace-only to null.
        // This prevents whitespace-only strings from bypassing validation guards below
        // and being sent as meaningless overrides to the Seerr API.
        var rootFolder = string.IsNullOrWhiteSpace(dto.RootFolder) ? null : dto.RootFolder.Trim();

        if (rootFolder != null)
        {
            if (rootFolder.Length > 512)
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path exceeds maximum length." });
            }

            if (rootFolder.Contains("..", StringComparison.Ordinal) ||
                rootFolder.TrimStart().StartsWith('~'))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Invalid root folder path." });
            }

            if (rootFolder.Any(c => char.IsControl(c)))
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

        var serviceType = mediaType == "movie" ? "radarr" : "sonarr";
        var permissions = await _discovery.GetUserRequestPermissionsAsync(
            currentJellyfinUserId, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

        if (!permissions.CanRequest)
        {
            // GetUserRequestPermissionsAsync distinguishes "transient Seerr failure" from
            // "user not linked" via DeniedReason — propagate the specific message to the client.
            // Use 503 for transient upstream failures so the client knows to retry,
            // and 403 for genuine permission denials.
            var isTransient = permissions.DeniedReason != null
                && permissions.DeniedReason.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase);
            var statusCode = isTransient ? 503 : 403;
            return StatusCode(statusCode, new RequestResult
            {
                Success = false,
                Message = permissions.DeniedReason ?? "You do not have permission to submit requests."
            });
        }

        var seerrUserId = await _discovery.ResolveSeerrUserIdAsync(
            currentJellyfinUserId, cancellationToken).ConfigureAwait(false);

        if (seerrUserId == null)
        {
            // At this point GetUserRequestPermissionsAsync already confirmed the user exists
            // in Seerr (CanRequest=true). A null here indicates a transient cache/network issue
            // between the two calls — use 502 to signal a retriable upstream failure.
            return StatusCode(502, new RequestResult
            {
                Success = false,
                Message = "Could not verify your Seerr account. Please try again."
            });
        }

        if (dto.ServerId.HasValue || dto.ProfileId.HasValue || rootFolder != null)
        {
            if (!dto.ServerId.HasValue || !dto.ProfileId.HasValue)
            {
                return BadRequest(new RequestResult { Success = false, Message = "ServerId and ProfileId must be specified together." });
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

            // Validate root folder against the matched profile.
            // When the profile has no specific root folder (empty/null), accept both null and empty
            // from the client — the request will use Seerr's server default.
            // When the profile HAS a root folder, the client must provide an exact match.
            var profileHasRootFolder = !string.IsNullOrEmpty(matchedProfile.RootFolder);
            if (profileHasRootFolder)
            {
                if (rootFolder == null || !string.Equals(rootFolder, matchedProfile.RootFolder, StringComparison.Ordinal))
                {
                    return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to use this root folder." });
                }
            }
            else if (rootFolder != null)
            {
                // Profile has no root folder constraint — reject if client sends a non-empty
                // root folder (trying to override to an arbitrary path when none is configured).
                return StatusCode(403, new RequestResult { Success = false, Message = "You are not authorized to use this root folder." });
            }
        }

        var (success, message) = await _discovery.SubmitRequestAsync(
            dto.TmdbId,
            mediaType,
            seerrUserId,
            dto.ServerId,
            dto.ProfileId,
            rootFolder,
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
            // Best-effort cache update — log but do not fail the request.
            _logger.LogWarning(ex, "[Discovery] Failed to mark item {TmdbId}/{MediaType} as requested in cache for user {UserId}", dto.TmdbId, mediaType, currentJellyfinUserId);
        }

        try
        {
            _feedbackStore.RecordRequested(currentJellyfinUserId, dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "[Discovery] Failed to record requested item {TmdbId}/{MediaType} for user {UserId}", dto.TmdbId, mediaType, currentJellyfinUserId);
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
            _logger.LogWarning(ex, "[Discovery] Failed to record dismissed item {TmdbId}/{MediaType} for user {UserId}", dto.TmdbId, mediaType, currentUserId);
        }

        return Ok(new RequestResult { Success = true, Message = "Item dismissed." });
    }

    /// <summary>
    ///     Filters the full service info list to only include servers and profiles
    ///     that appear in the user's allowed profile list.
    ///     This ensures non-advanced users cannot enumerate the full Seerr topology
    ///     (servers, profiles, root folders) beyond what they are permitted to use.
    /// </summary>
    /// <param name="services">The complete service info from Seerr.</param>
    /// <param name="allowedProfiles">The user's permitted profiles (pre-evaluated by permission logic).</param>
    /// <returns>A filtered list containing only the servers and profiles the user may access.</returns>
    private static List<SeerrServiceInfo> FilterServicesByAllowedProfiles(
        IReadOnlyList<SeerrServiceInfo> services,
        IReadOnlyList<AllowedQualityProfile> allowedProfiles)
    {
        // If the permission evaluation returned no profiles, the user should submit
        // with server defaults — return an empty list so the frontend skips the popup.
        if (allowedProfiles.Count == 0)
        {
            return [];
        }

        // Build a lookup of allowed (ServerId, ProfileId) pairs for O(1) membership checks.
        var allowedKeys = new HashSet<(int ServerId, int ProfileId)>(
            allowedProfiles.Select(p => (p.ServerId, p.ProfileId)));

        var result = new List<SeerrServiceInfo>();
        foreach (var service in services)
        {
            // Only include profiles that are in the user's allowed set for this server.
            var filteredProfiles = service.Profiles
                .Where(p => allowedKeys.Contains((service.Id, p.Id)))
                .ToList();

            if (filteredProfiles.Count == 0)
            {
                continue;
            }

            // Return a projection with only the allowed profiles — do not expose the full list.
            // RootFolders are also filtered to only include paths that correspond to allowed profiles
            // for this server, preventing information disclosure of unrelated server paths.
            var allowedRootPaths = new HashSet<string>(
                allowedProfiles
                    .Where(p => p.ServerId == service.Id && !string.IsNullOrEmpty(p.RootFolder))
                    .Select(p => p.RootFolder),
                StringComparer.Ordinal);

            var filteredRootFolders = allowedRootPaths.Count > 0
                ? new System.Collections.ObjectModel.Collection<SeerrRootFolder>(
                    service.RootFolders.Where(rf => allowedRootPaths.Contains(rf.Path)).ToList())
                : new System.Collections.ObjectModel.Collection<SeerrRootFolder>();

            result.Add(new SeerrServiceInfo
            {
                Id = service.Id,
                Name = service.Name,
                IsDefault = service.IsDefault,
                Is4k = service.Is4k,
                ActiveProfileId = filteredProfiles.Any(p => p.Id == service.ActiveProfileId) ? service.ActiveProfileId : (filteredProfiles.FirstOrDefault()?.Id ?? 0),
                ActiveDirectory = allowedRootPaths.Contains(service.ActiveDirectory) ? service.ActiveDirectory : (filteredRootFolders.FirstOrDefault()?.Path ?? string.Empty),
                Profiles = new System.Collections.ObjectModel.Collection<SeerrQualityProfile>(filteredProfiles),
                RootFolders = filteredRootFolders
            });
        }

        return result;
    }

    /// <summary>
    ///     Builds the set of item keys excluded from the visible pool for a user.
    /// </summary>
    private HashSet<(int TmdbId, string MediaType)> BuildExcludedItemKeys(Guid userId)
    {
        var excluded = new HashSet<(int TmdbId, string MediaType)>();
        try
        {
            foreach (var item in _feedbackStore.GetDismissedItems(userId))
            {
                excluded.Add(item);
            }

            foreach (var item in _feedbackStore.GetRequestedItems(userId))
            {
                excluded.Add(item);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "[Discovery] Failed to load excluded item keys for user {UserId}", userId);
        }

        return excluded;
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
