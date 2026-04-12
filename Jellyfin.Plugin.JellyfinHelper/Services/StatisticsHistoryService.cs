using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Persists statistics snapshots to a JSON file for historical trend tracking,
/// and caches the latest full scan result to disk for persistence across restarts.
/// </summary>
public class StatisticsHistoryService
{
    private const string HistoryFileName = "jellyfin-helper-statistics-history.json";
    private const string LatestResultFileName = "jellyfin-helper-statistics-latest.json";
    private const int MaxSnapshots = 365;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _historyFilePath;
    private readonly string _latestResultFilePath;
    private readonly ILogger<StatisticsHistoryService> _logger;
    private readonly object _fileLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsHistoryService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public StatisticsHistoryService(IApplicationPaths applicationPaths, ILogger<StatisticsHistoryService> logger)
    {
        _logger = logger;
        _historyFilePath = Path.Combine(applicationPaths.DataPath, HistoryFileName);
        _latestResultFilePath = Path.Combine(applicationPaths.DataPath, LatestResultFileName);
    }

    /// <summary>
    /// Loads all historical snapshots from disk.
    /// </summary>
    /// <returns>A read-only list of snapshots ordered by timestamp ascending.</returns>
    public IReadOnlyList<StatisticsSnapshot> LoadHistory()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_historyFilePath))
                {
                    return Array.Empty<StatisticsSnapshot>();
                }

                var json = File.ReadAllText(_historyFilePath);
                var snapshots = JsonSerializer.Deserialize<List<StatisticsSnapshot>>(json, JsonOptions);
                return snapshots?.AsReadOnly() ?? (IReadOnlyList<StatisticsSnapshot>)Array.Empty<StatisticsSnapshot>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("StatisticsHistory", $"Could not load statistics history from {_historyFilePath}", ex, _logger);
                return Array.Empty<StatisticsSnapshot>();
            }
        }
    }

    /// <summary>
    /// Appends a snapshot derived from the given result to the history file.
    /// Automatically trims old entries beyond <see cref="MaxSnapshots"/>.
    /// </summary>
    /// <param name="result">The statistics result to snapshot.</param>
    public void SaveSnapshot(MediaStatisticsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var snapshot = StatisticsSnapshot.FromResult(result);

        lock (_fileLock)
        {
            try
            {
                var history = LoadHistoryUnsafe();
                history.Add(snapshot);

                // Trim to keep only the most recent snapshots
                if (history.Count > MaxSnapshots)
                {
                    history.RemoveRange(0, history.Count - MaxSnapshots);
                }

                var directory = Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(history, JsonOptions);
                File.WriteAllText(_historyFilePath, json);

                PluginLogService.LogInfo("StatisticsHistory", $"Saved statistics snapshot ({history.Count} total entries)", _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("StatisticsHistory", $"Could not save statistics history to {_historyFilePath}", ex, _logger);
            }
        }
    }

    /// <summary>
    /// Saves the latest full statistics result to disk for persistence across server restarts.
    /// </summary>
    /// <param name="result">The statistics result to persist.</param>
    public void SaveLatestResult(MediaStatisticsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

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

                PluginLogService.LogDebug("StatisticsHistory", $"Saved latest statistics result to {_latestResultFilePath}", _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PluginLogService.LogWarning("StatisticsHistory", $"Could not save latest statistics result to {_latestResultFilePath}", ex, _logger);
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
                PluginLogService.LogWarning("StatisticsHistory", $"Could not load latest statistics result from {_latestResultFilePath}", ex, _logger);
                return null;
            }
        }
    }

    /// <summary>
    /// Internal load without locking (caller must hold the lock).
    /// </summary>
    private List<StatisticsSnapshot> LoadHistoryUnsafe()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return new List<StatisticsSnapshot>();
            }

            var json = File.ReadAllText(_historyFilePath);
            var snapshots = JsonSerializer.Deserialize<List<StatisticsSnapshot>>(json, JsonOptions);
            return snapshots ?? new List<StatisticsSnapshot>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PluginLogService.LogWarning("StatisticsHistory", $"Could not load statistics history from {_historyFilePath}", ex, _logger);
            return new List<StatisticsSnapshot>();
        }
    }
}
