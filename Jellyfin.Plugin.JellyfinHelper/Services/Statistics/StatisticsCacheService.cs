using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private const string WatchedBucket = "Watched";
    private const string LogCategory = "StatisticsCache";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private static readonly string[] _legacyWatchedKeys = ["1 user", "2–3 users", "4+ users", "2-3 users"];
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
        string json;
        try
        {
            // Serialize outside the lock: big results take a while and concurrent
            // readers must not wait for string building. The payload is complete
            // either way, so last move wins without torn reads (atomic replace).
            json = JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (JsonException ex)
        {
            _pluginLog.LogWarning(
                LogCategory,
                $"Could not save latest statistics result to {_latestResultFilePath}",
                ex,
                _logger);
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_latestResultFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Use AtomicFile so a transient sharing violation on the final File.Move (typical when an AV scanner or the Search indexer briefly holds the file handle) gets a bounded retry with backoff.
                AtomicFile.WriteAllText(_latestResultFilePath, json);

                _pluginLog.LogDebug(
                    LogCategory,
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
                    LogCategory,
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
                RehydrateLibraryUnion(result);
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
                LogCategory,
                $"Could not load latest statistics result from {_latestResultFilePath}",
                ex,
                _logger);
            return null;
        }
    }

    private static void RehydrateLibraryUnion(MediaStatisticsResult result)
    {
        // Libraries is excluded from the payload (every library already serializes once
        // inside its typed group), so a fresh payload always arrives with an empty union.
        // Rebuild it so object-model consumers keep working. LibraryOrder restores exact
        // scan order; payloads predating it (or with unresolvable names) fall back to
        // grouped order, with stragglers appended.
        if (result.Libraries.Count > 0)
        {
            return;
        }

        var byName = new Dictionary<string, LibraryStatistics>(StringComparer.Ordinal);
        foreach (var lib in result.Movies.Concat(result.TvShows).Concat(result.Music)
                     .Concat(result.Books).Concat(result.Other))
        {
            byName.TryAdd(lib.LibraryName, lib);
        }

        foreach (var name in result.LibraryOrder)
        {
            if (byName.Remove(name, out var lib))
            {
                result.Libraries.Add(lib);
            }
        }

        foreach (var lib in result.Movies.Concat(result.TvShows).Concat(result.Music)
                     .Concat(result.Books).Concat(result.Other))
        {
            if (!result.Libraries.Contains(lib))
            {
                result.Libraries.Add(lib);
            }
        }
    }

    private static void MigrateLegacyBitrateTiers(MediaStatisticsResult result)
    {
        // Category collections deserialize into separate instances (no reference
        // preservation), so every collection needs the migration, not just Libraries.
        // Re-running on an already-migrated instance is a no-op: new labels pass through.
        foreach (var lib in result.Libraries.Concat(result.Movies).Concat(result.TvShows)
                     .Concat(result.Music).Concat(result.Books).Concat(result.Other))
        {
            MigrateIntDict(lib.VideoBitrateTiers);
            MigrateLongDict(lib.VideoBitrateTierSizes);
            MigratePathsDict(lib.VideoBitrateTierPaths);
        }
    }

    private static bool IsLegacyBitrateTier(string key) =>
        key is "2-5 Mbps" or "5-10 Mbps" or "10-20 Mbps" or "20-40 Mbps" or "> 40 Mbps";

    private static void MigrateIntDict(Dictionary<string, int> dict) =>
        MigrateDict(dict, static (existing, value) => existing + value);

    private static void MigrateLongDict(Dictionary<string, long> dict) =>
        MigrateDict(dict, static (existing, value) => existing + value);

    private static void MigratePathsDict(Dictionary<string, System.Collections.ObjectModel.Collection<string>> dict) =>
        MigrateDict(
            dict,
            static (existing, value) =>
            {
                foreach (var p in value)
                {
                    existing.Add(p);
                }

                return existing;
            });

    private static void MigrateDict<TValue>(Dictionary<string, TValue> dict, Func<TValue, TValue, TValue> merge)
    {
        var legacyKeys = dict.Keys.Where(IsLegacyBitrateTier).ToList();

        foreach (var old in legacyKeys)
        {
            if (!dict.TryGetValue(old, out var value))
            {
                continue;
            }

            dict.Remove(old);
            var migrated = MediaStatisticsService.MapLegacyBitrateTier(old);
            dict[migrated] = dict.TryGetValue(migrated, out var existing) ? merge(existing, value) : value;
        }
    }

    private static void MigrateLegacyWatchedBuckets(MediaStatisticsResult result)
    {
        // Same as bitrate above: every category collection deserializes separately.
        foreach (var lib in result.Libraries.Concat(result.Movies).Concat(result.TvShows)
                     .Concat(result.Music).Concat(result.Books).Concat(result.Other))
        {
            MigrateWatchedBuckets(lib.WatchedTiers, lib.WatchedTierPaths, lib.WatchedTierSizes);
        }
    }

    private static void MigrateWatchedBuckets(
        Dictionary<string, int> tiers,
        Dictionary<string, Collection<string>> tierPaths,
        Dictionary<string, long> tierSizes)
    {
        if (!_legacyWatchedKeys.Any(k => tiers.ContainsKey(k) || tierPaths.ContainsKey(k) || tierSizes.ContainsKey(k)))
        {
            return;
        }

        var watchedCount = 0;
        var watchedPaths = new Collection<string>();
        long watchedSize = 0;

        foreach (var k in _legacyWatchedKeys)
        {
            DrainWatchedBucket(k, tiers, tierPaths, tierSizes, ref watchedCount, watchedPaths, ref watchedSize);
        }

        StoreWatchedBucket(tiers, tierPaths, tierSizes, watchedCount, watchedPaths, watchedSize);
    }

    private static void StoreWatchedBucket(
        Dictionary<string, int> tiers,
        Dictionary<string, Collection<string>> tierPaths,
        Dictionary<string, long> tierSizes,
        int watchedCount,
        Collection<string> watchedPaths,
        long watchedSize)
    {
        if (watchedCount > 0)
        {
            tiers[WatchedBucket] = tiers.TryGetValue(WatchedBucket, out var existing) ? existing + watchedCount : watchedCount;
        }

        if (watchedPaths.Count > 0)
        {
            if (tierPaths.TryGetValue(WatchedBucket, out var existingPaths))
            {
                foreach (var p in watchedPaths)
                {
                    existingPaths.Add(p);
                }
            }
            else
            {
                tierPaths[WatchedBucket] = watchedPaths;
            }
        }

        if (watchedSize > 0)
        {
            tierSizes[WatchedBucket] = tierSizes.TryGetValue(WatchedBucket, out var existingSize) ? existingSize + watchedSize : watchedSize;
        }
    }

    private static void DrainWatchedBucket(
        string key,
        Dictionary<string, int> tiers,
        Dictionary<string, Collection<string>> tierPaths,
        Dictionary<string, long> tierSizes,
        ref int watchedCount,
        Collection<string> watchedPaths,
        ref long watchedSize)
    {
        if (tiers.TryGetValue(key, out var c))
        {
            watchedCount += c;
            tiers.Remove(key);
        }

        if (tierPaths.TryGetValue(key, out var paths))
        {
            foreach (var p in paths)
            {
                watchedPaths.Add(p);
            }

            tierPaths.Remove(key);
        }

        if (tierSizes.TryGetValue(key, out var s))
        {
            watchedSize += s;
            tierSizes.Remove(key);
        }
    }
}