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
    private const string ReasonKey = "reasonTrakt";

    // Caches are warmed by the scheduled task; request-time serves a fresh entry or does a lazy live fetch.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITraktAuthService _authService;
    private readonly ITraktUserStore _store;
    private readonly ISeerrDiscoveryService _discoveryService;
    private readonly IDiscoveryFeedbackStore _feedbackStore;
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
    /// <param name="feedbackStore">The dismissed/requested store, applied on every serve.</param>
    /// <param name="cache">The Trakt result cache.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public TraktDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ITraktAuthService authService,
        ITraktUserStore store,
        ISeerrDiscoveryService discoveryService,
        IDiscoveryFeedbackStore feedbackStore,
        TraktCacheService cache,
        IPluginLogService pluginLog,
        ILogger<TraktDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _feedbackStore = feedbackStore ?? throw new ArgumentNullException(nameof(feedbackStore));
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

        // Link check first (memory-only): unlinked users never fetch, and any leftover cache from
        // before a disconnect is dropped instead of served stale.
        if (_store.GetToken(userId)?.IsLinked != true)
        {
            _cache.InvalidatePersonal(userId);
            return null;
        }

        var cached = _cache.GetPersonal(userId, CacheTtl);
        if (cached is not null)
        {
            return WithVisibleItems(userId, cached);
        }

        var accessToken = await _authService.GetValidAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(accessToken))
        {
            // No usable token and nothing cached: nothing to serve.
            return null;
        }

        var candidates = new List<ExternalDiscoveryCandidate>();
        candidates.AddRange(await FetchPersonalAsync(userId, "/recommendations/movies", "movie", config, accessToken, cancellationToken).ConfigureAwait(false));
        candidates.AddRange(await FetchPersonalAsync(userId, "/recommendations/shows", "tv", config, accessToken, cancellationToken).ConfigureAwait(false));
        if (candidates.Count == 0)
        {
            return null;
        }

        var result = await _discoveryService.ScoreExternalCandidatesAsync(userId, candidates, ReasonKey, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            _cache.SetPersonal(userId, result);
            return WithVisibleItems(userId, result);
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
        var scored = await _discoveryService.ScoreExternalCandidatesAsync(userId, pool, ReasonKey, cancellationToken).ConfigureAwait(false);
        return scored is null ? null : WithVisibleItems(userId, scored);
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

    // Removes dismissed/requested items on every serve, mirroring the local pool's view-load
    // filtering. Scoring-time exclusion is not enough: a dismissal or request can land after the
    // score was computed (and cached for hours). The cache keeps the full pool; filtering happens
    // here so consumed items never reappear and can never be requested twice.
    private DiscoveryResult WithVisibleItems(Guid userId, DiscoveryResult result)
    {
        HashSet<(int TmdbId, string MediaType)>? excluded = null;
        try
        {
            var dismissed = _feedbackStore.GetDismissedItems(userId);
            var requested = _feedbackStore.GetRequestedItems(userId);
            if ((dismissed?.Count ?? 0) > 0 || (requested?.Count ?? 0) > 0)
            {
                excluded = new HashSet<(int TmdbId, string MediaType)>();
                if (dismissed is not null)
                {
                    excluded.UnionWith(dismissed);
                }

                if (requested is not null)
                {
                    excluded.UnionWith(requested);
                }
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogDebug(LogSource, $"Consumed-item filter unavailable ({ex.Message}); serving unfiltered.", _logger);
        }

        if (excluded is null)
        {
            return new DiscoveryResult
            {
                UserId = result.UserId,
                Recommendations = [.. result.Recommendations],
                GeneratedAt = result.GeneratedAt,
            };
        }

        return new DiscoveryResult
        {
            UserId = result.UserId,
            Recommendations = result.Recommendations
                .Where(r =>
                {
                    var mediaType = string.IsNullOrWhiteSpace(r.MediaType) ? "movie" : r.MediaType.Trim().ToLowerInvariant();
                    return !r.AlreadyRequested && !excluded.Contains((r.TmdbId, mediaType));
                })
                .ToList(),
            GeneratedAt = result.GeneratedAt,
        };
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchPersonalAsync(
        Guid userId, string relPath, string mediaType, PluginConfiguration config, string accessToken, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(relPath, config.TraktClientId, config.TraktLimit, accessToken);
        var (items, status) = await SendAndReadAsync<List<TraktMediaItem>>(request, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized)
        {
            // The stored token was rejected mid-flight (revoked at trakt.tv after our check): force one
            // refresh and retry once with the new credential.
            var renewed = await _authService.RefreshAccessTokenAsync(userId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(renewed) || string.Equals(renewed, accessToken, StringComparison.Ordinal))
            {
                // No new credential to retry with: fail this fetch without unlinking, so a transient
                // outage never destroys a healthy link.
                return [];
            }

            using var retry = BuildRequest(relPath, config.TraktClientId, config.TraktLimit, renewed);
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

        var mapped = TraktMapper.MapMediaItems(items, mediaType, out var dropped);
        LogDropped(dropped, mediaType, "personal");
        return mapped;
    }

    private async Task<List<ExternalDiscoveryCandidate>> FetchTrendingAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var all = new List<ExternalDiscoveryCandidate>();

        using (var movieReq = BuildRequest("/movies/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var (movies, _) = await SendAndReadAsync<List<TraktTrendingItem>>(movieReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(movies, "movie", out var droppedMovies));
            LogDropped(droppedMovies, "movie", "trending");
        }

        using (var showReq = BuildRequest("/shows/trending", config.TraktClientId, config.TraktLimit, accessToken: null))
        {
            var (shows, _) = await SendAndReadAsync<List<TraktTrendingItem>>(showReq, cancellationToken).ConfigureAwait(false);
            all.AddRange(TraktMapper.MapTrendingItems(shows, "tv", out var droppedShows));
            LogDropped(droppedShows, "tv", "trending");
        }

        return all;
    }

    private async Task<(T? Data, HttpStatusCode Status)> SendAndReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
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
