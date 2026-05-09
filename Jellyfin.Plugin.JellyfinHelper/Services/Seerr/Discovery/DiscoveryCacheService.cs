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
    ///     Loads cached discovery results from disk.
    /// </summary>
    /// <returns>The deserialized results, or an empty list if the file does not exist or is invalid.</returns>
    public IReadOnlyList<DiscoveryResult> Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return [];
                }

                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<DiscoveryResult>>(json, JsonOptions) ?? [];
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