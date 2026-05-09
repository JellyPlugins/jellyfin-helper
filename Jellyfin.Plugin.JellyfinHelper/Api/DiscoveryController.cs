using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryController"/> class.
    /// </summary>
    /// <param name="cache">The discovery cache service.</param>
    /// <param name="discovery">The discovery service.</param>
    public DiscoveryController(DiscoveryCacheService cache, ISeerrDiscoveryService discovery)
    {
        _cache = cache;
        _discovery = discovery;
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
        return Ok(results);
    }

    /// <summary>
    ///     Returns the list of Seerr users for the profile selection popup.
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
    public async Task<ActionResult<IReadOnlyList<SeerrServiceInfo>>> GetServiceInfo(
        string serviceType,
        CancellationToken cancellationToken)
    {
        var services = await _discovery.GetServiceInfoAsync(serviceType, cancellationToken).ConfigureAwait(false);
        return Ok(services);
    }

    /// <summary>
    ///     Submits a media request to the configured Seerr instance.
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
        if (dto == null || dto.TmdbId <= 0)
        {
            return BadRequest(new RequestResult { Success = false, Message = "Invalid TMDb ID." });
        }

        if (dto.MediaType is not ("movie" or "tv"))
        {
            return BadRequest(new RequestResult { Success = false, Message = "mediaType must be 'movie' or 'tv'." });
        }

        var (success, message) = await _discovery.SubmitRequestAsync(
            dto.TmdbId,
            dto.MediaType,
            dto.SeerrUserId,
            dto.ServerId,
            dto.ProfileId,
            dto.RootFolder,
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            return StatusCode(502, new RequestResult { Success = false, Message = message });
        }

        return Ok(new RequestResult { Success = true, Message = message });
    }
}
