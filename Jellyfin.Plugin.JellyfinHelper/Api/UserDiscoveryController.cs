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
///     User-facing API controller for Seerr Discovery. Does NOT require admin elevation - any authenticated Jellyfin user can access these endpoints (gated by the DiscoveryUserAccessEnabled configuration toggle).
/// </summary>
[ApiController]
[Authorize]
[Route("JellyfinHelper/Discovery/My")]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public sealed class UserDiscoveryController : ControllerBase
{
    private const string DiscoveryAccessDisabledMessage = "Discovery user access is disabled by the administrator.";
    private const string MovieMediaType = "movie";
    private const string RadarrServiceType = "radarr";
    private static readonly TimeSpan RequestRateLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Minimum interval between out-of-band request reconciliations for a single user on the view-load path. Matches the Seerr user-roster cache TTL, below which a re-fetch cannot surface fresher data.
    /// </summary>
    private static readonly TimeSpan ReconcileTtl = TimeSpan.FromMinutes(5);

    // Guards the rate-limit check-and-update so it is atomic. The controller is instantiated per request, so an instance lock would not serialize concurrent requests; a shared static lock does.
    private static readonly object RateLimitGate = new();

    // Generation counter folded into every rate-limit cache key.
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
    /// <param name="cancellationToken">Cancellation token for the reconciliation fetch.</param>
    /// <returns>The discovery result for the current user, or null if not available.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DiscoveryResult?>> GetMyDiscoveryResults(CancellationToken cancellationToken)
    {
        if (!IsDiscoveryUserAccessEnabled())
        {
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var currentUserId = userId.Value;

        // Fold in any out-of-band Seerr requests before reading the pool so an item the user
        // already requested elsewhere leaves the view and the next backfill item takes its slot.
        await MaybeReconcileAsync(currentUserId, cancellationToken).ConfigureAwait(false);

        // Read the persisted pool without the request token: reconcile above already absorbs a
        // client disconnect, and this endpoint's contract is to still render the cached view rather
        // than fail it. The read only touches the in-memory cache after the first load, so it is cheap.
        var results = await _cache.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var userResult = results.FirstOrDefault(r => r.UserId.Equals(currentUserId));
        if (userResult == null)
        {
            return Ok(null);
        }

        // Filter persisted pool: exclude dismissed/requested, serve next N. Normalize MediaType
        // like DiscoveryFeedbackStore does so matching is case-insensitive.
        var excluded = BuildExcludedItemKeys(currentUserId);
        var visible = userResult.Recommendations
            .Where(r =>
            {
                var normalizedMediaType = string.IsNullOrWhiteSpace(r.MediaType)
                    ? MovieMediaType
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
    ///     Runs discovery reconciliation for the user at most once per <see cref="ReconcileTtl"/> window. The reconciliation itself is fail-safe; this wrapper additionally guarantees a reconciliation failure never breaks the view render.
    /// </summary>
    /// <param name="userId">The current Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token forwarded to the Seerr fetch.</param>
    /// <returns>A task that completes once the (possibly skipped) reconciliation has finished.</returns>
    private async Task MaybeReconcileAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Claim the per-user window under the shared lock BEFORE the async fetch so concurrent
        // view loads for the same user do not each trigger a Seerr round-trip.
        lock (RateLimitGate)
        {
            var key = BuildReconcileKey(userId);
            if (_memoryCache.TryGetValue(key, out _))
            {
                return;
            }

            _memoryCache.Set(key, true, ReconcileTtl);
        }

        try
        {
            await _discovery.ReconcileRequestedItemsAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected mid-reconcile; nothing to render, propagation is harmless here
            // but we swallow to keep the endpoint's contract (never fail on reconcile) uniform.
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "[Discovery] Reconciliation failed for user {UserId}; serving the cached view unchanged.", userId);
        }
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
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var currentUserId = userId.Value;
        serviceType = serviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        mediaType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;

        if (serviceType is not (RadarrServiceType or "sonarr"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "serviceType must be 'radarr' or 'sonarr'." });
        }

        if (mediaType is not (MovieMediaType or "tv"))
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
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        serviceType = serviceType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (serviceType is not (RadarrServiceType or "sonarr"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "serviceType must be 'radarr' or 'sonarr'." });
        }

        // Only expose service infrastructure to users who actually have request permission. Prevents information disclosure of Radarr/Sonarr server names, paths, and profiles to users without the Seerr REQUEST permission.
        var mediaType = serviceType == RadarrServiceType ? MovieMediaType : "tv";
        var permissions = await _discovery.GetUserRequestPermissionsAsync(
            userId.Value, mediaType, serviceType, cancellationToken).ConfigureAwait(false);
        if (!permissions.CanRequest)
        {
            // Distinguish transient upstream failures (Seerr temporarily unavailable) from genuine permission denials.
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

        // GetUserRequestPermissionsAsync already evaluated CanSelectQualityProfile and built the allowed profiles list from GetServiceInfoAsync internally.
        if (permissions.Profiles.Count == 0)
        {
            return Ok(Array.Empty<SeerrServiceInfo>());
        }

        // Reconstruct the filtered service info directly from the permissions result to avoid a redundant second GetServiceInfoAsync HTTP round-trip to Seerr.
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
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
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
    ///     AllowAnonymous is required because the script tag in index.html loads before Jellyfin's authentication context is established.
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

        Response.Headers.CacheControl = "no-cache";
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
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
        }

        ArgumentNullException.ThrowIfNull(dto);

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

        // Per-user rate limit: prevent a single user from flooding Seerr with requests. IMemoryCache auto-evicts entries after RequestRateLimit, so no manual sweep is needed and the dictionary cannot grow unbounded across plugin restarts.
        if (CheckRateLimit(currentJellyfinUserId, out var retryAfterSeconds))
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new RequestResult
            {
                Success = false,
                Message = "Too many requests. Please wait before submitting another request."
            });
        }

        var serviceType = mediaType == MovieMediaType ? RadarrServiceType : "sonarr";
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
            // GetUserRequestPermissionsAsync already confirmed the user exists in Seerr.
            return StatusCode(502, new RequestResult
            {
                Success = false,
                Message = "Could not verify your Seerr account. Please try again."
            });
        }

        var overrideError = ValidateProfileOverride(dto, permissions, rootFolder);
        if (overrideError != null)
        {
            return overrideError;
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

        await PersistRequestBookkeepingAsync(dto, mediaType, currentJellyfinUserId).ConfigureAwait(false);

        return StatusCode(StatusCodes.Status201Created, new RequestResult { Success = true, Message = message });
    }

    /// <summary>
    ///     Atomically checks and claims the current user's per-user rate-limit window.
    /// </summary>
    /// <param name="userId">The current Jellyfin user id.</param>
    /// <param name="retryAfterSeconds">The number of seconds to wait when the limit is exceeded.</param>
    /// <returns><c>true</c> when the request must be rejected as rate limited.</returns>
    private bool CheckRateLimit(Guid userId, out int retryAfterSeconds)
    {
        var now = DateTime.UtcNow;
        var rateLimitExceeded = false;
        retryAfterSeconds = 0;

        // Atomic check-and-update: without the lock, concurrent requests for the same user could all observe a cache miss (or a stale timestamp) and each write `now`, all passing the limit and submitting duplicate upstream requests.
        lock (RateLimitGate)
        {
            var rateLimitKey = BuildRateLimitKey(userId);

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

        return rateLimitExceeded;
    }

    /// <summary>
    ///     Validates a client-supplied server/profile/root-folder override against the permissions the user is actually allowed to use.
    /// </summary>
    private ObjectResult? ValidateProfileOverride(DiscoveryRequestDto dto, UserRequestPermissionResult permissions, string? rootFolder)
    {
        if (!dto.ServerId.HasValue && !dto.ProfileId.HasValue && rootFolder == null)
        {
            return null;
        }

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

        // Validate root folder against the matched profile. When the profile has no specific root folder (empty/null), accept both null and empty from the client - the request will use Seerr's server default.
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

        return null;
    }

    /// <summary>
    ///     Performs the best-effort local bookkeeping (cache + feedback store) after Seerr has accepted a request.
    /// </summary>
    private async Task PersistRequestBookkeepingAsync(DiscoveryRequestDto dto, string mediaType, Guid currentJellyfinUserId)
    {
        // CancellationToken is DELIBERATELY NOT forwarded to the cache / feedback-store updates below. Once Seerr has accepted the request above, the local bookkeeping MUST run regardless of whether the HTTP client has disconnected - otherwise: 1.
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
            return StatusCode(403, new RequestResult { Success = false, Message = DiscoveryAccessDisabledMessage });
        }

        ArgumentNullException.ThrowIfNull(dto);

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
    ///     Resets all per-user rate-limit state. Called from Plugin's constructor on every plugin load and from OnUninstalling so stale entries from a previous plugin load do not leak into a subsequent reload.
    /// </summary>
    internal static void ClearRateLimitState()
    {
        lock (RateLimitGate)
        {
            _rateLimitGeneration++;
        }
    }

    /// <summary>
    ///     Builds the per-user rate-limit cache key. The single source of truth for the key format, shared by the controller and its tests so the two can never drift.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user the request belongs to.</param>
    /// <returns>The fully-qualified, namespaced rate-limit cache key.</returns>
    internal static string BuildRateLimitKey(Guid jellyfinUserId) =>
        $"JellyfinHelper:discovery:ratelimit:{_rateLimitGeneration}:{jellyfinUserId:N}";

    /// <summary>
    ///     Builds the per-user reconciliation-throttle cache key. Shares the rate-limit generation counter so a plugin reload (via ClearRateLimitState) invalidates both throttles at once. The single source of truth for the key format, shared by the controller and its tests so the two can never drift.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user the reconciliation belongs to.</param>
    /// <returns>The fully-qualified, namespaced reconciliation-throttle cache key.</returns>
    internal static string BuildReconcileKey(Guid jellyfinUserId) =>
        $"JellyfinHelper:discovery:reconcile:{_rateLimitGeneration}:{jellyfinUserId:N}";

    /// <summary>
    ///     Reconstructs SeerrServiceInfo objects directly from the pre-evaluated AllowedQualityProfile list without requiring a second Seerr API call.
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

            // Deduplicate quality profiles by ProfileId to prevent duplicates caused by BuildAllowedProfileList emitting one AllowedQualityProfile entry per (ProfileId × RootFolder) combination.
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

            // IsDefault (server-level Seerr "default server" flag) is intentionally not restored here: AllowedQualityProfile carries only profile-level IsDefault (active profile+directory combination), not the server-level flag.
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
        => DiscoverySupport.BuildExcludedItemKeys(
            _feedbackStore,
            userId,
            ex => _logger.LogWarning(ex, "[Discovery] Failed to load excluded item keys for user {UserId}", userId));

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
    private Guid? GetCurrentUserId() => DiscoverySupport.GetCurrentUserId(User);

    private static string SanitizeForLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
}
