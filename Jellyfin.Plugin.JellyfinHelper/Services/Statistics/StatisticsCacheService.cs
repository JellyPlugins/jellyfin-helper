using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
/// Caches the latest full scan result to disk for persistence across restarts.
/// </summary>
public class StatisticsCacheService
{
    private const string LatestResultFileName = "jellyfin-helper-statistics-latest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _latestResultFilePath;
    private readonly ILogger<StatisticsCacheService> _logger;
    private readonly Lock _fileLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsCacheService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public StatisticsCacheService(IApplicationPaths applicationPaths, ILogger<StatisticsCacheService> logger)
    {
        _logger = logger;
        _latestResultFilePath = Path.Join(applicationPaths.DataPath, LatestResultFileName);
    }

    /// <summary>
    /// Saves the latest full statistics result to disk for persistence across server restarts.
    /// </summary>
    /// <param name="result">The statistics result to persist.</param>
    public void SaveLatestResult(MediaStatisticsResult result)
    {
        lock (_fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_latestResultFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(result, JsonOptions);
                File.WriteAllText(_latestResultFilePath, json);

                PluginLogService.LogDebug("StatisticsCache", $"Saved latest statistics result to {_latestResultFilePath}", _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("StatisticsCache", $"Could not save latest statistics result to {_latestResultFilePath}", ex, _logger);
            }
        }
    }

    /// <summary>
    /// Loads the latest full statistics result from disk.
    /// </summary>
    /// <returns>The last saved statistics result, or null if none exists.</returns>
    public MediaStatisticsResult? LoadLatestResult()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_latestResultFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_latestResultFilePath);
                return JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("StatisticsCache", $"Could not load latest statistics result from {_latestResultFilePath}", ex, _logger);
                return null;
            }
        }
    }
}
