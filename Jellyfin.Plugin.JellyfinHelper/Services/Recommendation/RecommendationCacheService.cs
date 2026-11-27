using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Persists recommendation results to disk (JSON) following the same pattern
///     as <see cref="Statistics.StatisticsCacheService" />.
/// </summary>
public sealed class RecommendationCacheService : IRecommendationCacheService
{
    private const string LogSource = "RecommendationCache";

    private const string CacheFileName = "jellyfin-helper-recommendations-latest.json";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private readonly string _cacheFilePath;
    private readonly Lock _fileLock = new();
    private readonly ILogger<RecommendationCacheService> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ILibraryManager? _libraryManager;
    private readonly IPluginConfigurationService? _configService;
    private readonly VirtualItemDetector? _virtualItems;
    private readonly IUserManager? _userManager;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RecommendationCacheService" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="libraryManager">Live library identities for current cache eligibility.</param>
    /// <param name="configService">Current excluded-library configuration.</param>
    /// <param name="virtualItems">Shared virtual-media detector.</param>
    /// <param name="userManager">Current account availability and permissions.</param>
    public RecommendationCacheService(
        IApplicationPaths applicationPaths,
        IPluginLogService pluginLog,
        ILogger<RecommendationCacheService> logger,
        ILibraryManager? libraryManager = null,
        IPluginConfigurationService? configService = null,
        VirtualItemDetector? virtualItems = null,
        IUserManager? userManager = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _pluginLog = pluginLog;
        _logger = logger;
        _cacheFilePath = Path.Join(applicationPaths.DataPath, CacheFileName);
        _libraryManager = libraryManager;
        _configService = configService;
        _virtualItems = virtualItems;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public void SaveResults(IReadOnlyList<RecommendationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        string json;
        try
        {
            json = JsonSerializer.Serialize(results, JsonOptions);
        }
        catch (JsonException ex)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Could not serialize recommendation results for {_cacheFilePath}",
                ex,
                _logger);
            return;
        }

        lock (_fileLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Use AtomicFile so a transient Windows AV/indexer sharing violation on the final File.Move gets a bounded retry instead of silently dropping the save.
                AtomicFile.WriteAllText(_cacheFilePath, json);

                _pluginLog.LogDebug(
                    LogSource,
                    $"Saved {results.Count} recommendation results to {_cacheFilePath}",
                    _logger);
            }

            // Broader filter than plain IOException / UnauthorizedAccessException / JsonException because AtomicFile.WriteAllText can also surface SecurityException, NotSupportedException and ArgumentException (malformed path characters from OS layer).
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or JsonException
                                        or System.Security.SecurityException
                                        or NotSupportedException
                                        or ArgumentException)
            {
                _pluginLog.LogWarning(
                    LogSource,
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
                    LogSource,
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
                    LogSource,
                    $"Cache file {_cacheFilePath} deserialized to null.",
                    logger: _logger);
            }

            return FilterCurrentLibraryScope(results);
        }
        catch (JsonException ex)
        {
            _pluginLog.LogWarning(
                LogSource,
                $"Could not load recommendation results from {_cacheFilePath}",
                ex,
                _logger);
            return null;
        }
    }

    private IReadOnlyList<RecommendationResult>? FilterCurrentLibraryScope(IReadOnlyList<RecommendationResult>? results)
    {
        if (results is null || _libraryManager is null)
        {
            return results;
        }

        try
        {
            var excluded = CleanupConfigHelper.ParseCommaSeparated(_configService?.GetConfiguration().ExcludedLibraries ?? string.Empty);
            var scope = excluded.Count > 0 ? LibraryPathResolver.GetLibraryRootScope(_libraryManager, excluded) : null;
            var ids = results.SelectMany(ResultItemIds).Distinct();
            var allowed = ids.Where(id => _libraryManager.GetItemById(id) is { } item && !item.IsVirtualItem && _virtualItems?.IsVirtual(item) != true
                && (scope is null || LibraryPathResolver.IsAllowed(item.Path, scope))).ToHashSet();
            var scoped = TrainingService.ScopePreviousResults(results, allowed);
            if (_userManager is null)
            {
                return scoped;
            }

            var visible = new List<RecommendationResult>();
            foreach (var result in scoped)
            {
                var user = _userManager.GetUserById(result.UserId);
                if (user is null || user.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled))
                {
                    continue;
                }

                var requested = ResultItemIds(result).Distinct().ToArray();
                var permitted = LibraryUserAccess.GetAllowedIds(_libraryManager, user, requested);
                visible.AddRange(TrainingService.ScopePreviousResults([result], permitted));
            }

            return visible;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, "Current library eligibility for cached recommendations is unavailable.", ex, _logger);
            return null;
        }
    }

    private static IEnumerable<Guid> ResultItemIds(RecommendationResult result)
        => result.Recommendations.Concat(result.TrainingContrasts ?? []).Select(item => item.ItemId)
            .Concat((result.RankingDiagnostics?.Entries ?? []).Select(entry => entry.ItemId));
}
