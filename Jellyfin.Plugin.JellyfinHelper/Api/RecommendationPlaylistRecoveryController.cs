using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Explicit administrator recovery for ambiguous failed playlist creation; never deletes playlists.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Recommendations/Playlists")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class RecommendationPlaylistRecoveryController : ControllerBase
{
    private readonly IRecommendationPlaylistService _playlists;

    /// <summary>Initializes a new instance of the <see cref="RecommendationPlaylistRecoveryController"/> class.</summary>
    /// <param name="playlists">The managed playlist lifecycle.</param>
    public RecommendationPlaylistRecoveryController(IRecommendationPlaylistService playlists) => _playlists = playlists;

    /// <summary>Releases an administrator-acknowledged ambiguity without deleting manual or partial playlists.</summary>
    /// <param name="userId">The owner whose creation may resume.</param>
    /// <param name="cancellationToken">Cancellation of durable preservation before releasing ambiguity.</param>
    /// <returns>Whether an ambiguous attempt was resolved; no deletion is performed.</returns>
    [HttpPost("{userId}/ResolveCreation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ResolvePlaylistCreation([FromRoute] Guid userId, CancellationToken cancellationToken = default)
        => userId == Guid.Empty ? BadRequest() : Ok(new { Resolved = await _playlists.ResolveDeferredCreationAsync(userId, cancellationToken).ConfigureAwait(false) });
}
