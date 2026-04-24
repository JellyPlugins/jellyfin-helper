using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Persists recommendation results to disk (JSON) following the same pattern
///     as <see cref="Statistics.StatisticsCacheService" />.
/// </summary>
public sealed class RecommendationCacheService : IRecommendationCacheService
{
    private const string CacheFileName = "jellyfin-helper-recommendations-latest.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _cacheFilePath;
    private readonly Lock _fileLock = new();
    private readonly ILogger<RecommendationCacheService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationCacheService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public RecommendationCacheService(
        IApplicationPaths applicationPaths,
        IPluginLogService pluginLog,
        ILogger<RecommendationCacheService> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;
        _cacheFilePath = Path.Join(applicationPaths.DataPath, CacheFileName);
    }

    /// <inheritdoc />
    public void SaveResults(IReadOnlyList<RecommendationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        lock (_fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(results, JsonOptions);
                var tempFilePath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempFilePath, json);
                File.Move(tempFilePath, _cacheFilePath, true);

                _pluginLog.LogDebug(
                    "RecommendationCache",
                    $"Saved {results.Count} recommendation results to {_cacheFilePath}",
                    _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(_cacheFilePath + ".tmp");
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }

                _pluginLog.LogWarning(
                    "RecommendationCache",
                    $"Could not save recommendation results to {_cacheFilePath}",
                    ex,
                    _logger);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecommendationResult>? LoadResults()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_cacheFilePath);
                var results = JsonSerializer.Deserialize<List<RecommendationResult>>(json, JsonOptions);
                if (results is null)
                {
                    _pluginLog.LogWarning(
                        "RecommendationCache",
                        $"Cache file {_cacheFilePath} deserialized to null.",
                        logger: _logger);
                }

                return results;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "RecommendationCache",
                    $"Could not load recommendation results from {_cacheFilePath}",
                    ex,
                    _logger);
                return null;
            }
        }
    }
}