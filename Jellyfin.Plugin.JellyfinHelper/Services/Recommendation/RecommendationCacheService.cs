using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
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

                // Use AtomicFile so a transient Windows AV/indexer sharing violation on the
                // final File.Move gets a bounded retry instead of silently dropping the save.
                // AtomicFile also handles temp-file cleanup internally.
                AtomicFile.WriteAllText(_cacheFilePath, json);

                _pluginLog.LogDebug(
                    "RecommendationCache",
                    $"Saved {results.Count} recommendation results to {_cacheFilePath}",
                    _logger);
            }

            // Broader filter than plain IOException / UnauthorizedAccessException / JsonException
            // because AtomicFile.WriteAllText can also surface SecurityException,
            // NotSupportedException and ArgumentException (malformed path characters from OS layer).
            // Best-effort save must degrade gracefully for every one of those rather than crashing
            // the scheduled task. Matches the filter used in StatisticsCacheService.
            //
            // Not covered by unit tests: triggering SecurityException / NotSupportedException
            // reliably in-process requires filesystem edge cases (locked-down user accounts,
            // exotic path syntax on non-Windows) that a portable xUnit run cannot reproduce.
            // The handler body is intentionally identical to the IOException/JsonException
            // path (log + swallow, no state mutation) so all six exception types share the
            // same code path — extending the filter cannot introduce a new failure mode.
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or JsonException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException)
            {
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
        string json;
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return null;
                }

                json = File.ReadAllText(_cacheFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "RecommendationCache",
                    $"Could not load recommendation results from {_cacheFilePath}",
                    ex,
                    _logger);
                return null;
            }
        }

        try
        {
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
        catch (JsonException ex)
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