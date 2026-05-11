using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    ///     Maximum allowed file size for the discovery cache file (50 MB).
    ///     Files exceeding this size are treated as corrupted and deleted.
    /// </summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _filePath;
    private readonly Lock _fileLock = new();
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<DiscoveryCacheService> _logger;

    /// <summary>
    ///     In-memory cache of discovery results. Avoids reading from disk on every API call.
    ///     Invalidated on <see cref="Save"/> and <see cref="MarkAsRequested"/>.
    ///     Typed as <c>List</c> (not <c>IReadOnlyList</c>) because <see cref="MarkAsRequested"/>
    ///     mutates individual recommendation items in-place.
    /// </summary>
    private List<DiscoveryResult>? _memoryCache;

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
    ///     Loads cached discovery results. Returns a snapshot of the in-memory cache if available,
    ///     otherwise reads from disk and populates the cache.
    ///     Callers receive a detached copy to prevent external mutation of cache state.
    /// </summary>
    /// <returns>The deserialized results, or an empty list if the file does not exist or is invalid.</returns>
    public IReadOnlyList<DiscoveryResult> Load()
    {
        lock (_fileLock)
        {
            if (_memoryCache != null)
            {
                return _memoryCache.AsReadOnly();
            }

            try
            {
                if (!File.Exists(_filePath))
                {
                    _memoryCache = [];
                    return _memoryCache.AsReadOnly();
                }

                // Hardening: reject oversized files (likely corrupted or tampered)
                var fileInfo = new FileInfo(_filePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    _pluginLog.LogWarning(
                        "DiscoveryCache",
                        $"Discovery cache file exceeds {MaxFileSizeBytes / (1024 * 1024)}MB ({fileInfo.Length} bytes). Deleting and returning empty.",
                        null,
                        _logger);
                    try
                    {
                        File.Delete(_filePath);
                    }
                    catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                    {
                        // Best effort
                    }

                    _memoryCache = [];
                    return _memoryCache.AsReadOnly();
                }

                var json = File.ReadAllText(_filePath);
                _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
                return _memoryCache.AsReadOnly();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not load discovery results from {_filePath}: {ex.Message}",
                    ex,
                    _logger);
                // Cache empty result to prevent repeated failed disk reads on every API call.
                // Next Save() will repopulate the cache with fresh data.
                _memoryCache = [];
                return _memoryCache.AsReadOnly();
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

                    // Hardening: reject oversized files (consistent with Load())
                    var fileInfo = new FileInfo(_filePath);
                    if (fileInfo.Length > MaxFileSizeBytes)
                    {
                        _pluginLog.LogWarning(
                            "DiscoveryCache",
                            $"Discovery cache file exceeds {MaxFileSizeBytes / (1024 * 1024)}MB ({fileInfo.Length} bytes). Deleting and returning.",
                            null,
                            _logger);
                        try
                        {
                            File.Delete(_filePath);
                        }
                        catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                        {
                            // Best effort
                        }

                        _memoryCache = [];
                        return;
                    }

                    var json = File.ReadAllText(_filePath);
                    _memoryCache = JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
                }

                if (_memoryCache.Count == 0)
                {
                    return;
                }

                // Determine which items need updating WITHOUT mutating the live cache yet.
                // This avoids leaving _memoryCache in an inconsistent state if persistence fails.
                var indicesToMark = new List<(int UserIdx, int RecIdx)>();
                for (var u = 0; u < _memoryCache.Count; u++)
                {
                    var recs = _memoryCache[u].Recommendations;
                    for (var r = 0; r < recs.Count; r++)
                    {
                        if (recs[r].TmdbId == tmdbId && !recs[r].AlreadyRequested)
                        {
                            indicesToMark.Add((u, r));
                        }
                    }
                }

                if (indicesToMark.Count > 0)
                {
                    // Apply mutations
                    foreach (var (userIdx, recIdx) in indicesToMark)
                    {
                        _memoryCache[userIdx].Recommendations[recIdx].AlreadyRequested = true;
                    }

                    var tempFilePath = _filePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                    try
                    {
                        var updatedJson = JsonSerializer.Serialize(_memoryCache, JsonOptions);
                        File.WriteAllText(tempFilePath, updatedJson);
                        File.Move(tempFilePath, _filePath, overwrite: true);
                    }
                    catch
                    {
                        // Rollback in-memory mutations on persistence failure
                        foreach (var (userIdx, recIdx) in indicesToMark)
                        {
                            _memoryCache[userIdx].Recommendations[recIdx].AlreadyRequested = false;
                        }

                        try
                        {
                            File.Delete(tempFilePath);
                        }
                        catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
                        {
                            // Best effort cleanup
                        }

                        throw;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "DiscoveryCache",
                    $"Could not mark TMDb#{tmdbId} as requested in cache: {ex.Message}",
                    ex,
                    _logger);

                // Match Load() behavior: prevent repeated failed disk reads on subsequent calls.
                _memoryCache ??= [];
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

                // Update in-memory cache to match persisted state.
                // Always create a detached copy — never alias the caller's list to prevent
                // external mutation bypassing _fileLock synchronization.
                _memoryCache = new List<DiscoveryResult>(results);

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