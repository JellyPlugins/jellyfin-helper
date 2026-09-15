using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
///     Caches the latest full scan result to disk for persistence across restarts.
/// </summary>
public class StatisticsCacheService : IStatisticsCacheService
{
    private const string LatestResultFileName = "jellyfin-helper-statistics-latest.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly Lock _fileLock = new();

    private readonly string _latestResultFilePath;
    private readonly ILogger<StatisticsCacheService> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StatisticsCacheService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger.</param>
    public StatisticsCacheService(
        IApplicationPaths applicationPaths,
        IPluginLogService pluginLog,
        ILogger<StatisticsCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _pluginLog = pluginLog;
        _logger = logger;
        _latestResultFilePath = Path.Join(applicationPaths.DataPath, LatestResultFileName);
    }

    /// <summary>
    ///     Saves the latest full statistics result to disk for persistence across server restarts.
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

                // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
                AtomicFile.WriteAllText(_latestResultFilePath, json);

                _pluginLog.LogDebug(
                    "StatisticsCache",
                    $"Saved latest statistics result to {_latestResultFilePath}",
                    _logger);
            }

            // Broader filter than plain IOException / UnauthorizedAccessException because AtomicFile.WriteAllText can also surface SecurityException, NotSupportedException, ArgumentException (malformed path characters from OS layer), and JsonException (serializer).
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException
                                        or JsonException)
            {
                _pluginLog.LogWarning(
                    "StatisticsCache",
                    $"Could not save latest statistics result to {_latestResultFilePath}",
                    ex,
                    _logger);
            }
        }
    }

    /// <summary>
    ///     Loads the latest full statistics result from disk.
    /// </summary>
    /// <returns>The last saved statistics result, or null if none exists.</returns>
    public MediaStatisticsResult? LoadLatestResult()
    {
        try
        {
            string json;
            lock (_fileLock)
            {
                if (!File.Exists(_latestResultFilePath))
                {
                    return null;
                }

                json = File.ReadAllText(_latestResultFilePath);
            }

            var result = JsonSerializer.Deserialize<MediaStatisticsResult>(json, JsonOptions);
            if (result != null)
            {
                MigrateLegacyBitrateTiers(result);
                MigrateLegacyWatchedBuckets(result);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or System.Security.SecurityException
                                    or JsonException)
        {
            _pluginLog.LogWarning(
                "StatisticsCache",
                $"Could not load latest statistics result from {_latestResultFilePath}",
                ex,
                _logger);
            return null;
        }
    }

    private static void MigrateLegacyBitrateTiers(MediaStatisticsResult result)
    {
        foreach (var lib in result.Libraries)
        {
            MigrateIntDict(lib.VideoBitrateTiers);
            MigrateLongDict(lib.VideoBitrateTierSizes);
            MigratePathsDict(lib.VideoBitrateTierPaths);
        }
    }

    private static void MigrateIntDict(Dictionary<string, int> dict)
    {
        var legacyKeys = new List<string>();
        foreach (var key in dict.Keys)
        {
            if (key is "2-5 Mbps" or "5-10 Mbps" or "10-20 Mbps" or "20-40 Mbps" or "> 40 Mbps")
            {
                legacyKeys.Add(key);
            }
        }

        foreach (var old in legacyKeys)
        {
            if (!dict.TryGetValue(old, out var value))
            {
                continue;
            }

            dict.Remove(old);
            var migrated = MediaStatisticsService.MapLegacyBitrateTier(old);
            if (dict.TryGetValue(migrated, out var existing))
            {
                dict[migrated] = existing + value;
            }
            else
            {
                dict[migrated] = value;
            }
        }
    }

    private static void MigrateLongDict(Dictionary<string, long> dict)
    {
        var legacyKeys = new List<string>();
        foreach (var key in dict.Keys)
        {
            if (key is "2-5 Mbps" or "5-10 Mbps" or "10-20 Mbps" or "20-40 Mbps" or "> 40 Mbps")
            {
                legacyKeys.Add(key);
            }
        }

        foreach (var old in legacyKeys)
        {
            if (!dict.TryGetValue(old, out var value))
            {
                continue;
            }

            dict.Remove(old);
            var migrated = MediaStatisticsService.MapLegacyBitrateTier(old);
            if (dict.TryGetValue(migrated, out var existing))
            {
                dict[migrated] = existing + value;
            }
            else
            {
                dict[migrated] = value;
            }
        }
    }

    private static void MigratePathsDict(Dictionary<string, System.Collections.ObjectModel.Collection<string>> dict)
    {
        var legacyKeys = new List<string>();
        foreach (var key in dict.Keys)
        {
            if (key is "2-5 Mbps" or "5-10 Mbps" or "10-20 Mbps" or "20-40 Mbps" or "> 40 Mbps")
            {
                legacyKeys.Add(key);
            }
        }

        foreach (var old in legacyKeys)
        {
            if (!dict.TryGetValue(old, out var value))
            {
                continue;
            }

            dict.Remove(old);
            var migrated = MediaStatisticsService.MapLegacyBitrateTier(old);
            if (dict.TryGetValue(migrated, out var existing))
            {
                foreach (var p in value)
                {
                    existing.Add(p);
                }
            }
            else
            {
                dict[migrated] = value;
            }
        }
    }

    private static void MigrateLegacyWatchedBuckets(MediaStatisticsResult result)
    {
        foreach (var lib in result.Libraries)
        {
            MigrateWatchedBuckets(lib.WatchedTiers, lib.WatchedTierPaths, lib.WatchedTierSizes);
        }
    }

    private static void MigrateWatchedBuckets(
        Dictionary<string, int> tiers,
        Dictionary<string, Collection<string>> tierPaths,
        Dictionary<string, long> tierSizes)
    {
        var legacyKeys = new[] { "1 user", "2–3 users", "4+ users", "2-3 users" };
        var hasLegacy = false;
        foreach (var k in legacyKeys)
        {
            if (tiers.ContainsKey(k) || tierPaths.ContainsKey(k) || tierSizes.ContainsKey(k))
            {
                hasLegacy = true;
                break;
            }
        }

        if (!hasLegacy)
        {
            return;
        }

        var watchedCount = 0;
        var watchedPaths = new Collection<string>();
        long watchedSize = 0;

        foreach (var k in legacyKeys)
        {
            if (tiers.TryGetValue(k, out var c))
            {
                watchedCount += c;
                tiers.Remove(k);
            }

            if (tierPaths.TryGetValue(k, out var paths))
            {
                foreach (var p in paths)
                {
                    watchedPaths.Add(p);
                }

                tierPaths.Remove(k);
            }

            if (tierSizes.TryGetValue(k, out var s))
            {
                watchedSize += s;
                tierSizes.Remove(k);
            }
        }

        if (watchedCount > 0)
        {
            tiers["Watched"] = tiers.TryGetValue("Watched", out var existing) ? existing + watchedCount : watchedCount;
        }

        if (watchedPaths.Count > 0)
        {
            if (tierPaths.TryGetValue("Watched", out var existingPaths))
            {
                foreach (var p in watchedPaths)
                {
                    existingPaths.Add(p);
                }
            }
            else
            {
                tierPaths["Watched"] = watchedPaths;
            }
        }

        if (watchedSize > 0)
        {
            tierSizes["Watched"] = tierSizes.TryGetValue("Watched", out var existingSize) ? existingSize + watchedSize : watchedSize;
        }
    }
}