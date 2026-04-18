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
        try
        {
            using var client = CreateClient(baseUrl, apiKey);
            using var response = await client.GetAsync(
                new Uri("api/v1/settings/main", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or UriFormatException or JsonException or ArgumentException)
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
        if (maxAgeDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgeDays), "maxAgeDays must be greater than or equal to 0.");
        }

        var result = new SeerrCleanupResult { DryRun = dryRun };
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-maxAgeDays);

        HttpClient unsafeClient;
        try
        {
            unsafeClient = CreateClient(baseUrl, apiKey);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "SeerrCleanup",
                $"Invalid Seerr configuration: {ex.Message}",
                ex,
                _logger);
            result.Failed = 1;
            return result;
        }

        using var client = unsafeClient;

        // Phase 1: Paginate through all requests and collect expired ones
        var expiredRequests = new List<SeerrRequest>();
        var skip = 0;
        bool hasMore;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestUrl = $"api/v1/request?take={PageSize}&skip={skip}&sort=added&filter=all";

            SeerrRequestPage? page;
            try
            {
                using var response = await client.GetAsync(
                    new Uri(requestUrl, UriKind.Relative),
                    cancellationToken).ConfigureAwait(false);

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
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    $"Failed to fetch requests page (skip={skip}): {ex.Message}",
                    ex,
                    _logger);
                break;
            }

            if (page?.Results == null || page.Results.Count == 0)
            {
                break;
            }

            if (page.PageInfo == null)
            {
                result.Failed++;
                _pluginLog.LogWarning(
                    "SeerrCleanup",
                    "Unexpected API response: missing pageInfo, aborting pagination",
                    logger: _logger);
                break;
            }

            foreach (var request in page.Results)
            {
                result.TotalChecked++;

                // Compare dates in UTC to avoid timezone issues
                if (request.CreatedAt >= cutoffDate)
                {
                    continue;
                }

                result.ExpiredFound++;
                expiredRequests.Add(request);
            }

            skip += page.Results.Count;
            hasMore = skip < page.PageInfo.Results;
        }
        while (hasMore);

        // Phase 2: Process expired requests (log in dry-run, delete otherwise)
        foreach (var request in expiredRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mediaTitle = await ResolveMediaTitleAsync(client, request.Media, cancellationToken).ConfigureAwait(false);
            var mediaInfo = request.Media != null
                ? $"\"{mediaTitle}\" ({request.Media.MediaType}, TMDB: {request.Media.TmdbId})"
                : $"request #{request.Id}";

            var ageDays = (DateTimeOffset.UtcNow - request.CreatedAt).Days;

            if (dryRun)
            {
                _pluginLog.LogInfo(
                    "SeerrCleanup",
                    $"[Dry Run] Would delete expired request #{request.Id} ({mediaInfo}), created {request.CreatedAt:O}, age {ageDays} days",
                    _logger);
            }
            else
            {
                try
                {
                    using var deleteResponse = await client.DeleteAsync(
                        new Uri($"api/v1/request/{request.Id}", UriKind.Relative),
                        cancellationToken).ConfigureAwait(false);

                    if (deleteResponse.IsSuccessStatusCode)
                    {
                        result.Deleted++;
                        _pluginLog.LogInfo(
                            "SeerrCleanup",
                            $"Deleted expired request #{request.Id} ({mediaInfo}), created {request.CreatedAt:O}, age {ageDays} days",
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
            }
        }

        return result;
    }

    /// <summary>
    ///     Resolves the human-readable title for a media item by querying the Seerr movie/TV detail endpoint.
    /// </summary>
    /// <param name="client">The configured HTTP client.</param>
    /// <param name="media">The media info from the request (may be null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved title, or "Unknown" if resolution fails.</returns>
    internal async Task<string> ResolveMediaTitleAsync(
        HttpClient client,
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

            using var response = await client.GetAsync(
                new Uri(endpoint, UriKind.Relative),
                cancellationToken).ConfigureAwait(false);

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
    ///     Creates an HttpClient configured for the Seerr API with the appropriate base URL and API key header.
    /// </summary>
    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient("SeerrIntegration");

        // Validate and normalize the base URL
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsedBaseUrl) ||
            (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException("Invalid Seerr base URL.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        client.BaseAddress = new Uri(parsedBaseUrl.AbsoluteUri.TrimEnd('/') + "/");

        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }
}