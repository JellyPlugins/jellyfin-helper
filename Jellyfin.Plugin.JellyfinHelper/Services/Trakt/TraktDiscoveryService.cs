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
        if (config is null || !config.TraktSourcingEnabled)
        {
            // Master switch off: source nothing from either own creds or the official plugin, and drop any warm
            // pool so flipping the switch takes effect immediately rather than on the 12h TTL.
            _cache.InvalidatePersonal(userId);
            return null;
        }

        // Resolve this user's personal source. Precedence is deterministic and documented: the user's OWN
        // device-flow link always wins, so installing the official Trakt plugin never silently switches the
        // source for a user who already linked here. Only when the user has no own link do we fall back to the
        // official plugin's token (sourcing through its single app avoids Trakt's one-app-per-free-account
        // collision). Resolution is per user, not cached globally, so two users can use different sources.
        var source = await ResolvePersonalSourceAsync(userId, config, cancellationToken).ConfigureAwait(false);
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

        // The movies fetch on the own flow can unlink the user mid-flight (dead grant). Re-resolve before the
        // shows fetch so it never runs with a dead own token. Because own-precedence is strict (an own-linked
        // user whose token is gone resolves to null, never to the official token), a re-resolve that now returns
        // null means the own link died mid-flight: stop rather than silently finishing on a different source.
        var showsSource = await ResolvePersonalSourceAsync(userId, config, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public Task<bool> IsLinkedForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktSourcingEnabled)
        {
            return Task.FromResult(false);
        }

        // Answer "is this user linked?" WITHOUT forcing a token refresh. A link-status check runs on every tab
        // load / status poll; calling GetValidAccessTokenAsync here would hit the network and rotate the
        // single-use refresh token just to answer a boolean. Read link state cheaply instead:
        //   - own: the store says the user is linked and own creds exist (the actual token is validated/refreshed
        //     later on the fetch path);
        //   - official: the plugin is present and holds a usable, unexpired token (a pure file read).
        var ownLinked = _store.GetToken(userId)?.IsLinked == true && !string.IsNullOrWhiteSpace(config.TraktClientId);
        if (ownLinked)
        {
            return Task.FromResult(true);
        }

        var officialLinked = _officialPlugin.IsPresent() && _officialPlugin.TryGetToken(userId, _now()) is not null;
        return Task.FromResult(officialLinked);
    }

    // Picks the per-user personal source for the FETCH path, own-link first then the official plugin. Returns null
    // when neither is usable. Own-precedence is strict: if the user is own-linked but the own token is currently
    // unavailable (transient refresh failure, revoked grant), this returns null rather than falling back to the
    // official token - the user explicitly linked their own account, so a transient own-token outage must not
    // silently switch their source (and mix two sources' results in the cache). The official branch is reached
    // only for a user with no own link at all. The official token is read-only and never refreshed by the Helper.
    private async Task<PersonalSource?> ResolvePersonalSourceAsync(Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Own device-flow link wins. GetValidAccessTokenAsync may issue a refresh grant, which we want on the own
        // path; it is awaited (never sync-over-async) and honors the request cancellation token.
        if (_store.GetToken(userId)?.IsLinked == true && !string.IsNullOrWhiteSpace(config.TraktClientId))
        {
            var ownToken = await _authService.GetValidAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);

            // Strict precedence: an own-linked user with no currently-usable own token gets no source this
            // request (empty grid), NOT the official token. Do not fall through to the official branch.
            return string.IsNullOrEmpty(ownToken)
                ? null
                : new PersonalSource(config.TraktClientId, ownToken, IsOwnFlow: true);
        }

        // No own link at all: the official plugin may hold a usable, unexpired token for this Jellyfin user.
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
    public void InvalidatePersonal(Guid userId) => _cache.InvalidatePersonal(userId);

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();

        // Trending is sourceable when the master switch is on AND Trakt is available at all: own creds
        // (TraktEnabled) OR the official plugin present. Gating purely on TraktEnabled would blank the trending
        // grid on an official-plugin-only server, which has no own creds. Unlike personal, trending needs only a
        // client id (no bearer), so the official app id is fine.
        if (config is null || !config.TraktSourcingEnabled || (!config.TraktEnabled && !_officialPlugin.IsPresent()))
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
        if (config is null || !config.TraktSourcingEnabled)
        {
            // Master switch off: warm nothing. (The next live request also short-circuits, so no stale pool.)
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

        // On an official-plugin-only server there is no own client id; trending is client-id-only (no bearer), so
        // fall back to the official app's public id rather than sending an empty trakt-api-key (which Trakt rejects).
        var clientId = string.IsNullOrWhiteSpace(config.TraktClientId)
            ? External.OfficialTraktPluginReader.OfficialTraktClientId
            : config.TraktClientId;

        using (var movieReq = BuildRequest("/movies/trending", clientId, config.TraktLimit, accessToken: null))
        {
            var (movies, _) = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, MediaTypeMovie, out var droppedMovies));
            LogDropped(droppedMovies, MediaTypeMovie, "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", clientId, config.TraktLimit, accessToken: null))
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
