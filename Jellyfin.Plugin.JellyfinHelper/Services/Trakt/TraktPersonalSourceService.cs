using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktPersonalSourceService"/>: resolves the per-user Trakt source with strict
///     own-link precedence and owns the own-flow refresh/unlink lifecycle. The official plugin's token is
///     strictly read-only here: it is never refreshed or unlinked (the official plugin owns its single-use
///     rotation and re-link on its own cycle).
/// </summary>
public sealed class TraktPersonalSourceService : ITraktPersonalSourceService
{
    private readonly ITraktUserStore _store;
    private readonly ITraktAuthService _authService;
    private readonly External.IOfficialTraktPluginReader _officialPlugin;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktPersonalSourceService"/> class.
    /// </summary>
    /// <param name="store">The own device-flow token store.</param>
    /// <param name="authService">The own device-flow auth service, for per-user access tokens.</param>
    /// <param name="officialPlugin">Reader for the official Trakt plugin's per-user token (fallback source).</param>
    /// <param name="now">Clock seam (absolute instant) for deterministic expiry handling; defaults to local now.</param>
    public TraktPersonalSourceService(
        ITraktUserStore store,
        ITraktAuthService authService,
        External.IOfficialTraktPluginReader officialPlugin,
        Func<DateTimeOffset>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _officialPlugin = officialPlugin ?? throw new ArgumentNullException(nameof(officialPlugin));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public bool IsAvailable(PluginConfiguration config)
        => config.TraktEnabled || _officialPlugin.IsPresent();

    /// <inheritdoc />
    public async Task<TraktPersonalSource?> ResolveAsync(Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Own device-flow link wins, so installing the official plugin never silently switches the source for a
        // user who already linked here. GetValidAccessTokenAsync may issue a refresh grant, which we want on the
        // own path; it is awaited (never sync-over-async) and honors the request cancellation token.
        if (_store.GetToken(userId)?.IsLinked == true && !string.IsNullOrWhiteSpace(config.TraktClientId))
        {
            var ownToken = await _authService.GetValidAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);

            // Strict precedence: an own-linked user with no currently-usable own token gets no source this
            // request (empty grid), NOT the official token. Do not fall through to the official branch.
            return string.IsNullOrEmpty(ownToken)
                ? null
                : new TraktPersonalSource(config.TraktClientId, ownToken, IsOwnFlow: true);
        }

        // Fallback: the official plugin holds a usable, unexpired token for this Jellyfin user. Reached only
        // for a user with no usable own link (unlinked, or own client id not configured).
        if (_officialPlugin.IsPresent())
        {
            var officialToken = _officialPlugin.TryGetToken(userId, _now());
            if (officialToken is not null)
            {
                return new TraktPersonalSource(External.OfficialTraktPluginReader.OfficialTraktClientId, officialToken.AccessToken, IsOwnFlow: false);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsLinked(Guid userId, PluginConfiguration config)
    {
        // Own link first (store read only, no token validation or refresh), then the official plugin's usable
        // token. Mirrors ResolveAsync's precedence without the network cost.
        if (_store.GetToken(userId)?.IsLinked == true && !string.IsNullOrWhiteSpace(config.TraktClientId))
        {
            return true;
        }

        return _officialPlugin.IsPresent() && _officialPlugin.TryGetToken(userId, _now()) is not null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetLinkedUserIds()
    {
        var userIds = new HashSet<Guid>(_store.GetLinkedUserIds());
        if (_officialPlugin.IsPresent())
        {
            foreach (var officialUserId in _officialPlugin.GetLinkedUserIds(_now()))
            {
                userIds.Add(officialUserId);
            }
        }

        return userIds;
    }

    /// <inheritdoc />
    public Task<string?> RefreshOwnTokenAsync(Guid userId, string? rejectedAccessToken, CancellationToken cancellationToken)
        => _authService.RefreshAccessTokenAsync(userId, rejectedAccessToken, cancellationToken);

    /// <inheritdoc />
    public Task UnlinkOwnAsync(Guid userId, CancellationToken cancellationToken)
        => _store.RemoveAsync(userId, cancellationToken);
}
