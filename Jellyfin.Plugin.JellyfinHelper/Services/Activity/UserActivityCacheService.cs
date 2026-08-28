using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Activity;

/// <summary>
///     Persists user activity results to disk (JSON) following the same pattern as RecommendationCacheService.
/// </summary>
public sealed class UserActivityCacheService : IUserActivityCacheService
{
    private const string LogSource = "UserActivityCache";

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
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _pluginLog = pluginLog;
        _logger = logger;
        _cacheFilePath = Path.Join(applicationPaths.DataPath, CacheFileName);
    }

    /// <inheritdoc />
    public void SaveResult(UserActivityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

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

                // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
                AtomicFile.WriteAllText(_cacheFilePath, json);

                _pluginLog.LogDebug(
                    LogSource,
                    $"Saved activity result with {result.TotalItemsWithActivity} items to {_cacheFilePath}",
                    _logger);
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException
                                        or JsonException)
            {
                _pluginLog.LogWarning(
                    LogSource,
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
                var result = JsonSerializer.Deserialize<UserActivityResult>(json, JsonOptions);
                if (result is null)
                {
                    _pluginLog.LogWarning(LogSource, $"Cache file {_cacheFilePath} deserialized to null.", logger: _logger);
                }

                return result;
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException
                                        or JsonException)
            {
                _pluginLog.LogWarning(
                    LogSource,
                    $"Could not load activity result from {_cacheFilePath}",
                    ex,
                    _logger);
                return null;
            }
        }
    }
}
