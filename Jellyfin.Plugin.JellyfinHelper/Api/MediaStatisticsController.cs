using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for media statistics with caching, rate limiting, export, history,
/// cleanup tracking, configuration, trash management, and Arr integration.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper")]
[Produces(MediaTypeNames.Application.Json)]
public class MediaStatisticsController : ControllerBase
{
    private const string StatsCacheKey = "JellyfinHelper_Statistics";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);

    private static readonly TimeSpan MinScanInterval = TimeSpan.FromSeconds(30);
    private static readonly object RateLimitLock = new();
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Simple in-memory rate limiting
    private static DateTime _lastScanTime = DateTime.MinValue;

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly MediaStatisticsService _statisticsService;
    private readonly StatisticsHistoryService _historyService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MediaStatisticsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaStatisticsController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="httpClientFactory">The HTTP client factory for Arr API requests.</param>
    /// <param name="cache">The memory cache.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="serviceLogger">The statistics service logger.</param>
    /// <param name="historyLogger">The history service logger.</param>
    public MediaStatisticsController(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<MediaStatisticsController> logger,
        ILogger<MediaStatisticsService> serviceLogger,
        ILogger<StatisticsHistoryService> historyLogger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _statisticsService = new MediaStatisticsService(libraryManager, fileSystem, serviceLogger);
        _historyService = new StatisticsHistoryService(applicationPaths, historyLogger);
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Gets media statistics for all libraries. Results are cached for 7 days.
    /// </summary>
    /// <param name="forceRefresh">Set to true to bypass the cache and force a fresh scan.</param>
    /// <returns>The media statistics.</returns>
    [HttpGet("Statistics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<MediaStatisticsResult> GetStatistics([FromQuery] bool forceRefresh = false)
    {
        // Try cache first (unless force refresh)
        if (!forceRefresh && _cache.TryGetValue(StatsCacheKey, out MediaStatisticsResult? cached) && cached != null)
        {
            _logger.LogDebug("Returning cached statistics");
            return Ok(cached);
        }

        // Rate limiting: prevent excessive scans
        lock (RateLimitLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastScanTime < MinScanInterval)
            {
                // Check cache again inside lock (another request might have populated it)
                if (_cache.TryGetValue(StatsCacheKey, out MediaStatisticsResult? recentCached) && recentCached != null)
                {
                    return Ok(recentCached);
                }

                _logger.LogWarning("Rate limit exceeded for statistics scan");
                return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Please wait before requesting another scan." });
            }

            _lastScanTime = now;
        }

        var result = _statisticsService.CalculateStatistics();

        // Cache the result
        _cache.Set(StatsCacheKey, result, CacheDuration);

        // Save snapshot for historical tracking
        try
        {
            _historyService.SaveSnapshot(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save statistics snapshot");
        }

        return Ok(result);
    }

    /// <summary>
    /// Exports the current statistics as a JSON file download.
    /// </summary>
    /// <returns>A JSON file containing the statistics.</returns>
    [HttpGet("Statistics/Export/Json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ExportJson()
    {
        var result = GetCachedOrCalculate();

        var json = JsonSerializer.Serialize(result, ExportJsonOptions);

        var bytes = Encoding.UTF8.GetBytes(json);
        var timestamp = result.ScanTimestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "application/json", $"jellyfin-statistics-{timestamp}.json");
    }

    /// <summary>
    /// Exports the current statistics as a CSV file download.
    /// </summary>
    /// <returns>A CSV file containing the per-library statistics.</returns>
    [HttpGet("Statistics/Export/Csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ExportCsv()
    {
        var result = GetCachedOrCalculate();

        var sb = new StringBuilder();
        sb.AppendLine("Library,CollectionType,VideoFiles,VideoSizeBytes,AudioFiles,AudioSizeBytes,SubtitleFiles,SubtitleSizeBytes,ImageFiles,ImageSizeBytes,NfoFiles,NfoSizeBytes,TrickplayFolders,TrickplaySizeBytes,OtherFiles,OtherSizeBytes,TotalSizeBytes");

        foreach (var lib in result.Libraries)
        {
            sb.AppendLine(string.Join(
                ",",
                EscapeCsv(lib.LibraryName),
                EscapeCsv(lib.CollectionType),
                lib.VideoFileCount,
                lib.VideoSize,
                lib.AudioFileCount,
                lib.AudioSize,
                lib.SubtitleFileCount,
                lib.SubtitleSize,
                lib.ImageFileCount,
                lib.ImageSize,
                lib.NfoFileCount,
                lib.NfoSize,
                lib.TrickplayFolderCount,
                lib.TrickplaySize,
                lib.OtherFileCount,
                lib.OtherSize,
                lib.TotalSize));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var timestamp = result.ScanTimestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return File(bytes, "text/csv", $"jellyfin-statistics-{timestamp}.csv");
    }

    /// <summary>
    /// Gets the historical statistics trend data.
    /// </summary>
    /// <returns>A list of historical snapshots.</returns>
    [HttpGet("Statistics/History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<StatisticsSnapshot>> GetHistory()
    {
        var history = _historyService.LoadHistory();
        return Ok(history);
    }

    // === Cleanup Statistics ===

    /// <summary>
    /// Gets the accumulated cleanup statistics (total bytes freed, items deleted, last cleanup time).
    /// </summary>
    /// <returns>The cleanup statistics.</returns>
    [HttpGet("Cleanup/Statistics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCleanupStatistics()
    {
        var (totalBytesFreed, totalItemsDeleted, lastCleanupTimestamp) = CleanupTrackingService.GetStatistics();
        return Ok(new
        {
            TotalBytesFreed = totalBytesFreed,
            TotalItemsDeleted = totalItemsDeleted,
            LastCleanupTimestamp = lastCleanupTimestamp,
        });
    }

    // === Configuration ===

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    /// <returns>The plugin configuration.</returns>
    [HttpGet("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        var config = CleanupConfigHelper.GetConfig();
        return Ok(config);
    }

    /// <summary>
    /// Updates the plugin configuration.
    /// </summary>
    /// <param name="updatedConfig">The updated configuration.</param>
    /// <returns>A status result.</returns>
    [HttpPost("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdateConfiguration([FromBody] PluginConfiguration updatedConfig)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest(new { message = "Plugin not initialized." });
        }

        // Validate
        if (updatedConfig.OrphanMinAgeDays < 0)
        {
            return BadRequest(new { message = "OrphanMinAgeDays must be >= 0." });
        }

        if (updatedConfig.TrashRetentionDays < 0)
        {
            return BadRequest(new { message = "TrashRetentionDays must be >= 0." });
        }

        // Preserve accumulated statistics (don't let the UI overwrite them)
        var currentConfig = plugin.Configuration;
        updatedConfig.TotalBytesFreed = currentConfig.TotalBytesFreed;
        updatedConfig.TotalItemsDeleted = currentConfig.TotalItemsDeleted;
        updatedConfig.LastCleanupTimestamp = currentConfig.LastCleanupTimestamp;

        plugin.UpdateConfiguration(updatedConfig);

        _logger.LogInformation("Plugin configuration updated.");
        return Ok(new { message = "Configuration saved." });
    }

    // === Trash Management ===

    /// <summary>
    /// Gets a summary of all trash folders across libraries.
    /// </summary>
    /// <returns>The trash summary.</returns>
    [HttpGet("Trash/Summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTrashSummary()
    {
        var libraryFolders = CleanupConfigHelper.GetFilteredLibraryLocations(_libraryManager);
        long totalSize = 0;
        int totalItems = 0;

        foreach (var folder in libraryFolders)
        {
            var trashPath = CleanupConfigHelper.GetTrashPath(folder);
            var (size, count) = TrashService.GetTrashSummary(trashPath);
            totalSize += size;
            totalItems += count;
        }

        return Ok(new
        {
            TotalSize = totalSize,
            TotalItems = totalItems,
        });
    }

    // === Arr Integration ===

    /// <summary>
    /// Compares all configured Radarr instances with Jellyfin movie libraries.
    /// Returns a merged result across all instances.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Arr/Radarr/Compare")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareRadarrAsync(CancellationToken cancellationToken)
    {
        var config = CleanupConfigHelper.GetConfig();
        var instances = config.GetEffectiveRadarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Radarr instance must be configured." });
        }

        var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
        var arrService = new ArrIntegrationService(httpClient, _logger);
        var movieFolders = GetJellyfinFolderNames("movies");

        var allMovies = new List<ArrMovie>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var movies = await arrService.GetRadarrMoviesAsync(instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
            allMovies.AddRange(movies);
        }

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(allMovies, movieFolders);
        return Ok(result);
    }

    /// <summary>
    /// Compares all configured Sonarr instances with Jellyfin TV libraries.
    /// Returns a merged result across all instances.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Arr/Sonarr/Compare")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareSonarrAsync(CancellationToken cancellationToken)
    {
        var config = CleanupConfigHelper.GetConfig();
        var instances = config.GetEffectiveSonarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Sonarr instance must be configured." });
        }

        var httpClient = _httpClientFactory.CreateClient("ArrIntegration");
        var arrService = new ArrIntegrationService(httpClient, _logger);
        var tvFolders = GetJellyfinFolderNames("tvshows");

        var allSeries = new List<ArrSeries>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var series = await arrService.GetSonarrSeriesAsync(instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
            allSeries.AddRange(series);
        }

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(allSeries, tvFolders);
        return Ok(result);
    }

    /// <summary>
    /// Gets the translation strings for the specified language (or the configured language).
    /// </summary>
    /// <param name="lang">Optional language code override. If not provided, uses the configured language.</param>
    /// <returns>A dictionary of translation keys to strings.</returns>
    [HttpGet("Translations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [AllowAnonymous]
    public ActionResult<Dictionary<string, string>> GetTranslations([FromQuery] string? lang = null)
    {
        var languageCode = lang ?? CleanupConfigHelper.GetConfig().Language;
        var translations = I18nService.GetTranslations(languageCode);
        return Ok(translations);
    }

    /// <summary>
    /// Gets the list of available library names for the configuration UI.
    /// </summary>
    /// <returns>A list of library names with their collection types.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLibraryNames()
    {
        var folders = _libraryManager.GetVirtualFolders();
        var result = folders.Select(f => new
        {
            f.Name,
            CollectionType = f.CollectionType?.ToString() ?? "Unknown",
            LocationCount = f.Locations.Length,
        }).ToList();

        return Ok(result);
    }

    // === Private helpers ===

    /// <summary>
    /// Returns cached statistics or calculates fresh ones.
    /// </summary>
    private MediaStatisticsResult GetCachedOrCalculate()
    {
        if (_cache.TryGetValue(StatsCacheKey, out MediaStatisticsResult? cached) && cached != null)
        {
            return cached;
        }

        var result = _statisticsService.CalculateStatistics();
        _cache.Set(StatsCacheKey, result, CacheDuration);
        return result;
    }

    /// <summary>
    /// Gets the set of top-level folder names for a given collection type from Jellyfin libraries.
    /// </summary>
    private HashSet<string> GetJellyfinFolderNames(string collectionType)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => string.Equals(f.CollectionType?.ToString(), collectionType, StringComparison.OrdinalIgnoreCase));

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var location in folder.Locations)
            {
                try
                {
                    var dirs = _fileSystem.GetDirectories(location, false);
                    foreach (var dir in dirs)
                    {
                        result.Add(dir.Name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not list directories in {Path}", location);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Escapes a value for CSV output.
    /// </summary>
    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}