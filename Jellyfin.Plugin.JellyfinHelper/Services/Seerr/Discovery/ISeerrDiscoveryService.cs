using System;
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
    ///     Gets the maximum number of visible discovery recommendations served to the frontend per user.
    /// </summary>
    int MaxVisiblePerUser { get; }

    /// <summary>
    ///     Scores an externally-supplied candidate set for a single user through the same per-user ensemble
    ///     pipeline the local discovery uses (exclusions, parental filter, pre-score, credits enrichment, final
    ///     score). Used by the Trakt source so its items rank exactly as local candidates would. Each resulting
    ///     recommendation is stamped with <paramref name="reasonKey"/>.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user to score for.</param>
    /// <param name="candidates">The pre-mapped TMDb candidates to score.</param>
    /// <param name="reasonKey">The i18n reason key to stamp on every produced recommendation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scored result, or null when the user has no usable profile or no candidates survive.</returns>
    Task<DiscoveryResult?> ScoreExternalCandidatesAsync(
        Guid jellyfinUserId,
        IReadOnlyList<ExternalDiscoveryCandidate> candidates,
        string reasonKey,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Removes dismissed/requested items from a discovery result for a user. Scoring-time exclusion
    ///     cannot see dismissals or requests that land after the score was computed (and cached), so every
    ///     serve filters again; this mirrors the local pool's view-load filtering. Never mutates the input.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user to filter for.</param>
    /// <param name="result">The result to filter.</param>
    /// <returns>A new result with only unconsumed recommendations.</returns>
    DiscoveryResult FilterConsumedItems(Guid jellyfinUserId, DiscoveryResult result);

    /// <summary>
    ///     Generates discovery recommendations for all users and persists results.
    ///     Called by the scheduled task.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task GenerateDiscoveryRecommendationsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Reconciles a user's existing Seerr requests against their cached discovery recommendations. Items the user requested outside the discovery UI are recorded as a positive feedback signal and marked as requested in the cache so they leave the visible pool and the next backfill item takes their slot. Fail-safe: any Seerr error or an unresolvable user leaves the cache and feedback store untouched.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user ID whose requests are reconciled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of cached recommendations newly reconciled as requested.</returns>
    Task<int> ReconcileRequestedItemsAsync(Guid jellyfinUserId, CancellationToken cancellationToken);

    /// <summary>
    ///     Submits a media request to the configured Seerr instance.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the media item.</param>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="seerrUserId">Optional Seerr user ID to submit the request as. Null uses API key owner.</param>
    /// <param name="serverId">Optional Radarr/Sonarr server ID override.</param>
    /// <param name="profileId">Optional quality profile ID override.</param>
    /// <param name="rootFolder">Optional root folder path override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing a success flag and a descriptive message.</returns>
    Task<(bool Success, string Message)> SubmitRequestAsync(
        int tmdbId,
        string mediaType,
        int? seerrUserId,
        int? serverId,
        int? profileId,
        string? rootFolder,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Fetches the list of users from the configured Seerr instance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of Seerr users, or empty if unavailable.</returns>
    Task<IReadOnlyList<SeerrUser>> GetSeerrUsersAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Fetches the configured Radarr/Sonarr service info from Seerr, including quality profiles and root folders.
    /// </summary>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of configured services with profiles and root folders.</returns>
    Task<IReadOnlyList<SeerrServiceInfo>> GetServiceInfoAsync(string serviceType, CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves a Jellyfin user ID to the corresponding Seerr user ID. Fetches all Seerr users and matches by the jellyfinUserId field using normalized comparison (without hyphens, case-insensitive).
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user GUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Seerr user ID if a match is found; otherwise null.</returns>
    Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken);

    /// <summary>
    ///     Evaluates the request permissions for a specific Jellyfin user and service type.
    /// </summary>
    /// <remarks>
    ///     The permission evaluation follows Overseerr/Jellyseerr's permission model: If the user has no Seerr account -> CanRequest is false.
    /// </remarks>
    /// <param name="jellyfinUserId">The Jellyfin user GUID.</param>
    /// <param name="mediaType">"movie" or "tv" - used to check type-specific permissions.</param>
    /// <param name="serviceType">"radarr" or "sonarr".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A permission result describing what the user can do and which profiles are available.</returns>
    Task<UserRequestPermissionResult> GetUserRequestPermissionsAsync(
        Guid jellyfinUserId,
        string mediaType,
        string serviceType,
        CancellationToken cancellationToken);
}