using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
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
    private readonly ISecretProtector _secretProtector;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ArrIntegrationController" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="arrService">The Arr integration service.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="secretProtector">Decrypts stored Arr API keys before outbound calls.</param>
    public ArrIntegrationController(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IArrIntegrationService arrService,
        IPluginLogService pluginLog,
        ILogger<ArrIntegrationController> logger,
        ICleanupConfigHelper configHelper,
        ISecretProtector secretProtector)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
        _secretProtector = secretProtector;
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
        ArgumentNullException.ThrowIfNull(request);

        var url = request.Url ?? string.Empty;

        // Scheme guard only. Loopback/private hosts not blocked: Arr typically runs on LAN and
        // the endpoint is admin-only without redirects, so the residual oracle risk is accepted.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A valid HTTP(S) URL is required." });
        }

        // Block well-known cloud metadata endpoints (AWS/Azure IMDS, GCP, Alibaba).
        if (SsrfGuard.IsCloudMetadataHost(parsedUrl.Host))
        {
            _pluginLog.LogWarning("API", $"Blocked connection test to cloud metadata endpoint: {parsedUrl.Host}", logger: _logger);
            return BadRequest(new ConnectionTestResponse { Success = false, Message = "A valid HTTP(S) URL is required." });
        }

        // Resolve the masked-key sentinel to the real stored key BEFORE the live call. When the admin opens Settings after a reload, the API-key input is pre-filled with the fixed-length mask (the real key never leaves the server).
        var apiKey = request.ApiKey ?? string.Empty;
        if (ApiKeyMaskResolver.IsMask(apiKey))
        {
            var config = _configHelper.GetConfig();
            var storedInstances = (config.RadarrInstances ?? [])
                .Concat(config.SonarrInstances ?? []);
            apiKey = ApiKeyMaskResolver.ResolveArrKey(request.ApiKey, request.Url, request.Name, storedInstances);

            // Mask sent but no stored instance matches this URL/Name. Do NOT forward the mask upstream (it would always fail with a misleading 401).
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _pluginLog.LogWarning("API", "Arr connection test received the masked key sentinel but no stored instance matched the URL/Name; cannot resolve a real key.", logger: _logger);
                return StatusCode(StatusCodes.Status502BadGateway, new ConnectionTestResponse { Success = false, Message = "Connection failed. Please verify URL and API Key and try again." });
            }
        }

        var (success, message) = await _arrService.TestConnectionAsync(
            parsedUrl.AbsoluteUri,
            _secretProtector.Unprotect(apiKey),
            cancellationToken).ConfigureAwait(false);

        if (!success)
        {
            // Log the detailed upstream message server-side but return a GENERIC one to the client, so the endpoint cannot be used to distinguish internal host/port states (reachability oracle).
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

        var totalInstanceCount = instances.Count;

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(
                    new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        // Scope to the selected instance's libraries only when multiple instances exist; a single
        // instance owns everything of its type, and the merge path compares against all libraries.
        HashSet<string>? allowedLibraries = null;
        if (index.HasValue && totalInstanceCount > 1)
        {
            allowedLibraries = await ResolveInstanceLibrariesAsync(instances[0], "movies", cancellationToken).ConfigureAwait(false);
        }

        var movieFolders = GetJellyfinFolderNames("movies", allowedLibraries);

        var allMovies = new List<ArrMovie>();
        var failedInstances = new List<string>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var movies = await _arrService.GetRadarrMoviesAsync(instance.Url, _secretProtector.Unprotect(instance.ApiKey), cancellationToken)
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

        var totalInstanceCount = instances.Count;

        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= instances.Count)
            {
                return BadRequest(
                    new { message = $"Invalid instance index {index.Value}. Valid range: 0-{instances.Count - 1}." });
            }

            instances = [instances[index.Value]];
        }

        HashSet<string>? allowedLibraries = null;
        if (index.HasValue && totalInstanceCount > 1)
        {
            allowedLibraries = await ResolveInstanceLibrariesAsync(instances[0], "tvshows", cancellationToken).ConfigureAwait(false);
        }

        var tvFolders = GetJellyfinFolderNames("tvshows", allowedLibraries);

        var allSeries = new List<ArrSeries>();
        var failedInstances = new List<string>();
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            var series = await _arrService.GetSonarrSeriesAsync(instance.Url, _secretProtector.Unprotect(instance.ApiKey), cancellationToken)
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

    /// <summary>
    ///     Resolves the Jellyfin library names a given instance should be compared against.
    ///     A manual override on the instance takes precedence; otherwise the libraries are matched
    ///     from the instance's Arr root folders. Returns null when no scoping applies (compare all).
    /// </summary>
    private async Task<HashSet<string>?> ResolveInstanceLibrariesAsync(
        ArrInstanceConfig instance,
        string collectionType,
        CancellationToken cancellationToken)
    {
        var overrideNames = SplitLibraryNames(instance.Libraries);
        if (overrideNames.Count > 0)
        {
            return overrideNames;
        }

        if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
        {
            return null;
        }

        var rootFolders = await _arrService.GetRootFoldersAsync(instance.Url, _secretProtector.Unprotect(instance.ApiKey), cancellationToken)
            .ConfigureAwait(false);
        if (rootFolders is null || rootFolders.Count == 0)
        {
            return null;
        }

        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => (f.Name, f.CollectionType?.ToString(), (IReadOnlyList<string>)(f.Locations ?? [])));
        var matched = ArrIntegrationService.MatchLibrariesToRootFolders(rootFolders, libraries, collectionType);

        return matched.Count > 0
            ? new HashSet<string>(matched, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    private static HashSet<string> SplitLibraryNames(string? libraries)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(libraries))
        {
            return result;
        }

        foreach (var name in libraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(name);
        }

        return result;
    }

    /// <summary>
    ///     Gets the set of top-level folder names for a given collection type from Jellyfin libraries.
    ///     When <paramref name="allowedLibraryNames"/> is non-null, only libraries whose name is in the
    ///     set are considered.
    /// </summary>
    private HashSet<string> GetJellyfinFolderNames(string collectionType, HashSet<string>? allowedLibraryNames = null)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => string.Equals(
                f.CollectionType?.ToString(),
                collectionType,
                StringComparison.OrdinalIgnoreCase));

        if (allowedLibraryNames is not null)
        {
            folders = folders.Where(f => allowedLibraryNames.Contains(f.Name));
        }

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