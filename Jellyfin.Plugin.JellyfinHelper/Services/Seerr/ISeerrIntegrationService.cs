using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
///     Provides integration with Jellyseerr/Overseerr/Seerr instances for request cleanup.
/// </summary>
public interface ISeerrIntegrationService
{
    /// <summary>
    ///     Tests connectivity to a Seerr instance.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Seerr instance.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple indicating success and a descriptive message.</returns>
    Task<(bool Success, string Message)> TestConnectionAsync(string baseUrl, string apiKey, bool skipCertificateValidation = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Performs Seerr cleanup: deletes requests older than the configured age threshold.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Seerr instance.</param>
    /// <param name="apiKey">The Seerr API key.</param>
    /// <param name="maxAgeDays">Maximum age in days before a request is cleaned up.</param>
    /// <param name="dryRun">If true, only reports what would be deleted without making changes.</param>
    /// <param name="skipCertificateValidation">True to skip TLS certificate validation (private CA / self-signed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cleanup result with counts and details.</returns>
    Task<SeerrCleanupResult> CleanupExpiredRequestsAsync(
        string baseUrl,
        string apiKey,
        int maxAgeDays,
        bool dryRun,
        bool skipCertificateValidation = false,
        CancellationToken cancellationToken = default);
}