using System;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
/// Tracks cleanup statistics (bytes freed, items deleted) and persists them in the plugin configuration.
/// Thread-safe: multiple cleanup tasks may call <see cref="RecordCleanup"/> concurrently.
/// </summary>
public static class CleanupTrackingService
{
    private static readonly Lock SyncLock = new();

    /// <summary>
    /// Records bytes freed and items deleted from a cleanup run into the plugin configuration.
    /// This method is thread-safe and can be called from multiple cleanup tasks concurrently.
    /// </summary>
    /// <param name="bytesFreed">The number of bytes freed.</param>
    /// <param name="itemsDeleted">The number of items deleted.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="pluginInstance">Optional plugin instance to use; if null, uses the static <see cref="Plugin.Instance"/>.</param>
    public static void RecordCleanup(long bytesFreed, int itemsDeleted, ILogger logger, IPluginLogService? pluginLog = null, Plugin? pluginInstance = null)
    {
        var plugin = pluginInstance ?? Plugin.Instance;

        // In test context, ConfigOverride might be used even if Plugin.Instance is null.
        if (plugin == null && CleanupConfigHelper.ConfigOverride == null)
        {
            pluginLog?.LogWarning("CleanupTracking", "Plugin instance is null, cannot record cleanup statistics.", logger: logger);
            return;
        }

        lock (SyncLock)
        {
            var config = CleanupConfigHelper.GetConfig();
            config.TotalBytesFreed += bytesFreed;
            config.TotalItemsDeleted += itemsDeleted;
            config.LastCleanupTimestamp = DateTime.UtcNow;

            plugin?.SaveConfiguration();

            pluginLog?.LogInfo("CleanupTracking", $"Cleanup recorded: {bytesFreed} bytes freed, {itemsDeleted} items deleted. Lifetime total: {config.TotalBytesFreed} bytes, {config.TotalItemsDeleted} items.", logger);
        }
    }

    /// <summary>
    /// Gets the current cleanup statistics from the plugin configuration.
    /// </summary>
    /// <returns>The cleanup statistics or default values if the plugin is not available.</returns>
    public static (long TotalBytesFreed, int TotalItemsDeleted, DateTime LastCleanupTimestamp) GetStatistics()
    {
        var config = CleanupConfigHelper.GetConfig();
        return (config.TotalBytesFreed, config.TotalItemsDeleted, config.LastCleanupTimestamp);
    }
}
