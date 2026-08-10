using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
///     Provides integration with Jellyseerr/Overseerr/Seerr instances for request cleanup.
///     Uses the Overseerr API v1 which is compatible with all three forks.
/// </summary>
public sealed class SeerrIntegrationService : ISeerrIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

    /// <summary>
    ///     Number of requests to fetch per page from the Seerr API.
    /// </summary>
    internal const int PageSize = 50;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrIntegrationService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrIntegrationService" /> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named HTTP clients.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public SeerrIntegrationService(
        IHttpClientFactory httpClientFactory,
        IPluginLogService pluginLog,
        ILogger<SeerrIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // Validate inputs before entering the catch-all try block so programming-error
        // exceptions (invalid key format) propagate instead of being swallowed as
        // connection failures.
        if (apiKey.Contains('\r', StringComparison.Ordinal)
            || apiKey.Contains('\n', StringComparison.Ordinal)
            || apiKey.Contains('\t', StringComparison.Ordinal)
            || apiKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("API key must not contain CR, LF, tab, or NUL characters.", nameof(apiKey));
        }

        try
        {
            var (client, baseUri, key) = ValidateAndGetClient(baseUrl, apiKey);
            using var req = BuildRequest(HttpMethod.Get, baseUri, "api/v1/settings/main", key);
            using var response = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<SeerrMainSettings>(json, JsonOptions);

            var title = !string.IsNullOrWhiteSpace(settings?.ApplicationTitle)
                ? settings.ApplicationTitle
                : "Seerr";

            return (true, $"Connected to {title}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or UriFormatException or JsonException or ArgumentException or FormatException)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<SeerrCleanupResult> CleanupExpiredRequestsAsync(
        string baseUrl,
        string apiKey,
        int maxAgeDays,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (maxAgeDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgeDays), "maxAgeDays must be at least 1.");
        }

        var result = new SeerrCleanupResult { DryRun = dryRun };
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-maxAgeDays);

        HttpClient client;
        Uri baseUri;
        string key;
        try
        {
            (client, baseUri, key) = ValidateAndGetClient(baseUrl, apiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or FormatException)
        {
            _pluginLog.LogWarning(
                "SeerrCleanup",
                $"Invalid Seerr configuration: {ex.Message}",
                ex,
                _logger);
            result.Failed = 1;
            return result;
        }

        // Phase 1: Paginate through all requests and collect expired ones
        var expiredRequests = new List<SeerrRequest>();
        var skip = 0;
        bool hasMore;
        var phaseOneFailed = false;
        const int MaxPages = 200;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestUrl = $"api/v1/request?take={PageSize}&skip={skip}&sort=added&filter=all";

            SeerrRequestPage? page;
            try
            {
                using var pageReq = BuildRequest(HttpMethod.Get, baseUri, requestUrl, key);
                using var response = await client.SendAsync(pageReq, cancellationToken).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                page = JsonSerializer.Deserialize<SeerrRequestPage>(json, JsonOptions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                result.Failed++;
                phaseOneFailed = true;
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    $"Timed out fetching requests page (skip={skip}): {ex.Message}",
                    ex,
                    _logger);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                result.Failed++;
                phaseOneFailed = true;
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    $"Failed to fetch requests page (skip={skip}): {ex.Message}",
                    ex,
                    _logger);
                break;
            }

            if (page?.Results == null)
            {
                result.Failed++;
                phaseOneFailed = true;
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    $"Unexpected null response deserializing requests page (skip={skip})",
                    logger: _logger);
                break;
            }

            if (page.Results.Count == 0)
            {
                break;
            }

            if (page.PageInfo == null)
            {
                result.Failed++;
                phaseOneFailed = true;
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    "Unexpected API response: missing pageInfo, aborting pagination",
                    logger: _logger);
                break;
            }

            foreach (var request in page.Results)
            {
                result.TotalChecked++;

                // Fail CLOSED on unknown creation date: only delete when we have a genuinely parsed,
                // non-default timestamp strictly older than the cutoff. A missing/null createdAt
                // (fork / reshaped API / reverse proxy) deserializes to null and MUST be preserved -
                // otherwise brand-new requests get deleted and the maxAgeDays safety is bypassed.
                // A future-dated timestamp is likewise not "expired".
                var createdAt = request.CreatedAt;
                if (createdAt is null
                    || createdAt.Value == default
                    || createdAt.Value >= cutoffDate
                    || createdAt.Value > DateTimeOffset.UtcNow)
                {
                    continue;
                }

                // Allowlist, not denylist: only PENDING (1) and DECLINED (3) requests are ever safe
                // to delete. A denylist ("skip 2/4/5") fails OPEN - a missing status field
                // (deserializes to 0) or a future/unknown Seerr status code would fall through and be
                // deleted. Approved/available/failed/completed and any unrecognized status must be
                // preserved, since Seerr uses them to track downloads and deleting them can trigger
                // duplicate re-requests. (Current Jellyseerr: 1=pending, 2=approved, 3=declined,
                // 4=failed, 5=completed.)
                if (request.Status is not (1 or 3))
                {
                    continue;
                }

                result.ExpiredFound++;
                expiredRequests.Add(request);
            }

            skip += PageSize;
            hasMore = skip < page.PageInfo.Results && (skip / PageSize) < MaxPages;
        }
        while (hasMore);

        // Phase 2: skip deletion if Phase 1 did not complete cleanly
        if (phaseOneFailed)
        {
            _pluginLog.LogWarning(
                "SeerrCleanup",
                "Phase 1 pagination did not complete successfully; skipping deletion to avoid acting on an incomplete snapshot.",
                logger: _logger);
            return result;
        }

        var titleCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var request in expiredRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Guaranteed non-null: every item in expiredRequests passed the fail-closed age guard above.
            var createdAt = request.CreatedAt!.Value;

            var mediaTitle = await ResolveMediaTitleCachedAsync(client, baseUri, key, request.Media, titleCache, cancellationToken).ConfigureAwait(false);
            var mediaInfo = request.Media != null
                ? $"\"{mediaTitle}\" ({request.Media.MediaType}, TMDB: {request.Media.TmdbId})"
                : $"request #{request.Id}";

            var ageDays = (DateTimeOffset.UtcNow - createdAt).Days;

            if (dryRun)
            {
                _pluginLog.LogInfo(
                    "SeerrCleanup",
                    $"[Dry Run] Would delete expired request #{request.Id} ({mediaInfo}), created {createdAt:O}, age {ageDays} days",
                    _logger);
            }
            else
            {
                try
                {
                    using var deleteReq = BuildRequest(HttpMethod.Delete, baseUri, $"api/v1/request/{request.Id}", key);
                    using var deleteResponse = await client.SendAsync(deleteReq, cancellationToken).ConfigureAwait(false);

                    if (deleteResponse.IsSuccessStatusCode)
                    {
                        result.Deleted++;
                        _pluginLog.LogInfo(
                            "SeerrCleanup",
                            $"Deleted expired request #{request.Id} ({mediaInfo}), created {createdAt:O}, age {ageDays} days",
                            _logger);
                    }
                    else
                    {
                        result.Failed++;
                        _pluginLog.LogWarning(
                            "SeerrCleanup",
                            $"Failed to delete request #{request.Id}: HTTP {(int)deleteResponse.StatusCode}",
                            logger: _logger);
                    }
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Failed++;
                    _pluginLog.LogWarning(
                        "SeerrCleanup",
                        $"Failed to delete request #{request.Id}: timeout",
                        ex,
                        _logger);
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
                {
                    result.Failed++;
                    _pluginLog.LogWarning(
                        "SeerrCleanup",
                        $"Failed to delete request #{request.Id}: {ex.Message}",
                        ex,
                        _logger);
                }

                // Small delay between DELETE calls to avoid overwhelming the Seerr API.
                // Break on cancellation so the caller receives partial results with an accurate
                // count rather than silently skipping remaining items without indication.
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Resolves the human-readable title for a media item, using a per-run cache to avoid
    ///     redundant API calls when the same TMDB ID appears in multiple requests.
    /// </summary>
    /// <param name="client">The configured HTTP client.</param>
    /// <param name="baseUri">The normalised base URI of the Seerr instance.</param>
    /// <param name="apiKey">The API key used for per-request authentication.</param>
    /// <param name="media">The media info from the request (may be null).</param>
    /// <param name="titleCache">Cache mapping "mediaType:tmdbId" to resolved titles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved title, or "Unknown" if resolution fails.</returns>
    internal async Task<string> ResolveMediaTitleCachedAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        SeerrMedia? media,
        Dictionary<string, string> titleCache,
        CancellationToken cancellationToken)
    {
        if (media == null || media.TmdbId <= 0)
        {
            return "Unknown";
        }

        var cacheKey = $"{media.MediaType}:{media.TmdbId}";
        if (titleCache.TryGetValue(cacheKey, out var cachedTitle))
        {
            return cachedTitle;
        }

        var title = await ResolveMediaTitleAsync(client, baseUri, apiKey, media, cancellationToken).ConfigureAwait(false);
        titleCache[cacheKey] = title;
        return title;
    }

    /// <summary>
    ///     Resolves the human-readable title for a media item by querying the Seerr movie/TV detail endpoint.
    /// </summary>
    /// <param name="client">The configured HTTP client.</param>
    /// <param name="baseUri">The normalised base URI of the Seerr instance.</param>
    /// <param name="apiKey">The API key used for per-request authentication.</param>
    /// <param name="media">The media info from the request (may be null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved title, or "Unknown" if resolution fails.</returns>
    internal async Task<string> ResolveMediaTitleAsync(
        HttpClient client,
        Uri baseUri,
        string apiKey,
        SeerrMedia? media,
        CancellationToken cancellationToken)
    {
        if (media == null || media.TmdbId <= 0)
        {
            return "Unknown";
        }

        try
        {
            var endpoint = string.Equals(media.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? $"api/v1/tv/{media.TmdbId}"
                : $"api/v1/movie/{media.TmdbId}";

            using var req = BuildRequest(HttpMethod.Get, baseUri, endpoint, apiKey);
            using var response = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return "Unknown";
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var details = JsonSerializer.Deserialize<SeerrMediaDetails>(json, JsonOptions);

            return details?.DisplayTitle ?? "Unknown";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            _pluginLog.LogDebug(
                "SeerrCleanup",
                $"Could not resolve title for TMDB {media.TmdbId}: {ex.Message}",
                _logger);
            return "Unknown";
        }
    }

    /// <summary>
    ///     Validates the base URL and API key, returning the factory-managed HTTP client and
    ///     the normalised base URI. The API key is passed back so callers can attach it per-request
    ///     instead of mutating the shared client's DefaultRequestHeaders.
    /// </summary>
    private (HttpClient Client, Uri BaseUri, string ApiKey) ValidateAndGetClient(string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsedBaseUrl) ||
            (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException("Invalid Seerr base URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        // Reject keys containing CR, LF, tab, or NUL to prevent header injection via TryAddWithoutValidation.
        if (apiKey.Contains('\r', StringComparison.Ordinal)
            || apiKey.Contains('\n', StringComparison.Ordinal)
            || apiKey.Contains('\t', StringComparison.Ordinal)
            || apiKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("API key must not contain CR, LF, tab, or NUL characters.", nameof(apiKey));
        }

        var baseUri = new Uri(parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/");
        var client = _httpClientFactory.CreateClient("SeerrIntegration");
        return (client, baseUri, apiKey);
    }

    /// <summary>
    ///     Builds an <see cref="HttpRequestMessage" /> for the given method and relative path,
    ///     attaching the API key per-request so the shared factory-managed client is not mutated.
    /// </summary>
    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri baseUri, string relPath, string apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, relPath));
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}