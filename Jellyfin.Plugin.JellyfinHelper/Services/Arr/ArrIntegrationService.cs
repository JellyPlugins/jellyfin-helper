using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Arr;

/// <summary>
///     Provides integration with Radarr and Sonarr APIs to compare libraries.
/// </summary>
public sealed class ArrIntegrationService : IArrIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private static readonly char[] PathSeparators = ['/', '\\'];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArrIntegrationService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ArrIntegrationService" /> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for creating named HTTP clients.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public ArrIntegrationService(
        IHttpClientFactory httpClientFactory,
        IPluginLogService pluginLog,
        ILogger<ArrIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _pluginLog = pluginLog;
        _logger = logger;
    }

    /// <summary>
    ///     Tests connectivity to a Radarr or Sonarr instance by calling its /api/v3/system/status endpoint.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Arr instance.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating success and a status message.</returns>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, "URL is empty.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "API key is empty.");
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/system/status", cancellationToken).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<ArrSystemStatusDto>(json, JsonOptions);
            var appName = status?.AppName ?? "Unknown";
            var version = status?.Version ?? "?";

            return (true, $"{appName} v{version}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout elapsed - not a user cancellation
            _pluginLog.LogWarning("ArrIntegration", $"Arr connection test timed out for {baseUrl}", ex, _logger);
            return (false, "Connection timed out.");
        }
        catch (HttpRequestException ex)
        {
            _pluginLog.LogWarning(
                "ArrIntegration",
                $"Arr connection test failed for {baseUrl}: {ex.Message}",
                ex,
                _logger);
            return (false, "Connection failed. Check the URL and network connectivity.");
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning("ArrIntegration", $"Response too large from Arr at {baseUrl}", ex, _logger);
            return (false, "Response too large.");
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "ArrIntegration",
                $"Arr connection test failed for {baseUrl}: {ex.Message}",
                ex,
                _logger);
            return (false, "Connection failed. Check the URL and network connectivity.");
        }
    }

    /// <summary>
    ///     Gets all movies from Radarr.
    /// </summary>
    /// <param name="baseUrl">The Radarr base URL.</param>
    /// <param name="apiKey">The Radarr API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of movies from Radarr.</returns>
    public async Task<List<ArrMovie>?> GetRadarrMoviesAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/movie", cancellationToken).ConfigureAwait(false);
            var movies = JsonSerializer.Deserialize<List<RadarrMovieDto>>(json, JsonOptions) ?? [];

            return movies.Select(m => new ArrMovie
            {
                Title = m.Title ?? string.Empty,
                Year = m.Year,
                ImdbId = m.ImdbId ?? string.Empty,
                TmdbId = m.TmdbId,
                HasFile = m.HasFile,
                Path = m.Path ?? string.Empty
            }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout elapsed - not a user cancellation; warn that the instance is unreachable.
            _pluginLog.LogWarning("ArrIntegration", $"Request to {baseUrl} timed out", null, _logger);
            return null;
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning("ArrIntegration", $"Response too large from Radarr at {baseUrl}", ex, _logger);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            _pluginLog.LogError("ArrIntegration", $"Failed to fetch movies from Radarr at {baseUrl}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    ///     Gets all series from Sonarr.
    /// </summary>
    /// <param name="baseUrl">The Sonarr base URL.</param>
    /// <param name="apiKey">The Sonarr API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of series from Sonarr.</returns>
    public async Task<List<ArrSeries>?> GetSonarrSeriesAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/series", cancellationToken).ConfigureAwait(false);
            var series = JsonSerializer.Deserialize<List<SonarrSeriesDto>>(json, JsonOptions) ?? [];

            return series.Select(s => new ArrSeries
            {
                Title = s.Title ?? string.Empty,
                Year = s.Year,
                ImdbId = s.ImdbId ?? string.Empty,
                TvdbId = s.TvdbId,
                TmdbId = s.TmdbId,
                Path = s.Path ?? string.Empty,
                EpisodeFileCount = s.Statistics?.EpisodeFileCount ?? 0,
                TotalEpisodeCount = s.Statistics?.TotalEpisodeCount ?? 0
            }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout elapsed - not a user cancellation; warn that the instance is unreachable.
            _pluginLog.LogWarning("ArrIntegration", $"Request to {baseUrl} timed out", null, _logger);
            return null;
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning("ArrIntegration", $"Response too large from Sonarr at {baseUrl}", ex, _logger);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            _pluginLog.LogError("ArrIntegration", $"Failed to fetch series from Sonarr at {baseUrl}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    ///     Compares Radarr movies with Jellyfin library folder names.
    /// </summary>
    /// <param name="radarrMovies">Movies from Radarr.</param>
    /// <param name="jellyfinFolderNames">Set of folder names in Jellyfin movie libraries.</param>
    /// <returns>The comparison result.</returns>
    public static ArrComparisonResult CompareRadarrWithJellyfin(
        IReadOnlyList<ArrMovie> radarrMovies,
        HashSet<string> jellyfinFolderNames)
    {
        var result = new ArrComparisonResult();
        var jellyfinNames = EnsureOrdinalIgnoreCase(jellyfinFolderNames);

        // Collect Radarr folder names in the same pass to avoid enumerating radarrMovies twice.
        var radarrFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var movie in radarrMovies)
        {
            var folderName = GetFolderName(movie.Path);
            if (string.IsNullOrEmpty(folderName))
            {
                continue;
            }

            radarrFolderNames.Add(folderName);

            if (jellyfinNames.Contains(folderName))
            {
                result.InBoth.Add(movie.Title);
            }
            else if (movie.HasFile)
            {
                result.InArrOnly.Add($"{movie.Title} ({movie.Year}) - has file on disk");
            }
            else
            {
                result.InArrOnlyMissing.Add($"{movie.Title} ({movie.Year}) - no file");
            }
        }

        foreach (var folderName in jellyfinNames.Where(f => !radarrFolderNames.Contains(f)))
        {
            result.InJellyfinOnly.Add(folderName);
        }

        return result;
    }

    /// <summary>
    ///     Compares Sonarr series with Jellyfin library folder names.
    /// </summary>
    /// <param name="sonarrSeries">Series from Sonarr.</param>
    /// <param name="jellyfinFolderNames">Set of folder names in Jellyfin TV libraries.</param>
    /// <returns>The comparison result.</returns>
    public static ArrComparisonResult CompareSonarrWithJellyfin(
        IReadOnlyList<ArrSeries> sonarrSeries,
        HashSet<string> jellyfinFolderNames)
    {
        var result = new ArrComparisonResult();
        var jellyfinNames = EnsureOrdinalIgnoreCase(jellyfinFolderNames);

        // Collect Sonarr folder names in the same pass to avoid enumerating sonarrSeries twice.
        var sonarrFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var series in sonarrSeries)
        {
            var folderName = GetFolderName(series.Path);
            if (string.IsNullOrEmpty(folderName))
            {
                continue;
            }

            sonarrFolderNames.Add(folderName);

            if (jellyfinNames.Contains(folderName))
            {
                result.InBoth.Add(series.Title);
            }
            else if (series.EpisodeFileCount > 0)
            {
                result.InArrOnly.Add(
                    $"{series.Title} ({series.Year}) - {series.EpisodeFileCount}/{series.TotalEpisodeCount} episodes on disk");
            }
            else
            {
                result.InArrOnlyMissing.Add($"{series.Title} ({series.Year}) - no episodes");
            }
        }

        foreach (var folderName in jellyfinNames.Where(f => !sonarrFolderNames.Contains(f)))
        {
            result.InJellyfinOnly.Add(folderName);
        }

        return result;
    }

    // --- DTOs for Radarr/Sonarr API responses ---

    /// <summary>Returns the last path segment of <paramref name="path"/>, normalized to remove trailing slashes.</summary>
    private static string GetFolderName(string path)
        => path.TrimEnd('/', '\\').Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    /// <summary>
    ///     Returns <paramref name="set"/> unchanged when it already uses <see cref="StringComparer.OrdinalIgnoreCase"/>;
    ///     otherwise returns a new <see cref="HashSet{T}"/> with the same elements and the correct comparer.
    /// </summary>
    private static HashSet<string> EnsureOrdinalIgnoreCase(HashSet<string> set)
        => ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase)
            ? set
            : new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Validates <paramref name="baseUrl"/> and <paramref name="relPath"/>, sends a GET request,
    ///     and returns the response body as a string, enforcing the 100 MB size cap.
    ///     Throws <see cref="ArgumentException"/> for bad URLs,
    ///     <see cref="HttpRequestException"/> for non-2xx responses,
    ///     and <see cref="ResponseTooLargeException"/> when the response exceeds the size limit.
    /// </summary>
    private async Task<string> FetchJsonAsync(
        string baseUrl,
        string apiKey,
        string relPath,
        CancellationToken cancellationToken)
    {
        ValidateArrUrl(baseUrl);
        var url = new Uri(new Uri(baseUrl.TrimEnd('/', '\\') + '/'), relPath);
        // Do NOT dispose: IHttpClientFactory manages the underlying handler lifetime.
        var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        // ResponseHeadersRead: return as soon as headers arrive so HttpResponseReader's LimitedStream
        // enforces the size cap while streaming the body, instead of HttpClient first buffering the
        // whole body (up to MaxResponseContentBufferSize) and then reading it a second time.
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateArrUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Invalid or unsupported URL scheme", nameof(baseUrl));
        }

        // Central SSRF guard: block cloud metadata endpoints on EVERY path that reaches the network,
        // including the configuration-save path which calls the service directly (bypassing the
        // controller-level check).
        SsrfGuard.ThrowIfCloudMetadataHost(uri.Host, nameof(baseUrl));
    }

    private static void EnsureApiKeyHeaderSafe(string apiKey)
    {
        if (apiKey.Contains('\r', StringComparison.Ordinal)
            || apiKey.Contains('\n', StringComparison.Ordinal)
            || apiKey.Contains('\t', StringComparison.Ordinal)
            || apiKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("API key must not contain CR, LF, tab, or NUL characters.", nameof(apiKey));
        }
    }

    private sealed class ArrSystemStatusDto
    {
        public string? AppName { get; init; }

        public string? Version { get; init; }
    }

    private sealed class RadarrMovieDto
    {
        public string? Title { get; set; }

        public int Year { get; set; }

        public string? ImdbId { get; set; }

        public int TmdbId { get; set; }

        public bool HasFile { get; set; }

        public string? Path { get; set; }
    }

    private sealed class SonarrSeriesDto
    {
        public string? Title { get; set; }

        public int Year { get; set; }

        public string? ImdbId { get; set; }

        public int TvdbId { get; set; }

        /// <summary>
        ///     Gets or sets the TMDb ID provided by Sonarr v4+ API (added in v4.0.12.2823, June 2024).
        ///     Sonarr v3 does NOT include this field - value remains at default 0.
        /// </summary>
        public int TmdbId { get; set; }

        public string? Path { get; set; }

        public SonarrStatisticsDto? Statistics { get; set; }
    }

    private sealed class SonarrStatisticsDto
    {
        public int EpisodeFileCount { get; set; }

        public int TotalEpisodeCount { get; set; }
    }
}
