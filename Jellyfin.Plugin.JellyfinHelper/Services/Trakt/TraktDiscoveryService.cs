using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktDiscoveryService"/>: fetches Trakt personal recommendations (OAuth) and global
///     trending (client id), maps them to TMDb candidates, and scores them through the shared discovery seam.
/// </summary>
public sealed class TraktDiscoveryService : ITraktDiscoveryService
{
    private const string LogSource = "Trakt";
    private const string MediaTypeMovie = "movie";
    private const string MediaTypeTv = "tv";

    // Caches are warmed by the scheduled task; request-time serves a fresh entry or does a lazy live fetch.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITraktAuthService _authService;
    private readonly ITraktUserStore _store;
    private readonly ISeerrDiscoveryService _discoveryService;
    private readonly TraktCacheService _cache;
    private readonly External.IOfficialTraktPluginReader _officialPlugin;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktDiscoveryService> _logger;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory (uses the hardened "Trakt" client).</param>
    /// <param name="authService">The auth service, for per-user access tokens.</param>
    /// <param name="store">The token store, used to enumerate linked users on refresh.</param>
    /// <param name="discoveryService">The discovery service exposing the external-candidate scoring seam.</param>
    /// <param name="cache">The Trakt result cache.</param>
    /// <param name="officialPlugin">Reader for the official Trakt plugin's per-user token (fallback source).</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="now">Clock seam (absolute instant) for deterministic expiry handling; defaults to local now.</param>
    public TraktDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ITraktAuthService authService,
        ITraktUserStore store,
        ISeerrDiscoveryService discoveryService,
        TraktCacheService cache,
        External.IOfficialTraktPluginReader officialPlugin,
        IPluginLogService pluginLog,
        ILogger<TraktDiscoveryService> logger,
        Func<DateTimeOffset>? now = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _officialPlugin = officialPlugin ?? throw new ArgumentNullException(nameof(officialPlugin));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetPersonalAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null)
        {
            return null;
        }

        // Resolve this user's personal source. Precedence is deterministic and documented: the user's OWN
        // device-flow link always wins, so installing the official Trakt plugin never silently switches the
        // source for a user who already linked here. Only when the user has no own link do we fall back to the
        // official plugin's token (sourcing through its single app avoids Trakt's one-app-per-free-account
        // collision). Resolution is per user, not cached globally, so two users can use different sources.
        var source = ResolvePersonalSource(userId, config);
        if (source is null)
        {
            // No usable source for this user: drop any stale cache rather than serving it after a disconnect.
            _cache.InvalidatePersonal(userId);
            return null;
        }

        var cached = _cache.GetPersonal(userId, CacheTtl);
        if (cached is not null)
        {
            return _discoveryService.FilterConsumedItems(userId, cached);
        }

        var candidates = new List<ExternalDiscoveryCandidate>();
        candidates.AddRange(await FetchPersonalAsync(userId, "/recommendations/movies", MediaTypeMovie, source, config.TraktLimit, rankOffset: 0, cancellationToken).ConfigureAwait(false));

        // An own-flow movies fetch can unlink the user mid-flight (dead grant); re-resolve so we never run the
        // shows fetch with a dead token or score a partial pool. The official source has no such 401-unlink
        // path, so for it the second resolution simply returns the same token.
        var showsSource = ResolvePersonalSource(userId, config);
        if (showsSource is null)
        {
            return null;
        }

        // Continue the ranking after the movies so personal shows rank below personal movies, matching
        // the fetch order.
        candidates.AddRange(await FetchPersonalAsync(userId, "/recommendations/shows", MediaTypeTv, showsSource, config.TraktLimit, candidates.Count, cancellationToken).ConfigureAwait(false));
        if (candidates.Count == 0)
        {
            return null;
        }

        var result = await _discoveryService.ScoreExternalCandidatesAsync(userId, candidates, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            _cache.SetPersonal(userId, result);
            return _discoveryService.FilterConsumedItems(userId, result);
        }

        return null;
    }

    // Picks the per-user personal source, own-link first then the official plugin. Returns null when neither is
    // usable for this user. For the own path the token is read fresh (so a mid-flight rotation is picked up on
    // the second call); for the official path the token is read-only from the foreign config and never refreshed.
    private PersonalSource? ResolvePersonalSource(Guid userId, PluginConfiguration config)
    {
        // Own device-flow link wins. GetValidAccessTokenAsync is intentionally not awaited here: it may issue a
        // refresh grant, which we want on the own path. Mirror the original flow by resolving the own token
        // first and only consulting the official plugin when the user is not linked here.
        if (_store.GetToken(userId)?.IsLinked == true && !string.IsNullOrWhiteSpace(config.TraktClientId))
        {
            var ownToken = _authService.GetValidAccessTokenAsync(userId, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(ownToken))
            {
                return new PersonalSource(config.TraktClientId!, ownToken, IsOwnFlow: true);
            }
        }

        // Fallback: the official plugin holds a usable, unexpired token for this Jellyfin user.
        if (_officialPlugin.IsPresent())
        {
            var officialToken = _officialPlugin.TryGetToken(userId, _now());
            if (officialToken is not null)
            {
                return new PersonalSource(External.OfficialTraktPluginReader.OfficialTraktClientId, officialToken.AccessToken, IsOwnFlow: false);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktEnabled)
        {
            return null;
        }

        // Serve each user's scored trending result from the cache; scoring replays the full
        // Seerr enrichment per candidate, so without this every tab load repeats it.
        var cached = _cache.GetTrending(userId, CacheTtl);
        if (cached is not null)
        {
            return _discoveryService.FilterConsumedItems(userId, cached);
        }

        var pool = _cache.GetTrendingPool(CacheTtl);
        if (pool is null)
        {
            var candidates = await FetchTrendingAsync(config, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return null;
            }

            _cache.SetTrendingPool(candidates);
            pool = candidates;
        }

        // The raw pool is shared; the scored ranking is cached per user and never served across users.
        var scored = await _discoveryService.ScoreExternalCandidatesAsync(userId, pool, cancellationToken).ConfigureAwait(false);
        if (scored is null)
        {
            return null;
        }

        _cache.SetTrending(userId, scored);
        return _discoveryService.FilterConsumedItems(userId, scored);
    }

    /// <inheritdoc />
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null)
        {
            return;
        }

        // Run when Trakt is sourceable at all: either own creds are stored (TraktEnabled) OR the official plugin
        // is present. An official-plugin-only server has no own creds, so gating purely on TraktEnabled would
        // skip warming its users entirely.
        var officialPresent = _officialPlugin.IsPresent();
        if (!config.TraktEnabled && !officialPresent)
        {
            return;
        }

        // Drop the shared trending pool and every per-user trending result derived from it, so the
        // next request re-fetches a fresh pool and rescores; trending is not warmed per user here. Then
        // warm each linked user's personal cache, guarding every user so one failure never aborts the rest.
        _cache.InvalidateTrendingPool();

        // Warm both source populations: users linked via our own device flow, plus users the official plugin
        // holds a usable token for (sourced only through it, so absent from our own store). Dedupe so a user
        // linked both ways is warmed once; own-flow precedence is still resolved per user inside GetPersonalAsync.
        var userIds = new HashSet<Guid>(_store.GetLinkedUserIds());
        if (officialPresent)
        {
            foreach (var officialUserId in _officialPlugin.GetLinkedUserIds(_now()))
            {
                userIds.Add(officialUserId);
            }
        }

        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await GetPersonalAsync(userId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ex.IsFatal() && !cancellationToken.IsCancellationRequested)
            {
                // Any single user's failure (timeout, malformed payload, scoring error) must never abort
                // the remaining users; a requested cancellation still propagates to the caller.
                _pluginLog.LogWarning(LogSource, "Personal refresh failed for a user.", ex, _logger);
            }
        }
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchPersonalAsync(
        Guid userId, string relPath, string mediaType, PersonalSource source, int limit, int rankOffset, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(relPath, source.ClientId, limit, source.AccessToken);
        var (items, status) = await SendAndReadAsync<List<TraktMediaItem>>(request, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            if (!source.IsOwnFlow)
            {
                // The official plugin owns the token lifecycle (single-use refresh, re-link). We are strictly
                // read-only on it: a 401 just means its token went stale, so fail this fetch and let the
                // official plugin refresh on its own cycle. Never refresh or unlink the foreign token here.
                return [];
            }

            // Own flow: the stored token was rejected mid-flight (revoked at trakt.tv after our check): force one
            // refresh and retry once with the new credential.
            var renewed = await _authService.RefreshAccessTokenAsync(userId, source.AccessToken, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(renewed) || string.Equals(renewed, source.AccessToken, StringComparison.Ordinal))
            {
                // No new credential to retry with: fail this fetch without unlinking, so a transient
                // outage never destroys a healthy link.
                return [];
            }

            using var retry = BuildRequest(relPath, source.ClientId, limit, renewed);
            (items, status) = await SendAndReadAsync<List<TraktMediaItem>>(retry, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.Unauthorized)
            {
                // Fresh credentials rejected: the grant is dead. Unlink so the UI offers a re-link
                // instead of an empty grid.
                await _store.RemoveAsync(userId, cancellationToken).ConfigureAwait(false);
                _cache.InvalidatePersonal(userId);
                return [];
            }
        }

        var mapped = TraktMapper.MapMediaItems(items, mediaType, out var dropped, rankOffset);
        LogDropped(dropped, mediaType, "personal");
        return mapped;
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchTrendingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var all = new List<ExternalDiscoveryCandidate>();

        using (var movieReq = BuildRequest("/movies/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var (movies, _) = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, MediaTypeMovie, out var droppedMovies));
            LogDropped(droppedMovies, MediaTypeMovie, "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var (shows, _) = await SendAndReadAsync<List<TraktTrendingItem>>(showReq, cancellationToken).ConfigureAwait(false);

            // Continue the ranking after the movies so trending shows rank below trending movies, matching
            // the fetch order the user sees.
            all.AddRange(TraktMapper.MapTrendingItems(shows, MediaTypeTv, out var droppedShows, all.Count));
            LogDropped(droppedShows, MediaTypeTv, "trending");
        }

        return all;
    }

    private async Task<(T? Data, HttpStatusCode Status)> SendAndReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("Trakt")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _pluginLog.LogWarning(LogSource, $"Trakt fetch failed: {(int)response.StatusCode}.", logger: _logger);
                return (default, response.StatusCode);
            }

            var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(json), response.StatusCode);
        }
        catch (Exception ex) when (!ex.IsFatal() && !cancellationToken.IsCancellationRequested)
        {
            // Network, timeout, size-limit, and malformed-payload failures degrade to an empty
            // fetch (ServiceUnavailable is never treated as Unauthorized, so the link survives).
            _pluginLog.LogWarning(LogSource, "Trakt fetch failed.", ex, _logger);
            return (default, HttpStatusCode.ServiceUnavailable);
        }
    }

    private static HttpRequestMessage BuildRequest(string relPath, string clientId, int limit, string? accessToken)
    {
        // extended=full is load-bearing: without it Trakt omits rating (mapped to 0 downstream), and the
        // minimum-vote filter would then empty the candidate pool.
        var uri = new Uri($"{TraktApi.BaseUrl}{relPath}?limit={limit}&extended=full", UriKind.Absolute);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private void LogDropped(int dropped, string mediaType, string list)
    {
        if (dropped > 0)
        {
            _pluginLog.LogDebug(LogSource, $"Dropped {dropped} {mediaType} {list} item(s) with no TMDb id.", _logger);
        }
    }

    private static PluginConfiguration? GetConfig() => Plugin.Instance?.Configuration;

    // The resolved per-user personal source: which Trakt app (client id) and bearer token to fetch with, and
    // whether it is the user's own device-flow link (which owns the 401-driven refresh/unlink lifecycle) or the
    // read-only official-plugin token (which the Helper must never refresh or unlink).
    private sealed record PersonalSource(string ClientId, string AccessToken, bool IsOwnFlow);
}
