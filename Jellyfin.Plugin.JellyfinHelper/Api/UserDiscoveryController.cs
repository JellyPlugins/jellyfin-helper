using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     User-facing API controller for Seerr Discovery.
///     Does NOT require admin elevation - any authenticated Jellyfin user can access these endpoints
///     (gated by the <c>DiscoveryUserAccessEnabled</c> configuration toggle).
/// </summary>
[ApiController]
[Authorize]
[Route("JellyfinHelper/Discovery/My")]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class UserDiscoveryController : ControllerBase
{
    private static readonly TimeSpan RequestRateLimit = TimeSpan.FromSeconds(10);

    // Guards the rate-limit check-and-update so it is atomic. The controller is instantiated per
    // request, so an instance lock would not serialize concurrent requests; a shared static lock
    // does. The critical section is only two IMemoryCache operations, so contention is negligible.
    private static readonly object RateLimitGate = new();

    // Generation counter folded into every rate-limit cache key. ClearRateLimitState() bumps it
    // (under RateLimitGate) so all keys minted by a previous plugin load become unreachable — an
    // instant logical reset even when the IMemoryCache instance survives a reload, without waiting
    // for the per-entry TTL to expire (which would otherwise leave a user seeing stale 429s).
    private static long _rateLimitGeneration;

    private readonly IMemoryCache _memoryCache;
    private readonly DiscoveryCacheService _cache;
    private readonly ISeerrDiscoveryService _discovery;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly IPluginConfigurationService _configurationService;
    private readonly ILogger<UserDiscoveryController> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserDiscoveryController"/> class.
    /// </summary>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="discovery">The discovery service.</param>
    /// <param name="feedbackStore">The discovery feedback store for training data collection.</param>
    /// <param name="configurationService">The plugin configuration service.</param>
    /// <param name="memoryCache">The memory cache used for per-user rate limiting.</param>
    /// <param name="logger">The logger instance.</param>
    public UserDiscoveryController(
        DiscoveryCacheService cache,
        ISeerrDiscoveryService discovery,
        IDiscoveryFeedbackStore feedbackStore,
        IPluginConfigurationService configurationService,
        IMemoryCache memoryCache,
        ILogger<UserDiscoveryController> logger)
    {
        _cache = cache;
        _discovery = discovery;
        _feedbackStore = feedbackStore;
        _configurationService = configurationService;
        _memoryCache = memoryCache;
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
        // Normalize MediaType using the same canonicalization as DiscoveryFeedbackStore:
        // null/whitespace → "movie", otherwise trimmed lowercase. This ensures dismissed/requested
        // items are correctly matched regardless of casing differences in cached data.
        var excluded = BuildExcludedItemKeys(currentUserId);
        var visible = userResult.Recommendations
            .Where(r =>
            {
                var normalizedMediaType = string.IsNullOrWhiteSpace(r.MediaType)
                    ? "movie"
                    : r.MediaType.Trim().ToLowerInvariant();
                return !r.AlreadyRequested && !excluded.Contains((r.TmdbId, normalizedMediaType));
            })
            .Take(_discovery.MaxVisiblePerUser)
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
    [ProducesResponseType(typeof(UserRequestPermissionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserRequestPermissionResult>> GetMyRequestPermissions(
        [RegularExpression(@"^(radarr|sonarr)$", ErrorMessage = "serviceType must be 'radarr' or 'sonarr'.")] string serviceType,
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
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<SeerrServiceInfo>>> GetMyServiceInfo(
        [RegularExpression(@"^(radarr|sonarr)$", ErrorMessage = "serviceType must be 'radarr' or 'sonarr'.")] string serviceType,
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
            // Distinguish transient upstream failures (Seerr temporarily unavailable) from
            // genuine permission denials. Return 503 for transient issues so the client can
            // retry, rather than silently returning an empty list that looks like "no services".
            if (permissions.IsTransient)
            {
                return StatusCode(503, new RequestResult
                {
                    Success = false,
                    Message = permissions.DeniedReason ?? "Could not verify your Seerr account. Please try again."
                });
            }

            return Ok(Array.Empty<SeerrServiceInfo>());
        }

        // GetUserRequestPermissionsAsync already evaluated CanSelectQualityProfile and
        // built the allowed profiles list from GetServiceInfoAsync internally.
        // If no profiles were returned, the user should use server defaults - return empty.
        if (permissions.Profiles.Count == 0)
        {
            return Ok(Array.Empty<SeerrServiceInfo>());
        }

        // Reconstruct the filtered service info directly from the permissions result
        // to avoid a redundant second GetServiceInfoAsync HTTP round-trip to Seerr.
        // GetUserRequestPermissionsAsync already called GetServiceInfoAsync internally
        // and distilled the results into the Profiles list with all needed metadata.
        var filteredServices = BuildServiceInfoFromProfiles(permissions.Profiles);
        return Ok(filteredServices);
    }

    /// <summary>
    ///     Returns the external link configuration (Seerr base URL) for constructing
    ///     deep links to TMDB and Seerr from the discovery UI.
    /// </summary>
    /// <returns>An object containing the Seerr base URL.</returns>
    [HttpGet("ExternalLinks")]
    [ProducesResponseType(typeof(SeerrUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult GetExternalLinksConfig()
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

        var config = _configurationService.GetConfiguration();
        var seerrUrl = config.SeerrUrl?.Trim().TrimEnd('/') ?? string.Empty;

        return Ok(new SeerrUrlResponse { SeerrUrl = seerrUrl });
    }

    /// <summary>
    ///     Serves the discovery sidebar JavaScript file as an embedded resource.
    /// </summary>
    /// <remarks>
    ///     AllowAnonymous is required because the script tag in index.html loads
    ///     before Jellyfin's authentication context is established. The script itself
    ///     uses authenticated API calls internally - no sensitive data is exposed here.
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
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<RequestResult>> SubmitMyRequest(
        [FromBody] DiscoveryRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, new RequestResult { Success = false, Message = "Discovery user access is disabled by the administrator." });
        }

        var jellyfinUserId = GetCurrentUserId();
        if (!jellyfinUserId.HasValue)
        {
            return Unauthorized();
        }

        var currentJellyfinUserId = jellyfinUserId.Value;
        var mediaType = dto.MediaType.Trim().ToLowerInvariant();

        // Normalize RootFolder: trim whitespace and coalesce whitespace-only to null
        // so whitespace-only strings are not forwarded as meaningless overrides to Seerr.
        var rootFolder = string.IsNullOrWhiteSpace(dto.RootFolder) ? null : dto.RootFolder.Trim();

        // Per-user rate limit: prevent a single user from flooding Seerr with requests.
        // IMemoryCache auto-evicts entries after RequestRateLimit, so no manual sweep is needed
        // and the dictionary cannot grow unbounded across plugin restarts.
        var now = DateTime.UtcNow;
        var rateLimitExceeded = false;
        var retryAfterSeconds = 0;

        // Atomic check-and-update: without the lock, concurrent requests for the same user could
        // all observe a cache miss (or a stale timestamp) and each write `now`, all passing the
        // limit and submitting duplicate upstream requests. Serializing the read+write closes that
        // race so only the first request in a window proceeds. The key includes the current
        // generation so a ClearRateLimitState() bump invalidates all prior-load entries at once.
        lock (RateLimitGate)
        {
            var rateLimitKey = $"ratelimit:{_rateLimitGeneration}:{currentJellyfinUserId:N}";

            if (_memoryCache.TryGetValue<DateTime>(rateLimitKey, out var lastRequest))
            {
                var elapsed = now - lastRequest;
                if (elapsed < RequestRateLimit)
                {
                    rateLimitExceeded = true;
                    retryAfterSeconds = (int)Math.Ceiling((RequestRateLimit - elapsed).TotalSeconds);
                }
            }

            // Only claim the window when the request is actually allowed through; refreshing the
            // timestamp on a rejected request would extend the block indefinitely under load.
            if (!rateLimitExceeded)
            {
                _memoryCache.Set(rateLimitKey, now, RequestRateLimit);
            }
        }

        if (rateLimitExceeded)
        {
            Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new RequestResult
            {
                Success = false,
                Message = "Too many requests. Please wait before submitting another request."
            });
        }

        var serviceType = mediaType == "movie" ? "radarr" : "sonarr";
        var permissions = await _discovery.GetUserRequestPermissionsAsync(
            currentJellyfinUserId, mediaType, serviceType, cancellationToken).ConfigureAwait(false);

        if (!permissions.CanRequest)
        {
            // Use 503 for transient upstream failures (e.g., Seerr temporarily unavailable)
            // so the client knows to retry, and 403 for genuine permission denials.
            var statusCode = permissions.IsTransient ? 503 : 403;
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
            // between the two calls - use 502 to signal a retriable upstream failure.
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
            // from the client - the request will use Seerr's server default.
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
                // Profile has no root folder constraint - reject if client sends a non-empty
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

        // ⚠️ CancellationToken is DELIBERATELY NOT forwarded to the cache / feedback-store
        // updates below. Once Seerr has accepted the request above, the local bookkeeping
        // MUST run regardless of whether the HTTP client has disconnected - otherwise:
        //   1. The requested item silently reappears on the next discovery-page refresh
        //      because MarkAsRequestedAsync never wrote the AlreadyRequested flag.
        //   2. The DiscoveryFeedbackStore misses a positive-signal training example, so the
        //      ML model never learns from this successful request.
        // Both would silently degrade user experience for a client that likely just closed
        // the tab or lost its connection immediately after clicking "Request".
        //
        // Async variant is preferred (over the legacy sync overload) because it releases
        // the request thread while AtomicFile's transient-IO retries sleep - the sync path
        // can block for up to ~200 ms on AV/indexer contention, which would starve the
        // request pool under a burst of user requests.
        try
        {
            await _cache.MarkAsRequestedAsync(dto.TmdbId, mediaType, currentJellyfinUserId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Best-effort cache update - log but do not fail the request.
            _logger.LogWarning(ex, "[Discovery] Failed to mark item {TmdbId}/{MediaType} as requested in cache for user {UserId}", dto.TmdbId, SanitizeForLog(mediaType), currentJellyfinUserId);
        }

        try
        {
            _feedbackStore.RecordRequested(currentJellyfinUserId, dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "[Discovery] Failed to record requested item {TmdbId}/{MediaType} for user {UserId}", dto.TmdbId, SanitizeForLog(mediaType), currentJellyfinUserId);
        }

        return StatusCode(StatusCodes.Status201Created, new RequestResult { Success = true, Message = message });
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
        var mediaType = dto.MediaType.Trim().ToLowerInvariant();

        try
        {
            _feedbackStore.RecordDismissed(currentUserId, dto.TmdbId, mediaType);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "[Discovery] Failed to record dismissed item {TmdbId}/{MediaType} for user {UserId}", dto.TmdbId, SanitizeForLog(mediaType), currentUserId);
        }

        return Ok(new RequestResult { Success = true, Message = "Item dismissed." });
    }

    /// <summary>
    ///     Resets all per-user rate-limit state. Called from <see cref="Plugin"/>'s constructor on
    ///     every plugin load and from <see cref="Plugin.OnUninstalling"/> so stale entries from a
    ///     previous plugin load do not leak into a subsequent reload (finding #313/#77/#117).
    ///     <para>
    ///         Rate-limit entries live in an <see cref="IMemoryCache"/> that may outlive a plugin
    ///         reload. Rather than enumerate and evict keys, we bump a generation counter that is
    ///         folded into every rate-limit key: all entries minted by the previous generation
    ///         become unreachable at once, so no user carries a stale 429 window across a reload.
    ///     </para>
    /// </summary>
    internal static void ClearRateLimitState()
    {
        lock (RateLimitGate)
        {
            _rateLimitGeneration++;
        }
    }

    /// <summary>
    ///     Reconstructs <see cref="SeerrServiceInfo"/> objects directly from the pre-evaluated
    ///     <see cref="AllowedQualityProfile"/> list without requiring a second Seerr API call.
    ///     The Profiles list from <see cref="UserRequestPermissionResult"/> already contains
    ///     all metadata needed by the frontend (ServerId, ServerName, ProfileId, ProfileName,
    ///     IsDefault, RootFolder) because it was built from the GetServiceInfoAsync result
    ///     during permission evaluation.
    /// </summary>
    /// <param name="allowedProfiles">The user's permitted profiles.</param>
    /// <returns>A list of service info objects grouped by server.</returns>
    private static List<SeerrServiceInfo> BuildServiceInfoFromProfiles(
        IReadOnlyList<AllowedQualityProfile> allowedProfiles)
    {
        // Group profiles by server and reconstruct minimal SeerrServiceInfo objects.
        // This avoids the redundant HTTP round-trip to Seerr that GetServiceInfoAsync would cause.
        return allowedProfiles.GroupBy(p => p.ServerId).Select(serverGroup =>
        {
            var profiles = serverGroup.ToList();
            var firstProfile = profiles[0];
            var defaultProfile = profiles.FirstOrDefault(p => p.IsDefault) ?? firstProfile;

            // Deduplicate quality profiles by ProfileId to prevent duplicates caused by
            // BuildAllowedProfileList emitting one AllowedQualityProfile entry per
            // (ProfileId × RootFolder) combination. The frontend only needs distinct profiles.
            var qualityProfiles = new System.Collections.ObjectModel.Collection<SeerrQualityProfile>(
                profiles
                    .GroupBy(p => p.ProfileId)
                    .Select(g => new SeerrQualityProfile
                    {
                        Id = g.Key,
                        Name = g.First().ProfileName
                    }).ToList());

            var rootFolders = new System.Collections.ObjectModel.Collection<SeerrRootFolder>(
                profiles
                    .Where(p => !string.IsNullOrEmpty(p.RootFolder))
                    .Select(p => p.RootFolder)
                    .Distinct(StringComparer.Ordinal)
                    .Select(path => new SeerrRootFolder { Path = path })
                    .ToList());

            // IsDefault (server-level Seerr "default server" flag) is intentionally not
            // restored here: AllowedQualityProfile carries only profile-level IsDefault
            // (active profile+directory combination), not the server-level flag. The
            // frontend profile-selection popup reads IsDefault from AllowedQualityProfile
            // via the RequestPermissions endpoint, not from SeerrServiceInfo returned here.
            return new SeerrServiceInfo
            {
                Id = firstProfile.ServerId,
                Name = firstProfile.ServerName,
                IsDefault = false,
                Is4k = false,
                ActiveProfileId = defaultProfile.ProfileId,
                ActiveDirectory = defaultProfile.RootFolder ?? string.Empty,
                Profiles = qualityProfiles,
                RootFolders = rootFolders
            };
        }).ToList();
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
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "[Discovery] Failed to load excluded item keys for user {UserId}", userId);
        }

        return excluded;
    }

    /// <summary>
    ///     Checks whether the admin has enabled user-level discovery access in plugin settings.
    /// </summary>
    private bool IsDiscoveryUserAccessEnabled()
    {
        return _configurationService.GetConfiguration().DiscoveryUserAccessEnabled;
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

    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
}
