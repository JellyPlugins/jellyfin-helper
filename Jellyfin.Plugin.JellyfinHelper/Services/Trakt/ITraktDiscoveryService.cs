using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Produces Trakt-sourced discovery results: a per-user personal list (OAuth) and a global trending list
///     (client id only). Both are mapped to TMDb candidates and scored through the shared discovery pipeline.
/// </summary>
public interface ITraktDiscoveryService
{
    /// <summary>
    ///     Returns the user's personal Trakt recommendations, scored for them. Serves a fresh cache entry when
    ///     available, otherwise fetches live and caches. Returns null when Trakt is disabled, the user is not
    ///     linked, or no candidates survive.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored personal result, or null.</returns>
    Task<DiscoveryResult?> GetPersonalAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the global trending list scored for the given user. Serves a fresh global cache entry when
    ///     available, otherwise fetches live and caches. Returns null when Trakt is disabled or no candidates
    ///     survive.
    /// </summary>
    /// <param name="userId">The Jellyfin user id the trending list is scored for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored trending result, or null.</returns>
    Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Refreshes both caches for all linked users (personal) and the global trending list. Called by the
    ///     scheduled task. Per-user failures never abort the others.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the refresh pass finishes.</returns>
    Task RefreshAllAsync(CancellationToken cancellationToken);
}
