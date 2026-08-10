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
    ///     Resolves a Jellyfin user ID to the corresponding Seerr user ID.
    ///     Fetches all Seerr users and matches by the <c>jellyfinUserId</c> field
    ///     using normalized comparison (without hyphens, case-insensitive).
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user GUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The Seerr user ID if a match is found; otherwise null.</returns>
    Task<int?> ResolveSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken cancellationToken);

    /// <summary>
    ///     Evaluates the request permissions for a specific Jellyfin user and service type.
    ///     Resolves the user to their Seerr account, checks their permission bitmask,
    ///     and returns only the quality profiles the user is authorized to select.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The permission evaluation follows Overseerr/Jellyseerr's permission model:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>If the user has no Seerr account → <see cref="UserRequestPermissionResult.CanRequest"/> is <c>false</c>.</item>
    ///         <item>If the user lacks REQUEST permission for the media type → <c>CanRequest</c> is <c>false</c>.</item>
    ///         <item>If the user has REQUEST_ADVANCED, MANAGE_REQUESTS, or ADMIN → all profiles are returned.</item>
    ///         <item>Otherwise (normal user) → only the server's default profile is returned.</item>
    ///     </list>
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