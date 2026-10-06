using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Drives the Trakt OAuth device flow and token lifecycle for a single Jellyfin user. Implementations
///     use the shared client id/secret and persist per-user tokens via <see cref="ITraktUserStore"/>.
/// </summary>
public interface ITraktAuthService
{
    /// <summary>
    ///     Begins the device flow by requesting a device + user code from Trakt.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The device-code response, or null when Trakt is not configured or the request failed.</returns>
    Task<TraktDeviceCodeResponse?> StartDeviceAuthAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Polls once for approval of a device code. On approval the issued token is stored for the user.
    /// </summary>
    /// <param name="userId">The Jellyfin user id to store the token under on approval.</param>
    /// <param name="deviceCode">The device code returned by <see cref="StartDeviceAuthAsync"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The poll status (Pending/Linked/Expired/Denied/Error).</returns>
    Task<TraktDevicePollStatus> PollDeviceAuthAsync(Guid userId, string deviceCode, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns a valid access token for a user, refreshing it first when it has expired. Returns null when
    ///     the user is not linked or the refresh failed (the caller then surfaces a re-link prompt).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A usable access token, or null when the user must re-link.</returns>
    Task<string?> GetValidAccessTokenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Forces a refresh from the stored refresh token, bypassing the expiry check. Used when Trakt
    ///     rejects the current access token mid-flight: either the stored token was already rotated, or it
    ///     is dead. Returns null without touching the store when there is nothing to refresh with.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The new access token, or null when no refresh was possible.</returns>
    Task<string?> RefreshAccessTokenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes a user's stored token (disconnect).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the token has been removed.</returns>
    Task DisconnectAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Validates a Trakt client id by issuing the client-id-only trending request. The client secret is not
    ///     exercised: in the device flow it is only used during token exchange, which cannot be tested without a
    ///     user approving a device code.
    /// </summary>
    /// <param name="clientId">The client id to validate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Success plus a human-readable message describing the outcome.</returns>
    Task<(bool Success, string Message)> TestClientIdAsync(string clientId, CancellationToken cancellationToken);
}
