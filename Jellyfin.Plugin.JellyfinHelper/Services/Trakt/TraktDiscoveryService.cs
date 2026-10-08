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
    private readonly ITraktPersonalSourceService _personalSources;
    private readonly ISeerrDiscoveryService _discoveryService;
    private readonly TraktCacheService _cache;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktDiscoveryService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory (uses the hardened "Trakt" client).</param>
    /// <param name="personalSources">The per-user personal-source lifecycle (own link first, official plugin fallback).</param>
    /// <param name="discoveryService">The discovery service exposing the external-candidate scoring seam.</param>
    /// <param name="cache">The Trakt result cache.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public TraktDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ITraktPersonalSourceService personalSources,
        ISeerrDiscoveryService discoveryService,
        TraktCacheService cache,
        IPluginLogService pluginLog,
        ILogger<TraktDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _personalSources = personalSources ?? throw new ArgumentNullException(nameof(personalSources));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        // Cheap link gate first (memory + file read, no network, no token refresh): unlinked users never
        // fetch, and any leftover cache from before a disconnect is dropped instead of served stale. A full
        // source resolution (which may issue a refresh grant on the own flow) runs only on a cache miss, so a
        // transient token failure never drops a warm cache and cache hits never touch the network or the disk.
        // The precedence rules themselves live in ITraktPersonalSourceService; this method only orchestrates.
        if (!_personalSources.IsLinked(userId, config))
        {
            _cache.InvalidatePersonal(userId);
            return null;
        }

        var cached = _cache.GetPersonal(userId, CacheTtl);
        if (cached is not null)
        {
            return _discoveryService.FilterConsumedItems(userId, cached);
        }

        // Resolve this user's personal source. Precedence is deterministic and documented: the user's OWN
        // device-flow link always wins, so installing the official Trakt plugin never silently switches the
        // source for a user who already linked here. Only when the user has no own link do we fall back to the
        // official plugin's token (sourcing through its single app avoids Trakt's one-app-per-free-account
        // collision). Resolution is per user, not cached globally, so two users can use different sources.
        var source = await _personalSources.ResolveAsync(userId, config, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            // Linked at the gate but unresolvable now (a transient refresh failure): serve nothing, but keep
            // the outcome cache-free so the next request retries instead of pinning the outage.
            return null;
        }

        var candidates = new List<ExternalDiscoveryCandidate>();
        var (movies, officialAuthFailed) = await FetchPersonalAsync(userId, "/recommendations/movies", MediaTypeMovie, source, config.TraktLimit, rankOffset: 0, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(movies);

        // A revoked/stale official token fails the movies call and would fail the shows call identically (same
        // dead token, same app). Stop here instead of firing a second doomed request; only the official plugin
        // can re-link this user. Serve nothing cache-free so the next request retries once the plugin rotates.
        if (officialAuthFailed)
        {
            return null;
        }

        // Re-resolve before the shows fetch so a token that went away between the two calls is caught.
        var showsSource = await _personalSources.ResolveAsync(userId, config, cancellationToken).ConfigureAwait(false);
        if (showsSource is null)
        {
            _cache.InvalidatePersonal(userId);
            return null;
        }

        // Continue the ranking after the movies so personal shows rank below personal movies, matching
        // the fetch order.
        var (shows, _) = await FetchPersonalAsync(userId, "/recommendations/shows", MediaTypeTv, showsSource, config.TraktLimit, candidates.Count, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(shows);
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
        // load / status poll; refreshing just to answer a boolean would hit the network and rotate the
        // single-use refresh token. The source service reads link state cheaply (store + file read).
        return Task.FromResult(_personalSources.IsLinked(userId, config));
    }

    /// <inheritdoc />
    public void InvalidatePersonal(Guid userId) => _cache.InvalidatePersonal(userId);

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();

        // Trending is sourceable when the master switch is on AND the official Trakt plugin is present. Trending
        // needs only a client id (no bearer), so the official app id is used.
        if (config is null || !config.TraktSourcingEnabled || !_personalSources.IsAvailable(config))
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

        // Run only when the official Trakt plugin is present (the sole Trakt source).
        if (!_personalSources.IsAvailable(config))
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
        // The merged population comes from the source service, which owns both stores.
        var userIds = _personalSources.GetLinkedUserIds();

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

    private async Task<(List<ExternalDiscoveryCandidate> Items, bool OfficialAuthFailed)> FetchPersonalAsync(
        Guid userId, string relPath, string mediaType, TraktPersonalSource source, int limit, int rankOffset, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(relPath, source.ClientId, limit, source.AccessToken);
        var (items, status) = await SendAndReadAsync<List<TraktMediaItem>>(request, relPath, hasBearer: true, cancellationToken).ConfigureAwait(false);

        // A revoked/stale official token answers 401 OR 403 ("unapproved app" when the token predates the current
        // official client id). We are read-only on it (the official plugin owns refresh/re-link), and the next
        // fetch would fail identically, so signal the caller to skip the remaining calls for this user.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ([], true);
        }

        var mapped = TraktMapper.MapMediaItems(items, mediaType, out var dropped, rankOffset);
        LogDropped(dropped, mediaType, "personal");
        return (mapped, false);
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchTrendingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var all = new List<ExternalDiscoveryCandidate>();

        // Trending is client-id-only (no bearer). Trakt is sourced through the official plugin, so use its public
        // app id rather than an empty trakt-api-key (which Trakt rejects).
        var clientId = External.OfficialTraktPluginReader.OfficialTraktClientId;

        using (var movieReq = BuildRequest("/movies/trending", clientId, config.TraktLimit, accessToken: null))
        {
            var (movies, _) = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, "/movies/trending", hasBearer: false, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, MediaTypeMovie, out var droppedMovies));
            LogDropped(droppedMovies, MediaTypeMovie, "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", clientId, config.TraktLimit, accessToken: null))
        {
            var (shows, _) = await SendAndReadAsync<List<TraktTrendingItem>>(showReq, "/shows/trending", hasBearer: false, cancellationToken).ConfigureAwait(false);

            // Continue the ranking after the movies so trending shows rank below trending movies, matching
            // the fetch order the user sees.
            all.AddRange(TraktMapper.MapTrendingItems(shows, MediaTypeTv, out var droppedShows, all.Count));
            LogDropped(droppedShows, MediaTypeTv, "trending");
        }

        return all;
    }

    private async Task<(T? Data, HttpStatusCode Status)> SendAndReadAsync<T>(
        HttpRequestMessage request, string relPath, bool hasBearer, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("Trakt")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailure(relPath, hasBearer, response);
                return (default, response.StatusCode);
            }

            var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(json), response.StatusCode);
        }
        catch (Exception ex) when (!ex.IsFatal() && !cancellationToken.IsCancellationRequested)
        {
            // Network, timeout, size-limit, and malformed-payload failures degrade to an empty
            // fetch (ServiceUnavailable is never treated as Unauthorized, so the link survives).
            _pluginLog.LogWarning(LogSource, $"Trakt fetch failed ({relPath}).", ex, _logger);
            return (default, HttpStatusCode.ServiceUnavailable);
        }
    }

    // Logs an HTTP failure with the endpoint, plus the VIP header for diagnosis. A 401/403 on a bearer (personal)
    // call means the official plugin's token is stale/revoked - expected, unactionable here (read-only; only the
    // official plugin can re-link), and noisy, so it is logged at DEBUG rather than as a repeated WARN burst.
    // Trending (no bearer) and all other failures stay WARN. No token value is ever logged.
    private void LogFetchFailure(string relPath, bool hasBearer, HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        var authFailure = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        var detail = $"Trakt fetch failed: {code} ({relPath}).";
        if (response.Headers.TryGetValues("X-VIP-User", out var vip))
        {
            detail += $" X-VIP-User={string.Join(",", vip)}";
        }

        if (authFailure && hasBearer)
        {
            _pluginLog.LogDebug(LogSource, detail + " The official Trakt plugin must re-link this user.", _logger);
            return;
        }

        _pluginLog.LogWarning(LogSource, detail, logger: _logger);
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
}
