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
///     API controller for Radarr and Sonarr integration.
///     Provides connection testing and library comparison.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/ArrIntegration")]
[Produces(MediaTypeNames.Application.Json)]
public class ArrIntegrationController : ControllerBase
{
    private readonly IArrIntegrationService _arrService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArrIntegrationController> _logger;
    private readonly IPluginLogService _pluginLog;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ArrIntegrationController" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="arrService">The Arr integration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    public ArrIntegrationController(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IArrIntegrationService arrService,
        IPluginLogService pluginLog,
        ILogger<ArrIntegrationController> logger,
        ICleanupConfigHelper configHelper)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
    }

    /// <summary>
    ///     Tests the connection to an Arr instance (Radarr or Sonarr) using the provided URL and API key.
    /// </summary>
    /// <param name="request">The connection test request containing URL and API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure with a message.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ConnectionTestResponse), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> TestArrConnectionAsync(
        [FromBody] ArrTestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var url = request.Url ?? string.Empty;

        // Scheme guard only: reject non-HTTP(S) schemes (file://, ftp://, ...). We deliberately do
        // NOT block loopback/private/link-local hosts here: Radarr/Sonarr almost always run on the
        // same host or LAN as Jellyfin (localhost:7878, 192.168.x.y), so an internal-IP block would
        // break the plugin's primary legitimate configuration. The endpoint is admin-only, does not
        // follow redirects, caps the response size, and never reflects the response body — so the
        // residual "internal reachability oracle" risk is low and accepted for a LAN-integration tool.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A valid HTTP(S) URL is required." });
        }

        var (success, message) = await _arrService.TestConnectionAsync(
            parsedUrl.AbsoluteUri,
            request.ApiKey ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            // Log the detailed upstream message server-side but return a GENERIC one to the client,
            // so the endpoint cannot be used to distinguish internal host/port states (reachability
            // oracle). The generic string matches the Seerr connection-test for consistency.
            _pluginLog.LogWarning("API", $"Arr connection test failed: {message}", logger: _logger);
            return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = "Connection failed. Please verify URL and API Key and try again." });
        }

        return Ok(new ConnectionTestResponse { Success = true, Message = message });
    }

    /// <summary>
    ///     Compares a single configured Radarr instance (by index) with Jellyfin movie libraries.
    ///     If no index is provided, merges all instances.
    /// </summary>
    /// <param name="index">Optional zero-based index of the Radarr instance to compare.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Compare/Radarr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareRadarrAsync(
        [FromQuery] int? index,
        CancellationToken cancellationToken)
    {
        var config = _configHelper.GetConfig();
        var instances = config.GetEffectiveRadarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Radarr instance must be configured." });
        }

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(
                    new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        var movieFolders = GetJellyfinFolderNames("movies");

        var allMovies = new List<ArrMovie>();
        var failedInstances = new List<string>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var movies = await _arrService.GetRadarrMoviesAsync(instance.Url, instance.ApiKey, cancellationToken)
                .ConfigureAwait(false);
            if (movies is null)
            {
                failedInstances.Add(instance.Name);
            }
            else
            {
                allMovies.AddRange(movies);
            }
        }

        if (failedInstances.Count > 0)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    message = $"Failed to fetch data from Radarr instance(s): {string.Join(", ", failedInstances)}"
                });
        }

        var result = ArrIntegrationService.CompareRadarrWithJellyfin(allMovies, movieFolders);
        return Ok(result);
    }

    /// <summary>
    ///     Compares a single configured Sonarr instance (by index) with Jellyfin TV libraries.
    ///     If no index is provided, merges all instances.
    /// </summary>
    /// <param name="index">Optional zero-based index of the Sonarr instance to compare.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison result.</returns>
    [HttpGet("Compare/Sonarr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrComparisonResult>> CompareSonarrAsync(
        [FromQuery] int? index,
        CancellationToken cancellationToken)
    {
        var config = _configHelper.GetConfig();
        var instances = config.GetEffectiveSonarrInstances();

        if (instances.Count == 0)
        {
            return BadRequest(new { message = "At least one Sonarr instance must be configured." });
        }

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(
                    new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        var tvFolders = GetJellyfinFolderNames("tvshows");

        var allSeries = new List<ArrSeries>();
        var failedInstances = new List<string>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var series = await _arrService.GetSonarrSeriesAsync(instance.Url, instance.ApiKey, cancellationToken)
                .ConfigureAwait(false);
            if (series is null)
            {
                failedInstances.Add(instance.Name);
            }
            else
            {
                allSeries.AddRange(series);
            }
        }

        if (failedInstances.Count > 0)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    message = $"Failed to fetch data from Sonarr instance(s): {string.Join(", ", failedInstances)}"
                });
        }

        var result = ArrIntegrationService.CompareSonarrWithJellyfin(allSeries, tvFolders);
        return Ok(result);
    }

    // === Private helpers ===

    /// <summary>
    ///     Gets the set of top-level folder names for a given collection type from Jellyfin libraries.
    ///     Deduplication is by folder name only (not full path), using a case-insensitive HashSet.
    ///     Known limitation: if two library locations contain different directories that share the same
    ///     name (e.g. "/movies/Action" and "/archive/Action"), only one entry is kept. This is intentional
    ///     for matching against Arr titles, which are also name-based, but may cause missed matches when
    ///     the same folder name appears across different library types or root paths.
    /// </summary>
    private HashSet<string> GetJellyfinFolderNames(string collectionType)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => string.Equals(
                f.CollectionType?.ToString(),
                collectionType,
                StringComparison.OrdinalIgnoreCase));

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            foreach (var location in folder.Locations)
            {
                var trashFullPath = _configHelper.GetTrashPath(location);

                try
                {
                    var dirs = _fileSystem.GetDirectories(location);
                    foreach (var dir in dirs)
                    {
                        // Skip trash directory by comparing the full resolved path
                        var normalizedDir = dir.FullName.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                        var normalizedTrash = trashFullPath.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar);
                        if (string.Equals(normalizedDir, normalizedTrash, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        result.Add(dir.Name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _pluginLog.LogWarning("API", $"Could not list directories in {location}", ex, _logger);
                }
            }
        }

        return result;
    }
}