using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Persists user activity results to disk (JSON) following the same pattern
///     as <see cref="Recommendation.RecommendationCacheService" />.
///     Cache is refreshed each time the scheduled task runs.
/// </summary>
public class UserActivityCacheService : IUserActivityCacheService
{
    private const string CacheFileName = "jellyfin-helper-useractivity-latest.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _cacheFilePath;
    private readonly Lock _fileLock = new();
    private readonly ILogger<UserActivityCacheService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UserActivityCacheService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    public UserActivityCacheService(
        IApplicationPaths applicationPaths,
        IPluginLogService pluginLog,
        ILogger<UserActivityCacheService> logger)
    {
        _pluginLog = pluginLog;
        _logger = logger;
        _cacheFilePath = Path.Join(applicationPaths.DataPath, CacheFileName);
    }

    /// <inheritdoc />
    public void SaveResult(UserActivityResult result)
    {
        lock (_fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(result, JsonOptions);
                var tempFilePath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempFilePath, json);
                File.Move(tempFilePath, _cacheFilePath, true);

                _pluginLog.LogDebug(
                    "UserActivityCache",
                    $"Saved activity result with {result.TotalItemsWithActivity} items to {_cacheFilePath}",
                    _logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "UserActivityCache",
                    $"Could not save activity result to {_cacheFilePath}",
                    ex,
                    _logger);
            }
        }
    }

    /// <inheritdoc />
    public UserActivityResult? LoadResult()
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
                return JsonSerializer.Deserialize<UserActivityResult>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _pluginLog.LogWarning(
                    "UserActivityCache",
                    $"Could not load activity result from {_cacheFilePath}",
                    ex,
                    _logger);
                return null;
            }
        }
    }
}