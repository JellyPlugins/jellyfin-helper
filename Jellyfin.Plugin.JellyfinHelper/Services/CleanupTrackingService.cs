using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Tracks cleanup statistics (bytes freed, items deleted) and persists them in the plugin configuration.
/// Thread-safe: multiple cleanup tasks may call <see cref="RecordCleanup"/> concurrently.
/// </summary>
public static class CleanupTrackingService
{
    private static readonly object SyncLock = new();

    /// <summary>
    /// Records bytes freed and items deleted from a cleanup run into the plugin configuration.
    /// This method is thread-safe and can be called from multiple cleanup tasks concurrently.
    /// </summary>
    /// <param name="bytesFreed">The number of bytes freed.</param>
    /// <param name="itemsDeleted">The number of items deleted.</param>
    /// <param name="logger">The logger.</param>
    public static void RecordCleanup(long bytesFreed, int itemsDeleted, ILogger logger)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            PluginLogService.LogWarning("CleanupTracking", "Plugin instance is null, cannot record cleanup statistics.", logger: logger);
            return;
        }

        lock (SyncLock)
        {
            var config = plugin.Configuration;
            config.TotalBytesFreed += bytesFreed;
            config.TotalItemsDeleted += itemsDeleted;
            config.LastCleanupTimestamp = DateTime.UtcNow;

            plugin.SaveConfiguration();

            PluginLogService.LogInfo("CleanupTracking", $"Cleanup recorded: {bytesFreed} bytes freed, {itemsDeleted} items deleted. Lifetime total: {config.TotalBytesFreed} bytes, {config.TotalItemsDeleted} items.", logger);
        }
    }

    /// <summary>
    /// Gets the current cleanup statistics from the plugin configuration.
    /// </summary>
    /// <returns>The cleanup statistics, or default values if the plugin is not available.</returns>
    public static (long TotalBytesFreed, int TotalItemsDeleted, DateTime LastCleanupTimestamp) GetStatistics()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return (0, 0, DateTime.MinValue);
        }

        var config = plugin.Configuration;
        return (config.TotalBytesFreed, config.TotalItemsDeleted, config.LastCleanupTimestamp);
    }
}