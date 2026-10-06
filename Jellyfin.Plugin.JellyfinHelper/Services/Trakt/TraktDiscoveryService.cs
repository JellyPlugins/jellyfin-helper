using System;
using System.Collections.Generic;
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
    private const string ReasonKey = "reasonTrakt";

    // Caches are warmed by the scheduled task; request-time serves a fresh entry or does a lazy live fetch.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITraktAuthService _authService;
    private readonly ITraktUserStore _store;
    private readonly ISeerrDiscoveryService _discoveryService;
    private readonly TraktCacheService _cache;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktDiscoveryService> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktDiscoveryService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory (uses the hardened "Trakt" client).</param>
    /// <param name="authService">The auth service, for per-user access tokens.</param>
    /// <param name="store">The token store, used to enumerate linked users on refresh.</param>
    /// <param name="discoveryService">The discovery service exposing the external-candidate scoring seam.</param>
    /// <param name="cache">The Trakt result cache.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public TraktDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ITraktAuthService authService,
        ITraktUserStore store,
        ISeerrDiscoveryService discoveryService,
        TraktCacheService cache,
        IPluginLogService pluginLog,
        ILogger<TraktDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetPersonalAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktEnabled)
        {
            return null;
        }

        var cached = _cache.GetPersonal(userId, CacheTtl);
        if (cached is not null)
        {
            return cached;
        }

        var accessToken = await _authService.GetValidAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        var candidates = new List<ExternalDiscoveryCandidate>();
        candidates.AddRange(await FetchPersonalAsync("/recommendations/movies", "movie", config, accessToken, cancellationToken).ConfigureAwait(false));
        candidates.AddRange(await FetchPersonalAsync("/recommendations/shows", "tv", config, accessToken, cancellationToken).ConfigureAwait(false));
        if (candidates.Count == 0)
        {
            return null;
        }

        var result = await _discoveryService.ScoreExternalCandidatesAsync(userId, candidates, ReasonKey, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            _cache.SetPersonal(userId, result);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DiscoveryResult?> GetTrendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktEnabled)
        {
            return null;
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

        // Always score the raw pool per user; a stored ranking is never served to anyone.
        return await _discoveryService.ScoreExternalCandidatesAsync(userId, pool, ReasonKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || !config.TraktEnabled)
        {
            return;
        }

        // Drop the shared trending pool so the next request re-fetches a fresh pool; per-user scoring
        // happens at request time from the raw pool, so there is nothing user-specific to warm here. Then
        // warm each linked user's personal cache, guarding every user so one failure never aborts the rest.
        _cache.InvalidateTrendingPool();

        foreach (var userId in _store.GetLinkedUserIds())
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
        string relPath, string mediaType, PluginConfiguration config, string accessToken, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(relPath, config.TraktClientId, config.TraktLimit, accessToken);
        var items = await SendAndReadAsync<List<TraktMediaItem>>(request, cancellationToken).ConfigureAwait(false);
        var mapped = TraktMapper.MapMediaItems(items, mediaType, out var dropped);
        LogDropped(dropped, mediaType, "personal");
        return mapped;
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchTrendingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var all = new List<ExternalDiscoveryCandidate>();

        using (var movieReq = BuildRequest("/movies/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var movies = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, "movie", out var droppedMovies));
            LogDropped(droppedMovies, "movie", "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var shows = await SendAndReadAsync<List<TraktTrendingItem>>(showReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(shows, "tv", out var droppedShows));
            LogDropped(droppedShows, "tv", "trending");
        }

        return all;
    }

    private async Task<T?> SendAndReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _httpClientFactory.CreateClient("Trakt")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _pluginLog.LogWarning(LogSource, $"Trakt fetch failed: {(int)response.StatusCode}.", logger: _logger);
            return default;
        }

        var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json);
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
