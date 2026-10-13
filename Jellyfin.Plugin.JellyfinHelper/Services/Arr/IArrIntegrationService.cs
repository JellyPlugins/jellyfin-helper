using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Arr;

/// <summary>
/// Interface for the service that integrates with Radarr and Sonarr APIs.
/// </summary>
/// <remarks>
///     Static comparison methods (CompareRadarrWithJellyfin, CompareSonarrWithJellyfin) remain on the concrete ArrIntegrationService class because they are pure functions with no instance state.
/// </remarks>
public interface IArrIntegrationService
{
    /// <summary>
    /// Tests connectivity to a Radarr or Sonarr instance by calling its /api/v3/system/status endpoint.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Arr instance.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating success and a status message.</returns>
    Task<(bool Success, string Message)> TestConnectionAsync(string baseUrl, string apiKey, bool skipCertificateValidation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all movies from Radarr.
    /// </summary>
    /// <param name="baseUrl">The Radarr base URL.</param>
    /// <param name="apiKey">The Radarr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of movies from Radarr, or null if the fetch failed.</returns>
    Task<List<ArrMovie>?> GetRadarrMoviesAsync(string baseUrl, string apiKey, bool skipCertificateValidation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all series from Sonarr.
    /// </summary>
    /// <param name="baseUrl">The Sonarr base URL.</param>
    /// <param name="apiKey">The Sonarr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of series from Sonarr, or null if the fetch failed.</returns>
    Task<List<ArrSeries>?> GetSonarrSeriesAsync(string baseUrl, string apiKey, bool skipCertificateValidation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the configured root folder paths from a Radarr or Sonarr instance.
    /// </summary>
    /// <param name="baseUrl">The Arr base URL.</param>
    /// <param name="apiKey">The Arr API key.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root folder paths, or null if the fetch failed.</returns>
    Task<List<string>?> GetRootFoldersAsync(string baseUrl, string apiKey, bool skipCertificateValidation = false, CancellationToken cancellationToken = default);
}