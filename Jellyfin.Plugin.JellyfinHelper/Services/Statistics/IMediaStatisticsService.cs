using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
/// Interface for the service that calculates media file statistics per library type.
/// </summary>
public interface IMediaStatisticsService
{
    /// <summary>
    /// Calculates local statistics without network lookups. Production scans use
    /// <see cref="CalculateStatisticsAsync"/> for optional Arr ownership protection.
    /// </summary>
    /// <returns>The aggregated media statistics.</returns>
    MediaStatisticsResult CalculateStatistics();

    /// <summary>Calculates statistics with bounded, optional Arr ownership protection.</summary>
    /// <param name="cancellationToken">Scan cancellation.</param>
    /// <returns>The aggregated media statistics.</returns>
    Task<MediaStatisticsResult> CalculateStatisticsAsync(CancellationToken cancellationToken = default);
}
