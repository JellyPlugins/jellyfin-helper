using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Per-user Trakt OAuth token storage. Tokens are encrypted at rest and persisted to the plugin data
///     path as JSON. Callers always receive decrypted tokens; the encryption boundary lives inside the store.
/// </summary>
public interface ITraktUserStore
{
    /// <summary>
    ///     Returns the decrypted token for a user, or null when the user has not linked Trakt.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The user's token with plaintext access/refresh values, or null when unlinked.</returns>
    TraktUserToken? Get(Guid userId);

    /// <summary>
    ///     Stores (or replaces) a user's token, encrypting the access and refresh values before they touch disk.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="token">The token to persist, with plaintext access/refresh values.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    /// <returns>A task that completes when the token has been persisted.</returns>
    Task SaveAsync(Guid userId, TraktUserToken token, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes a user's token (disconnect). A no-op when the user has no stored token.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    /// <returns>A task that completes when the token has been removed.</returns>
    Task RemoveAsync(Guid userId, CancellationToken cancellationToken);
}
