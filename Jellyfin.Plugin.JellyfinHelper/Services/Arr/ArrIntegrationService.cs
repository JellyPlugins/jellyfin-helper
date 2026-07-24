using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Arr;

/// <summary>
///     Provides integration with Radarr and Sonarr APIs to compare libraries.
/// </summary>
public sealed class ArrIntegrationService : IArrIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;

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
            ValidateArrUrl(baseUrl);
            var url = new Uri(new Uri(baseUrl.TrimEnd('/', '\\')), "api/v3/system/status").ToString();
            // Do NOT dispose: IHttpClientFactory manages the underlying handler lifetime.
            var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long statusLen && statusLen > 100 * 1024 * 1024)
            {
                throw new InvalidOperationException("Response too large");
            }

            var json = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                "ArrIntegration",
                $"Arr connection test failed for {baseUrl}: {ex.Message}",
                ex,
                _logger);
            return (false, $"Error: {ex.Message}");
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
            ValidateArrUrl(baseUrl);
            // Do NOT dispose: IHttpClientFactory manages the underlying handler lifetime.
            var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
            var url = new Uri(new Uri(baseUrl.TrimEnd('/', '\\')), "api/v3/movie").ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long movieLen && movieLen > 100 * 1024 * 1024)
            {
                throw new InvalidOperationException("Response too large");
            }

            var json = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            // HttpClient.Timeout elapsed — not a user cancellation; warn that the instance is unreachable.
            _pluginLog.LogWarning("ArrIntegration", $"Request to {baseUrl} timed out", null, _logger);
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
            ValidateArrUrl(baseUrl);
            // Do NOT dispose: IHttpClientFactory manages the underlying handler lifetime.
            var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
            var url = new Uri(new Uri(baseUrl.TrimEnd('/', '\\')), "api/v3/series").ToString();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long seriesLen && seriesLen > 100 * 1024 * 1024)
            {
                throw new InvalidOperationException("Response too large");
            }

            var json = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            // HttpClient.Timeout elapsed — not a user cancellation; warn that the instance is unreachable.
            _pluginLog.LogWarning("ArrIntegration", $"Request to {baseUrl} timed out", null, _logger);
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

        // Ensure case-insensitive comparison regardless of caller's HashSet comparer
        var jellyfinNames = ReferenceEquals(jellyfinFolderNames.Comparer, StringComparer.OrdinalIgnoreCase)
            ? jellyfinFolderNames
            : new HashSet<string>(jellyfinFolderNames, StringComparer.OrdinalIgnoreCase);

        foreach (var movie in radarrMovies)
        {
            var folderName = Path.GetFileName(movie.Path.TrimEnd('/').TrimEnd('\\'));
            if (string.IsNullOrEmpty(folderName))
            {
                continue;
            }

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

        // Find Jellyfin-only items (not in Radarr)
        var radarrFolderNames = new HashSet<string>(
            radarrMovies
                .Select(m => Path.GetFileName(m.Path.TrimEnd('/').TrimEnd('\\')))
                .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);

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

        // Ensure case-insensitive comparison regardless of caller's HashSet comparer
        var jellyfinNames = ReferenceEquals(jellyfinFolderNames.Comparer, StringComparer.OrdinalIgnoreCase)
            ? jellyfinFolderNames
            : new HashSet<string>(jellyfinFolderNames, StringComparer.OrdinalIgnoreCase);

        foreach (var series in sonarrSeries)
        {
            var folderName = Path.GetFileName(series.Path.TrimEnd('/').TrimEnd('\\'));
            if (string.IsNullOrEmpty(folderName))
            {
                continue;
            }

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

        var sonarrFolderNames = new HashSet<string>(
            sonarrSeries
                .Select(s => Path.GetFileName(s.Path.TrimEnd('/').TrimEnd('\\')))
                .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var folderName in jellyfinNames.Where(f => !sonarrFolderNames.Contains(f)))
        {
            result.InJellyfinOnly.Add(folderName);
        }

        return result;
    }

    // --- DTOs for Radarr/Sonarr API responses ---

    private static void ValidateArrUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Invalid or unsupported URL scheme", nameof(baseUrl));
        }
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

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int MaxBytes = 100 * 1024 * 1024;
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new LimitedStream(stream, MaxBytes);
        using var reader = new StreamReader(limited);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
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
        ///     Sonarr v3 does NOT include this field — value remains at default 0.
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

    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;

        public LimitedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
            {
                throw new InvalidOperationException("Response too large");
            }

            var toRead = (int)Math.Min(count, remaining);
            var n = _inner.Read(buffer, offset, toRead);
            _bytesRead += n;
            if (_bytesRead > _maxBytes)
            {
                throw new InvalidOperationException("Response too large");
            }

            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
            {
                throw new InvalidOperationException("Response too large");
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _bytesRead += n;
            if (_bytesRead > _maxBytes)
            {
                throw new InvalidOperationException("Response too large");
            }

            return n;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
