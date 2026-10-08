using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
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
    private const string LogSource = "ArrIntegration";

    // Named client without TLS certificate validation, for Arr instances behind a reverse proxy
    // with a private CA, self-signed, or IP certificate. Registered in PluginServiceRegistrator;
    // selected per instance, never globally.
    private const string InsecureClientName = "ArrIntegrationInsecure";

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
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating success and a status message.</returns>
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        string baseUrl,
        string apiKey,
        bool skipCertificateValidation = false,
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
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/system/status", skipCertificateValidation, cancellationToken).ConfigureAwait(false);
            var status = JsonSerializer.Deserialize<ArrSystemStatusDto>(json, JsonOptions);
            var appName = status?.AppName ?? "Unknown";
            var version = status?.Version ?? "?";

            if (skipCertificateValidation)
            {
                _pluginLog.LogWarning(LogSource, $"Arr connection test OK for {SsrfGuard.SafeEndpointLabel(baseUrl)}, but TLS certificate validation is disabled for this instance.", null, _logger);
            }

            return (true, $"{appName} v{version}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout elapsed - not a user cancellation
            _pluginLog.LogWarning(LogSource, $"Arr connection test timed out for {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return (false, "Connection timed out.");
        }
        catch (HttpRequestException ex)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Arr connection test failed for {SsrfGuard.SafeEndpointLabel(baseUrl)}: {ex.Message}",
                ex,
                _logger);
            if (!skipCertificateValidation && HasCertificateError(ex))
            {
                return (false, "TLS certificate validation failed. If the server uses a private CA, self-signed, or IP certificate, enable 'Skip certificate validation' for this instance.");
            }

            // The redirect message is already client-safe (built from SafeEndpointLabel, no secrets) and
            // actionable, so surface it directly instead of the generic fallback.
            if (ex.StatusCode is >= (HttpStatusCode)300 and <= (HttpStatusCode)399)
            {
                return (false, ex.Message);
            }

            return (false, "Connection failed. Check the URL and network connectivity.");
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning(LogSource, $"Response too large from Arr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return (false, "Response too large.");
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or ArgumentException)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Arr connection test failed for {SsrfGuard.SafeEndpointLabel(baseUrl)}: {ex.Message}",
                ex,
                _logger);
            return (false, "Connection failed. Check the URL and network connectivity.");
        }
    }

    // Walks the exception chain for a TLS handshake failure (unknown issuer / PartialChain is the
    // reverse-proxy-with-private-CA case) so the test can point at the new opt-out instead of the
    // generic connectivity message.
    private static bool HasCertificateError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Gets all movies from Radarr.
    /// </summary>
    /// <param name="baseUrl">The Radarr base URL.</param>
    /// <param name="apiKey">The Radarr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of movies from Radarr.</returns>
    public async Task<List<ArrMovie>?> GetRadarrMoviesAsync(
        string baseUrl,
        string apiKey,
        bool skipCertificateValidation = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/movie", skipCertificateValidation, cancellationToken).ConfigureAwait(false);
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
            _pluginLog.LogWarning(LogSource, $"Request to {SsrfGuard.SafeEndpointLabel(baseUrl)} timed out", null, _logger);
            return null;
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning(LogSource, $"Response too large from Radarr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            _pluginLog.LogError(LogSource, $"Failed to fetch movies from Radarr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    ///     Gets all series from Sonarr.
    /// </summary>
    /// <param name="baseUrl">The Sonarr base URL.</param>
    /// <param name="apiKey">The Sonarr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of series from Sonarr.</returns>
    public async Task<List<ArrSeries>?> GetSonarrSeriesAsync(
        string baseUrl,
        string apiKey,
        bool skipCertificateValidation = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/series", skipCertificateValidation, cancellationToken).ConfigureAwait(false);
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
            _pluginLog.LogWarning(LogSource, $"Request to {SsrfGuard.SafeEndpointLabel(baseUrl)} timed out", null, _logger);
            return null;
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning(LogSource, $"Response too large from Sonarr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            _pluginLog.LogError(LogSource, $"Failed to fetch series from Sonarr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return null;
        }
    }

    /// <summary>
    ///     Gets the configured root folder paths from a Radarr or Sonarr instance.
    /// </summary>
    /// <param name="baseUrl">The Arr base URL.</param>
    /// <param name="apiKey">The Arr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root folder paths, or null if the fetch failed.</returns>
    public async Task<List<string>?> GetRootFoldersAsync(
        string baseUrl,
        string apiKey,
        bool skipCertificateValidation = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        EnsureApiKeyHeaderSafe(apiKey);

        try
        {
            var json = await FetchJsonAsync(baseUrl, apiKey, "api/v3/rootfolder", skipCertificateValidation, cancellationToken).ConfigureAwait(false);
            var folders = JsonSerializer.Deserialize<List<RootFolderDto>>(json, JsonOptions) ?? [];

            return folders
                .Select(f => f.Path ?? string.Empty)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout elapsed - not a user cancellation; warn that the instance is unreachable.
            _pluginLog.LogWarning(LogSource, $"Request to {SsrfGuard.SafeEndpointLabel(baseUrl)} timed out", null, _logger);
            return null;
        }
        catch (ResponseTooLargeException ex)
        {
            _pluginLog.LogWarning(LogSource, $"Response too large from Arr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            _pluginLog.LogError(LogSource, $"Failed to fetch root folders from Arr at {SsrfGuard.SafeEndpointLabel(baseUrl)}", ex, _logger);
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
        ArgumentNullException.ThrowIfNull(radarrMovies);
        ArgumentNullException.ThrowIfNull(jellyfinFolderNames);

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
        ArgumentNullException.ThrowIfNull(sonarrSeries);
        ArgumentNullException.ThrowIfNull(jellyfinFolderNames);

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

    /// <summary>Returns the last path segment of <paramref name="path"/>, normalized to remove trailing slashes.</summary>
    private static string GetFolderName(string path)
        => path.TrimEnd('/', '\\').Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    /// <summary>
    ///     Determines which Jellyfin libraries correspond to the given Arr root folders.
    ///     A library matches when one of its locations equals a root folder path, or when their
    ///     last path segments match. The segment fallback covers Arr and Jellyfin running in
    ///     separate containers with different mount prefixes for the same physical folder.
    ///     Only libraries whose collection type equals <paramref name="collectionType"/> are considered,
    ///     so a Radarr instance never matches a TV library and vice versa.
    /// </summary>
    /// <param name="rootFolderPaths">Root folder paths reported by the Arr instance.</param>
    /// <param name="libraries">The Jellyfin libraries as (name, collection type, locations) tuples.</param>
    /// <param name="collectionType">The collection type the instance manages (e.g. "movies", "tvshows").</param>
    /// <returns>The names of the libraries that match at least one root folder.</returns>
    public static IReadOnlyList<string> MatchLibrariesToRootFolders(
        IEnumerable<string> rootFolderPaths,
        IEnumerable<(string Name, string? CollectionType, IReadOnlyList<string> Locations)> libraries,
        string collectionType)
    {
        ArgumentNullException.ThrowIfNull(rootFolderPaths);
        ArgumentNullException.ThrowIfNull(libraries);

        var normalizedRoots = new HashSet<string>(
            rootFolderPaths
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        // Snapshot once so it can be enumerated twice (exact pass, then fallback pass).
        var sameType = libraries
            .Where(l => l.Locations is not null
                && string.Equals(l.CollectionType, collectionType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var normalizedLocationSet = new HashSet<string>(
            sameType
                .SelectMany(l => l.Locations)
                .Where(loc => !string.IsNullOrWhiteSpace(loc))
                .Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        // Basename fallback is only for roots with no exact same-type location match, so a container
        // remap still resolves without letting one root's basename pull in an unrelated same-named library.
        var fallbackSegments = new HashSet<string>(
            normalizedRoots
                .Where(root => !normalizedLocationSet.Contains(root))
                .Select(GetFolderName),
            StringComparer.OrdinalIgnoreCase);

        var matched = new List<string>();
        foreach (var (name, _, locations) in sameType)
        {
            var isMatch = locations.Any(loc =>
                !string.IsNullOrWhiteSpace(loc)
                && (normalizedRoots.Contains(NormalizePath(loc)) || fallbackSegments.Contains(GetFolderName(loc))));

            if (isMatch)
            {
                matched.Add(name);
            }
        }

        return matched;
    }

    /// <summary>Trims trailing path separators so equal folders compare equal regardless of a trailing slash.</summary>
    private static string NormalizePath(string path) => path.TrimEnd('/', '\\');

    /// <summary>
    ///     Returns unchanged when it already uses OrdinalIgnoreCase; otherwise returns a new HashSet{T} with the same elements and the correct comparer.
    /// </summary>
    private static HashSet<string> EnsureOrdinalIgnoreCase(HashSet<string> set)
        => ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase)
            ? set
            : new HashSet<string>(set, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Validates baseUrl and relPath, sends a GET request, and returns the response body as a string, enforcing the 100 MB size cap.
    /// </summary>
    private async Task<string> FetchJsonAsync(
        string baseUrl,
        string apiKey,
        string relPath,
        bool skipCertificateValidation,
        CancellationToken cancellationToken)
    {
        ValidateArrUrl(baseUrl);
        var url = new Uri(new Uri(baseUrl.TrimEnd('/', '\\') + '/'), relPath);
        // Do NOT dispose: IHttpClientFactory manages the underlying handler lifetime. The insecure
        // client is a separate named registration with identical hardening except validation.
        var httpClient = _httpClientFactory.CreateClient(skipCertificateValidation ? InsecureClientName : LogSource);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        // ResponseHeadersRead: return as soon as headers arrive so HttpResponseReader's LimitedStream enforces the size cap while streaming the body, instead of HttpClient first buffering the whole body (up to MaxResponseContentBufferSize) and then reading it a second time.
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        // We never auto-follow redirects (SSRF/MITM hardening), so a 3xx here means the configured URL is
        // not the one the server serves the API from (commonly http->https or a canonical host). Turn it
        // into an actionable error carrying the resolved target instead of a cryptic generic failure.
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            var location = response.Headers.Location;
            var hint = string.Empty;
            if (location is not null)
            {
                var absolute = location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(url, location).AbsoluteUri;
                hint = $" Suggested URL: {SsrfGuard.SafeEndpointLabel(absolute)}.";
            }

            throw new HttpRequestException($"The server redirected the request (HTTP {(int)response.StatusCode}). Check the URL, e.g. use https:// or the exact host the server expects.{hint}", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        return await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateArrUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Invalid or unsupported URL scheme", nameof(baseUrl));
        }

        // Central SSRF guard: block cloud metadata endpoints on EVERY path that reaches the network, including the configuration-save path which calls the service directly (bypassing the controller-level check).
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
        public string? Title { get; init; }

        public int Year { get; init; }

        public string? ImdbId { get; init; }

        public int TmdbId { get; init; }

        public bool HasFile { get; init; }

        public string? Path { get; init; }
    }

    private sealed class SonarrSeriesDto
    {
        public string? Title { get; init; }

        public int Year { get; init; }

        public string? ImdbId { get; init; }

        public int TvdbId { get; init; }

        /// <summary>
        ///     Gets the TMDb ID provided by Sonarr v4+ API (added in v4.0.12.2823, June 2024).
        /// </summary>
        public int TmdbId { get; init; }

        public string? Path { get; init; }

        public SonarrStatisticsDto? Statistics { get; init; }
    }

    private sealed class SonarrStatisticsDto
    {
        public int EpisodeFileCount { get; init; }

        public int TotalEpisodeCount { get; init; }
    }

    // Deserialization target for the root-folder endpoint. A record has no standalone set accessor,
    // so the property is populated through the constructor by System.Text.Json.
    private sealed record RootFolderDto(string? Path);
}
