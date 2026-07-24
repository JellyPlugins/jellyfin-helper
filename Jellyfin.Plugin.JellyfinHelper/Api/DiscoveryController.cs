using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for Seerr Discovery endpoints.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Discovery")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class DiscoveryController : ControllerBase
{
    private readonly DiscoveryCacheService _cache;
    private readonly ISeerrDiscoveryService _discovery;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
    private readonly ILogger<DiscoveryController> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="discovery">The discovery service.</param>
    /// <param name="feedbackStore">The discovery feedback store.</param>
    /// <param name="logger">The logger.</param>
    public DiscoveryController(DiscoveryCacheService cache, ISeerrDiscoveryService discovery, IDiscoveryFeedbackStore feedbackStore, ILogger<DiscoveryController> logger)
    {
        _cache = cache;
        _discovery = discovery;
        _feedbackStore = feedbackStore;
        _logger = logger;
    }

    /// <summary>
    ///     Returns the cached discovery recommendations for all users.
    /// </summary>
    /// <returns>A list of discovery results per user.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DiscoveryResult>> GetDiscoveryResults()
    {
        var results = _cache.Load();

        // Filter each user pool: only next N visible (non-dismissed, non-requested) items
        var filtered = new List<DiscoveryResult>(results.Count);
        foreach (var userResult in results)
        {
            var excluded = BuildExcludedItemKeys(userResult.UserId);
            var visible = userResult.Recommendations
                .Where(r => !r.AlreadyRequested && !excluded.Contains((r.TmdbId, string.IsNullOrWhiteSpace(r.MediaType) ? "movie" : r.MediaType.Trim().ToLowerInvariant())))
                .Take(_discovery.MaxVisiblePerUser)
                .ToList();

            filtered.Add(new DiscoveryResult
            {
                UserId = userResult.UserId,
                UserName = userResult.UserName,
                Recommendations = visible,
                GeneratedAt = userResult.GeneratedAt
            });
        }

        return Ok(filtered);
    }

    /// <summary>
    ///     Returns the list of Seerr users for the admin profile selection popup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of Seerr users.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SeerrUser>>> GetSeerrUsers(
        CancellationToken cancellationToken)
    {
        var users = await _discovery.GetSeerrUsersAsync(cancellationToken).ConfigureAwait(false);
        return Ok(users);
    }

    /// <summary>
    ///     Returns the configured Radarr or Sonarr service info from Seerr,
    ///     including available quality profiles and root folders.
    /// </summary>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of configured services with profiles.</returns>
    [HttpGet("Services/{serviceType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SeerrServiceInfo>>> GetServiceInfo(
        [RegularExpression("^(radarr|sonarr)$")] string serviceType,
        CancellationToken cancellationToken)
    {
        var services = await _discovery.GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return Ok(services);
    }

    /// <summary>
    ///     Submits a media request to the configured Seerr instance (Admin endpoint).
    /// </summary>
    /// <param name="dto">The request data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure.</returns>
    [HttpPost("Request")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<RequestResult>> SubmitRequest(
        [FromBody] DiscoveryRequestDto dto,
        CancellationToken cancellationToken)
    {
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

        // Block path traversal attempts, control characters, and excessive length in rootFolder
        if (rootFolder != null)
        {
            if (rootFolder.Length > 512)
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path exceeds maximum length." });
            }

            if (rootFolder.Contains("..", StringComparison.Ordinal) ||
                rootFolder.StartsWith('~'))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Invalid root folder path." });
            }

            if (rootFolder.Any(c => char.IsControl(c)))
            {
                return BadRequest(new RequestResult { Success = false, Message = "Root folder path contains invalid characters." });
            }
        }

        var callerUserId = GetCurrentUserId();

        var (success, message) = await _discovery.SubmitRequestAsync(
            dto.TmdbId,
            mediaType,
            dto.SeerrUserId,
            dto.ServerId,
            dto.ProfileId,
            rootFolder,
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new RequestResult { Success = false, Message = message });
        }

        // Mark item as requested in cache so it doesn't reappear on page refresh.
        // Best-effort: don't let cache bookkeeping failures turn a successful Seerr
        // request into a 500 response, which would encourage client retries.
        //
        // Async variant is preferred here (over the legacy MarkAsRequested sync overload)
        // because it releases the request thread while AtomicFile's transient-IO retries
        // sleep — the sync path can block for up to ~200 ms on AV/indexer contention,
        // which would starve the request pool under a burst of user requests.
        //
        // ⚠️ CancellationToken is DELIBERATELY NOT forwarded here. Once Seerr has accepted
        // the request (a few lines above), the local cache MUST be updated regardless of
        // whether the HTTP client has disconnected — otherwise the item silently reappears
        // on the next discovery-page refresh, and the user gets a phantom "please request
        // this again" prompt for an item they already successfully requested. See the
        // similar rationale in UserDiscoveryController.SubmitMyRequest.
        //
        // Pass the admin caller's Jellyfin user ID so the mark is scoped to their cache
        // entry, matching the behaviour of UserDiscoveryController.SubmitMyRequest.
        try
        {
            await _cache.MarkAsRequestedAsync(dto.TmdbId, mediaType, callerUserId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Already logged inside MarkAsRequestedAsync; swallow to preserve the 200 response
            // that the client will (or would have, absent cancellation) receive.
        }

        return Ok(new RequestResult { Success = true, Message = message });
    }

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
            _logger.LogWarning(ex, "Failed to build excluded item keys for user {UserId}; discovery may show already-requested items", userId);
        }

        return excluded;
    }

    private Guid? GetCurrentUserId()
    {
        var claim = User?.FindFirst("Jellyfin-UserId")
            ?? User?.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var parsedUserId))
        {
            return parsedUserId;
        }

        return null;
    }
}