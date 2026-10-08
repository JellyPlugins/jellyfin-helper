using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktPersonalSourceService"/>: resolves the per-user Trakt source from the official
///     Jellyfin Trakt plugin's token (strictly read-only — never refreshed or written back). The official plugin
///     owns the token lifecycle; the Helper has no own Trakt app.
/// </summary>
public sealed class TraktPersonalSourceService : ITraktPersonalSourceService
{
    private readonly External.IOfficialTraktPluginReader _officialPlugin;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktPersonalSourceService"/> class.
    /// </summary>
    /// <param name="officialPlugin">Reader for the official Trakt plugin's per-user token.</param>
    /// <param name="now">Clock seam (absolute instant) for deterministic expiry handling; defaults to local now.</param>
    public TraktPersonalSourceService(
        External.IOfficialTraktPluginReader officialPlugin,
        Func<DateTimeOffset>? now = null)
    {
        _officialPlugin = officialPlugin ?? throw new ArgumentNullException(nameof(officialPlugin));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public bool IsAvailable(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _officialPlugin.IsPresent();
    }

    /// <inheritdoc />
    public Task<TraktPersonalSource?> ResolveAsync(Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_officialPlugin.IsPresent())
        {
            var officialToken = _officialPlugin.TryGetToken(userId, _now());
            if (officialToken is not null)
            {
                return Task.FromResult<TraktPersonalSource?>(
                    new TraktPersonalSource(External.OfficialTraktPluginReader.OfficialTraktClientId, officialToken.AccessToken));
            }
        }

        return Task.FromResult<TraktPersonalSource?>(null);
    }

    /// <inheritdoc />
    public bool IsLinked(Guid userId, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // Cheap link check (file read, no network): the official plugin holds a usable, unexpired token.
        return _officialPlugin.IsPresent() && _officialPlugin.TryGetToken(userId, _now()) is not null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetLinkedUserIds()
    {
        if (!_officialPlugin.IsPresent())
        {
            return [];
        }

        return new HashSet<Guid>(_officialPlugin.GetLinkedUserIds(_now()));
    }
}
