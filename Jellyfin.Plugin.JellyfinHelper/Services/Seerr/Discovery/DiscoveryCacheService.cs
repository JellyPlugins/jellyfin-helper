using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Handles persistence of discovery results to the plugin data directory.
///     Mirrors the pattern used by <see cref="Recommendation.RecommendationCacheService"/>.
/// </summary>
public sealed class DiscoveryCacheService
{
    private const string FileName = "jellyfin-helper-discovery-results.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryCacheService> _logger;

    /// <summary>
    ///     In-memory cache of discovery results. Avoids reading from disk on every API call.
    ///     Invalidated on <see cref="Save"/> and <see cref="MarkAsRequested"/>.
    /// </summary>
    private IReadOnlyList<DiscoveryResult>? _memoryCache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiscoveryCacheService"/> class.
    /// </summary>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public DiscoveryCacheService(
        IPluginLogService pluginLog,
        ILogger<DiscoveryCacheService> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;

        var dataPath = Plugin.Instance?.DataFolderPath ?? string.Empty;
        _filePath = Path.Join(dataPath, FileName);
    }

    /// <summary>
    ///     Loads cached discovery results. Returns the in-memory cache if available,
    ///     otherwise reads from disk and populates the cache.
    /// </summary>
    /// <returns>The deserialized results, or an empty list if the file does not exist or is invalid.</returns>
    public IReadOnlyList<DiscoveryResult> Load()
    {
        lock (_fileLock)
        {
            if (_memoryCache != null)
            {
                return _memoryCache;
            }

            try
            {
                if (!File.Exists(_filePath))
                {
                    _memoryCache = [];
                    return _memoryCache;
                }

                var json = File.ReadAllText(_filePath);
                _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
                return _memoryCache;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not load discovery results from {_filePath}: {ex.Message}",
                    ex,
                    _logger);
                return [];
            }
        }
    }

    /// <summary>
    ///     Marks a specific TMDb item as already requested in the cached results.
    ///     Updates both the in-memory cache and the on-disk file.
    /// </summary>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    public void MarkAsRequested(int tmdbId)
    {
        lock (_fileLock)
        {
            try
            {
                // Ensure in-memory cache is populated
                if (_memoryCache == null)
                {
                    if (!File.Exists(_filePath))
                    {
                        return;
                    }

                    var json = File.ReadAllText(_filePath);
                    _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
                }

                if (_memoryCache.Count == 0)
                {
                    return;
                }

                var modified = false;
                foreach (var userResult in _memoryCache)
                {
                    foreach (var rec in userResult.Recommendations)
                    {
                        if (rec.TmdbId == tmdbId && !rec.AlreadyRequested)
                        {
                            rec.AlreadyRequested = true;
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    var updatedJson = JsonSerializer.Serialize(_memoryCache, JsonOptions);
                    File.WriteAllText(_filePath, updatedJson);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not mark TMDb#{tmdbId} as requested in cache: {ex.Message}",
                    ex,
                    _logger);
            }
        }
    }

    /// <summary>
    ///     Saves discovery results to disk using atomic write (temp file + move).
    /// </summary>
    /// <param name="results">The results to persist.</param>
    public void Save(IReadOnlyList<DiscoveryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        lock (_fileLock)
        {
            var tempFilePath = _filePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(results, JsonOptions);
                File.WriteAllText(tempFilePath, json);
                File.Move(tempFilePath, _filePath, overwrite: true);

                // Update in-memory cache to match persisted state
                _memoryCache = results;

                _pluginLog.LogDebug(
                    "DiscoveryCache",
                    $"Saved {results.Count} discovery results to {_filePath}",
                    _logger);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                {
                    // Best effort cleanup
                }

                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not save discovery results to {_filePath}: {ex.Message}",
                    ex,
                    _logger);
            }
        }
    }
}