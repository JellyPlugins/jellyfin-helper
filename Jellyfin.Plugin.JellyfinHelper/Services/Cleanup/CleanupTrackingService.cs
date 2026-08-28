using System;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;

/// <summary>
///     Tracks cleanup statistics (bytes freed, items deleted) and persists them in the plugin configuration.
/// </summary>
public class CleanupTrackingService : ICleanupTrackingService
{
    private readonly IPluginConfigurationService _configService;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="CleanupTrackingService" /> class.
    /// </summary>
    /// <param name="configService">The plugin configuration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    public CleanupTrackingService(
        IPluginConfigurationService configService,
        IPluginLogService pluginLog)
    {
        _configService = configService;
        _pluginLog = pluginLog;
    }

    /// <inheritdoc />
    public void RecordCleanup(long bytesFreed, int itemsDeleted, ILogger logger)
    {
        if (bytesFreed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesFreed), "Must be non-negative.");
        }

        if (itemsDeleted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemsDeleted), "Must be non-negative.");
        }

        long totalBytes = 0;
        int totalItems = 0;

        _configService.ReadAndMutate(config =>
        {
            // Clamp to avoid silent overflow on very large or very long-running servers.
            config.TotalBytesFreed = bytesFreed > long.MaxValue - config.TotalBytesFreed
                ? long.MaxValue
                : config.TotalBytesFreed + bytesFreed;

            config.TotalItemsDeleted = itemsDeleted > int.MaxValue - config.TotalItemsDeleted
                ? int.MaxValue
                : config.TotalItemsDeleted + itemsDeleted;

            config.LastCleanupTimestamp = DateTime.UtcNow;

            totalBytes = config.TotalBytesFreed;
            totalItems = config.TotalItemsDeleted;
        });

        _pluginLog.LogInfo(
            "CleanupTracking",
            $"Cleanup recorded: {bytesFreed} bytes freed, {itemsDeleted} items deleted. Lifetime total: {totalBytes} bytes, {totalItems} items.",
            logger);
    }

    /// <inheritdoc />
    public (long TotalBytesFreed, int TotalItemsDeleted, DateTime LastCleanupTimestamp) GetStatistics()
    {
        var config = _configService.GetConfiguration();
        return (config.TotalBytesFreed, config.TotalItemsDeleted, config.LastCleanupTimestamp);
    }
}