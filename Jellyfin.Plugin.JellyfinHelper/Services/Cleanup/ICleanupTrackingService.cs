using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
/// Tracks cleanup statistics (bytes freed, items deleted) and persists them in the plugin configuration.
/// Multiple cleanup tasks may call <see cref="RecordCleanup"/> concurrently.
/// </summary>
public interface ICleanupTrackingService
{
    /// <summary>
    /// Records bytes freed and items deleted from a cleanup run into the plugin configuration.
    /// This method is thread-safe and can be called from multiple cleanup tasks concurrently.
    /// </summary>
    /// <param name="bytesFreed">The number of bytes freed.</param>
    /// <param name="itemsDeleted">The number of items deleted.</param>
    /// <param name="logger">The logger.</param>
    void RecordCleanup(long bytesFreed, int itemsDeleted, ILogger logger);

    /// <summary>
    /// Gets the current cleanup statistics from the plugin configuration.
    /// </summary>
    /// <returns>The cleanup statistics or default values if the plugin is not available.</returns>
    (long TotalBytesFreed, int TotalItemsDeleted, DateTime LastCleanupTimestamp) GetStatistics();
}