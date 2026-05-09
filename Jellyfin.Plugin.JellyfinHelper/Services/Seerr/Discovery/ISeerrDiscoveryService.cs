using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Interface for the Seerr Discovery service that generates personalized content
///     recommendations from external sources and submits media requests.
/// </summary>
public interface ISeerrDiscoveryService
{
    /// <summary>
    ///     Generates discovery recommendations for all users and persists results.
    ///     Called by the scheduled task.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task GenerateDiscoveryRecommendationsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Submits a media request to the configured Seerr instance.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the media item.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="seerrUserId">Optional Seerr user ID to submit the request as. Null uses API key owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing a success flag and a descriptive message.</returns>
    Task<(bool Success, string Message)> SubmitRequestAsync(
        int tmdbId,
        string mediaType,
        int? seerrUserId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Fetches the list of users from the configured Seerr instance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of Seerr users, or empty if unavailable.</returns>
    Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken cancellationToken);
}
