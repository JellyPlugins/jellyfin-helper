using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for Radarr and Sonarr integration.
/// Provides connection testing and library comparison.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/ArrIntegration")]
[Produces(MediaTypeNames.Application.Json)]
public class ArrIntegrationController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ArrIntegrationService _arrService;
    private readonly ILogger<ArrIntegrationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrIntegrationController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="arrService">The Arr integration service.</param>
    /// <param name="logger">The controller logger.</param>
    public ArrIntegrationController(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ArrIntegrationService arrService,
        ILogger<ArrIntegrationController> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _arrService = arrService;
        _logger = logger;
    }

    /// <summary>
    /// Tests the connection to an Arr instance (Radarr or Sonarr) using the provided URL and API key.
    /// </summary>
    /// <param name="request">The connection test request containing URL and API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure with a message.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> TestArrConnectionAsync([FromBody] ArrTestConnectionRequest request, CancellationToken cancellationToken)
    {
        var (success, message) = await _arrService.TestConnectionAsync(
            request.Url ?? string.Empty,
            request.ApiKey ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

        return Ok(new { success, message });
    }

    /// <summary>
    /// Compares a single configured Radarr instance (by index) with Jellyfin movie libraries.
    /// If no index is provided, merges all instances.
    /// </summary>
    /// <param name="index">Optional zero-based index of the Radarr instance to compare.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Compare/Radarr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareRadarrAsync([FromQuery] int? index, CancellationToken cancellationToken)
    {
        var config = CleanupConfigHelper.GetConfig();
        var instances = config.GetEffectiveRadarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Radarr instance must be configured." });
        }

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        var movieFolders = GetJellyfinFolderNames("movies");

        var allMovies = new List<ArrMovie>();
        var validInstanceCount = 0;
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            validInstanceCount++;
            var movies = await _arrService.GetRadarrMoviesAsync(instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
            allMovies.AddRange(movies);
        }

        if (validInstanceCount == 0)
        {
            return BadRequest(new { message = "No Radarr instance has both a URL and API key configured." });
        }

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(allMovies, movieFolders);
        return Ok(result);
    }

    /// <summary>
    /// Compares a single configured Sonarr instance (by index) with Jellyfin TV libraries.
    /// If no index is provided, merges all instances.
    /// </summary>
    /// <param name="index">Optional zero-based index of the Sonarr instance to compare.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Compare/Sonarr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareSonarrAsync([FromQuery] int? index, CancellationToken cancellationToken)
    {
        var config = CleanupConfigHelper.GetConfig();
        var instances = config.GetEffectiveSonarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Sonarr instance must be configured." });
        }

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        var tvFolders = GetJellyfinFolderNames("tvshows");

        var allSeries = new List<ArrSeries>();
        var validInstanceCount = 0;
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            validInstanceCount++;
            var series = await _arrService.GetSonarrSeriesAsync(instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);
            allSeries.AddRange(series);
        }

        if (validInstanceCount == 0)
        {
            return BadRequest(new { message = "No Sonarr instance has both a URL and API key configured." });
        }

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(allSeries, tvFolders);
        return Ok(result);
    }

    // === Private helpers ===

    /// <summary>
    /// Gets the set of top-level folder names for a given collection type from Jellyfin libraries.
    /// </summary>
    private HashSet<string> GetJellyfinFolderNames(string collectionType)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => string.Equals(f.CollectionType?.ToString(), collectionType, StringComparison.OrdinalIgnoreCase));

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var config = CleanupConfigHelper.GetConfig();
        var trashFolderName = config.TrashFolderPath;
        var isTrashRelative = !Path.IsPathRooted(trashFolderName);

        foreach (var folder in folders)
        {
            foreach (var location in folder.Locations)
            {
                try
                {
                    var dirs = _fileSystem.GetDirectories(location);
                    foreach (var dir in dirs)
                    {
                        if (isTrashRelative && string.Equals(dir.Name, trashFolderName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        result.Add(dir.Name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PluginLogService.LogWarning("API", $"Could not list directories in {location}", ex, _logger);
                }
            }
        }

        return result;
    }
}
