using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    ///     Initializes a new instance of the <see cref="SeerrIntegrationService" /> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named HTTP clients.</param>
    /// <param name="logger">The logger.</param>
    public SeerrIntegrationService(
        IHttpClientFactory httpClientFactory,
        ILogger<SeerrIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
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

        using var client = CreateClient(baseUrl, apiKey);

        // Phase 1: Paginate through all requests and collect expired ones
        var expiredRequests = new List<SeerrRequest>();
        var skip = 0;
        bool hasMore;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestUrl = $"api/v1/request?take={PageSize}&skip={skip}&sort=added&filter=all";
            using var response = await client.GetAsync(
                new Uri(requestUrl, UriKind.Relative),
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<SeerrRequestPage>(json, JsonOptions);

            if (page?.Results == null || page.Results.Count == 0)
            {
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

            var mediaInfo = request.Media != null
                ? $"{request.Media.MediaType} (TMDB: {request.Media.TmdbId})"
                : $"request #{request.Id}";

            if (dryRun)
            {
                _logger.LogInformation(
                    "[Dry Run] Would delete expired Seerr request #{Id} ({Media}), created {CreatedAt:O}, age {Age} days",
                    request.Id,
                    mediaInfo,
                    request.CreatedAt,
                    (DateTimeOffset.UtcNow - request.CreatedAt).Days);
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
                        _logger.LogInformation(
                            "Deleted expired Seerr request #{Id} ({Media}), created {CreatedAt:O}, age {Age} days",
                            request.Id,
                            mediaInfo,
                            request.CreatedAt,
                            (DateTimeOffset.UtcNow - request.CreatedAt).Days);
                    }
                    else
                    {
                        result.Failed++;
                        _logger.LogWarning(
                            "Failed to delete Seerr request #{Id}: HTTP {StatusCode}",
                            request.Id,
                            (int)deleteResponse.StatusCode);
                    }
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    result.Failed++;
                    _logger.LogWarning(ex, "Failed to delete Seerr request #{Id}: timeout", request.Id);
                }
                catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
                {
                    result.Failed++;
                    _logger.LogWarning(
                        ex,
                        "Failed to delete Seerr request #{Id}: {Error}",
                        request.Id,
                        ex.Message);
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Creates an HttpClient configured for the Seerr API with the appropriate base URL and API key header.
    /// </summary>
    private HttpClient CreateClient(string baseUrl, string apiKey)
    {
        var client = _httpClientFactory.CreateClient("SeerrIntegration");

        // Ensure base URL ends with /
        var normalizedUrl = baseUrl.TrimEnd('/') + "/";
        client.BaseAddress = new Uri(normalizedUrl);

        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }
}