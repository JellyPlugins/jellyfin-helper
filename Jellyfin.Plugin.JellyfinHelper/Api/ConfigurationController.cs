using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     API controller for settings.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Configuration")]
[Produces(MediaTypeNames.Application.Json)]
public class ConfigurationController : ControllerBase
{
    // Single source of truth for accepted plugin log levels. Previously duplicated between
    // UpdateLogLevel and ApplyRequestToConfig; hoisted to a shared constant so adding /
    // removing a level touches one place instead of two that could silently drift.
    private static readonly string[] ValidLogLevels = ["DEBUG", "INFO", "WARN", "ERROR"];

    private readonly IArrIntegrationService _arrService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IPluginConfigurationService _configService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ConfigurationController> _logger;
    private readonly IPluginLogService _pluginLog;
    private readonly ISeerrIntegrationService _seerrService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ConfigurationController" /> class.
    /// </summary>
    /// <param name="arrService">The Arr integration service for connection testing.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    /// <param name="configService">The plugin configuration service for read/write access.</param>
    /// <param name="seerrService">The Seerr integration service for connection testing.</param>
    /// <param name="libraryManager">The Jellyfin library manager for listing available libraries.</param>
    public ConfigurationController(
        IArrIntegrationService arrService,
        IPluginLogService pluginLog,
        ILogger<ConfigurationController> logger,
        ICleanupConfigHelper configHelper,
        IPluginConfigurationService configService,
        ISeerrIntegrationService seerrService,
        ILibraryManager libraryManager)
    {
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
        _configService = configService;
        _seerrService = seerrService;
        _libraryManager = libraryManager;
    }

    /// <summary>
    ///     Gets the current plugin configuration.
    ///     API keys are replaced with a masked placeholder (<c>***</c>) so they
    ///     never leave the server in plain text. Clients that need to change a key must
    ///     send the real value via POST /Configuration; receiving the mask means the key
    ///     is already set. Sending the mask back via POST is a no-op — the real stored
    ///     key is preserved.
    /// </summary>
    /// <returns>The masked plugin configuration response.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ConfigurationResponse> GetConfiguration()
    {
        var config = _configHelper.GetConfig();
        return Ok(ConfigurationResponse.FromConfig(config));
    }

    /// <summary>
    ///     Gets the list of available Jellyfin libraries (virtual folders) for the multi-select UI.
    ///     Returns only libraries that are eligible for cleanup (excludes music, boxsets, and
    ///     collection-like libraries). The user's ExcludedLibraries setting is NOT applied here
    ///     because users need to see currently-excluded libraries in order to uncheck them.
    /// </summary>
    /// <returns>A list of library names.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAvailableLibraries()
    {
        var virtualFolders = _libraryManager.GetVirtualFolders();
        var libraries = virtualFolders
            .Where(f =>
            {
                if (string.IsNullOrWhiteSpace(f.Name))
                {
                    return false;
                }

                // Exclude non-video library types that cleanup never processes
                if (f.CollectionType is CollectionTypeOptions.music or CollectionTypeOptions.boxsets)
                {
                    return false;
                }

                // Fallback: also exclude by name pattern for manually created or migrated libraries
                var name = f.Name!;
                if (name.Contains("collection", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("boxset", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            })
            .Select(f => new
            {
                name = f.Name,
                collectionType = string.IsNullOrWhiteSpace(f.CollectionType?.ToString())
                    ? "unknown"
                    : f.CollectionType!.ToString()
            })
            .OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { libraries });
    }

    /// <summary>
    ///     Updates only the plugin log level without touching any other configuration fields.
    ///     This avoids race conditions when the Logs tab changes the level while Settings may be open.
    /// </summary>
    /// <param name="request">The log level update request containing the new level.</param>
    /// <returns>A status result.</returns>
    [HttpPut("LogLevel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdateLogLevel([FromBody] LogLevelUpdateRequest request)
    {
        if (!_configService.IsInitialized)
        {
            return BadRequest(new { message = "Plugin not initialized." });
        }

        var config = _configService.GetConfiguration();

        var level = string.IsNullOrWhiteSpace(request.PluginLogLevel)
            ? "INFO"
            : request.PluginLogLevel.Trim().ToUpperInvariant();

        if (Array.IndexOf(ValidLogLevels, level) < 0)
        {
            return BadRequest(
                new { message = $"Invalid log level '{request.PluginLogLevel}'. Allowed: DEBUG, INFO, WARN, ERROR." });
        }

        config.PluginLogLevel = level;
        _configService.SaveConfiguration();

        _pluginLog.LogInfo("API", $"Plugin log level updated to {level}.", _logger);

        return Ok(new { message = "Log level updated.", pluginLogLevel = level });
    }

    /// <summary>
    ///     Updates the plugin configuration. After saving, performs connection tests
    ///     against all configured Arr instances and logs warnings for unreachable ones.
    ///     The configuration is always saved regardless of connection test results.
    /// </summary>
    /// <param name="request">The configuration update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status result with optional connection warnings.</returns>
    [HttpPost]
    [ServiceFilter(typeof(ModelBindingLogFilter))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateConfigurationAsync(
        [FromBody] ConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        // Model-binding and null-body diagnostics are handled by ModelBindingLogFilter, which
        // runs with Order = int.MinValue so it fires *before* [ApiController]'s built-in
        // ModelStateInvalidFilter. Any 400 for a malformed payload therefore comes with a
        // matching WARNING entry in the plugin log — see ModelBindingLogFilter for the details.
        //
        // Defense-in-depth: if someone ever detaches the filter, we still want to reject a
        // null request rather than NRE. The log line is intentionally absent here because the
        // filter is the single source of truth for that diagnostic — we don't want duplicate
        // entries if both paths ever fire together.
        if (request is null)
        {
            return BadRequest(new { message = "Request body is required." });
        }

        if (!_configService.IsInitialized)
        {
            return BadRequest(new { message = "Plugin not initialized." });
        }

        var validationError = ConfigurationRequestValidator.Validate(request);
        if (validationError != null)
        {
            _pluginLog.LogWarning("API", $"Configuration validation rejected: {validationError}", logger: _logger);
            return BadRequest(new { message = validationError });
        }

        // Apply request values to the existing config (preserves accumulated statistics and internal state)
        var config = _configService.GetConfiguration();
        var persistedLogLevel = config.PluginLogLevel;

        ApplyRequestToConfig(request, config);
        _configService.SaveConfiguration();

        _pluginLog.LogInfo("API", "Plugin configuration updated.", _logger);

        // After saving, test all configured instance connections and log warnings
        var warnings = await TestAllConnectionsAsync(request, cancellationToken).ConfigureAwait(false);

        // Surface the dropped PluginLogLevel so the client doesn't think the change stuck.
        // The Settings POST intentionally does not mutate the log level (owned by the Logs tab);
        // callers that need to change it must use PUT /Configuration/LogLevel.
        if (!string.IsNullOrWhiteSpace(request.PluginLogLevel))
        {
            var requested = request.PluginLogLevel.Trim().ToUpperInvariant();
            if (!string.Equals(requested, persistedLogLevel, StringComparison.OrdinalIgnoreCase))
            {
                var warning = $"PluginLogLevel change ('{persistedLogLevel}' → '{requested}') was ignored by POST /Configuration. " +
                              $"Use PUT /Configuration/LogLevel to change the log level.";
                warnings.Add(warning);
                _pluginLog.LogWarning("API", warning, logger: _logger);
            }
        }

        // Warn when a relative trash path would escape the library root at runtime (falls back silently).
        if (request.UseTrash)
        {
            var trashWarning = ConfigurationRequestValidator.ValidateTrashPath(request.TrashFolderPath);
            if (trashWarning != null)
            {
                warnings.Add(trashWarning);
                _pluginLog.LogWarning("API", trashWarning, logger: _logger);
            }
        }

        return Ok(new { message = "Configuration saved.", warnings });
    }

    /// <summary>
    ///     Tests all configured instance connections (Arr + Seerr) and returns warnings for unreachable ones.
    ///     Results are also logged to the PluginLogs so they appear in the log viewer.
    /// </summary>
    /// <param name="request">The configuration request containing instances to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of warning messages for failed connections (empty if all succeeded).</returns>
    private async Task<List<string>> TestAllConnectionsAsync(
        ConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        await TestArrInstanceGroupAsync(request.RadarrInstances, "Radarr", warnings, cancellationToken)
            .ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return warnings;
        }

        await TestArrInstanceGroupAsync(request.SonarrInstances, "Sonarr", warnings, cancellationToken)
            .ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return warnings;
        }

        await TestSeerrConnectionAsync(request, warnings, cancellationToken).ConfigureAwait(false);

        return warnings;
    }

    /// <summary>
    ///     Tests the configured Seerr instance connection and appends a warning if unreachable.
    ///     Skipped when no Seerr URL or API key is configured.
    /// </summary>
    /// <param name="request">The configuration request containing Seerr settings.</param>
    /// <param name="warnings">The warnings list to append to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task TestSeerrConnectionAsync(
        ConfigurationUpdateRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SeerrUrl) || string.IsNullOrWhiteSpace(request.SeerrApiKey))
        {
            return;
        }

        // Use trimmed values consistent with what ApplyRequestToConfig persists
        var seerrUrl = request.SeerrUrl.Trim();
        var seerrApiKey = request.SeerrApiKey.Trim();

        // When the client echoes back the mask sentinel, the key was not changed — skip the test.
        // ApplyRequestToConfig already preserved the real stored key; using "***" as a live
        // credential would produce a guaranteed 401 from Seerr and a misleading warning.
        if (string.Equals(seerrApiKey, ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var (success, message) = await _seerrService.TestConnectionAsync(
                seerrUrl,
                seerrApiKey,
                cancellationToken).ConfigureAwait(false);

            if (success)
            {
                _pluginLog.LogInfo("API", $"Connection test OK for Seerr: {message}", _logger);
            }
            else
            {
                var warning = $"Seerr instance ({seerrUrl}) is not reachable: {message}";
                warnings.Add(warning);
                _pluginLog.LogWarning("API", warning, logger: _logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancelled via token - stop testing without logging a warning
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            // Handles network errors, timeouts, and non-token OperationCanceledException (e.g., HttpClient timeout)
            var warning = $"Connection test failed for Seerr ({seerrUrl}): {ex.Message}";
            warnings.Add(warning);
            _pluginLog.LogWarning("API", warning, ex, _logger);
        }
    }

    /// <summary>
    ///     Tests a group of Arr instances (Radarr or Sonarr) and appends warnings for unreachable ones.
    /// </summary>
    /// <param name="instances">The instances to test (may be null).</param>
    /// <param name="typeName">The type label (e.g. "Radarr" or "Sonarr").</param>
    /// <param name="warnings">The warnings list to append to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task TestArrInstanceGroupAsync(
        IReadOnlyList<ArrInstanceConfig>? instances,
        string typeName,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (instances is null)
        {
            return;
        }

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            // Skip the live test when the client echoed back the mask sentinel — same guard as
            // TestSeerrConnectionAsync. Sending "***" to Radarr/Sonarr produces a 401 and a
            // spurious warning even though the real stored key is perfectly valid.
            if (string.Equals(instance.ApiKey.Trim(), ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var (success, message) = await _arrService.TestConnectionAsync(
                    instance.Url,
                    instance.ApiKey,
                    cancellationToken).ConfigureAwait(false);

                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"{typeName} #{i + 1}";

                if (success)
                {
                    _pluginLog.LogInfo("API", $"Connection test OK for {label}: {message}", _logger);
                }
                else
                {
                    var warning = $"{typeName} instance '{label}' ({instance.Url}) is not reachable: {message}";
                    warnings.Add(warning);
                    _pluginLog.LogWarning("API", warning, logger: _logger);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // User cancelled - stop testing remaining instances
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"{typeName} #{i + 1}";
                var warning = $"{typeName} instance '{label}' ({instance.Url}) connection test failed: {ex.Message}";
                warnings.Add(warning);
                _pluginLog.LogWarning("API", warning, ex, _logger);
            }
        }
    }

    /// <summary>
    ///     Maps all user-editable fields from the update request onto the existing plugin configuration.
    ///     Preserves accumulated statistics and internal state that are not part of the request.
    /// </summary>
    /// <param name="request">The incoming configuration update request.</param>
    /// <param name="config">The existing plugin configuration to update.</param>
    private static void ApplyRequestToConfig(ConfigurationUpdateRequest request, PluginConfiguration config)
    {
        // Normalize nullable strings to prevent downstream NREs from explicit JSON null values
        config.ExcludedLibraries = request.ExcludedLibraries ?? string.Empty;
        config.OrphanMinAgeDays = Math.Clamp(request.OrphanMinAgeDays, 0, 3650);

        config.TrickplayTaskMode = request.TrickplayTaskMode;
        config.EmptyMediaFolderTaskMode = request.EmptyMediaFolderTaskMode;
        config.OrphanedSubtitleTaskMode = request.OrphanedSubtitleTaskMode;
        config.LinkRepairTaskMode = request.LinkRepairTaskMode;
        if (request.RecommendationsTaskMode.HasValue)
        {
            config.RecommendationsTaskMode = request.RecommendationsTaskMode.Value;
        }

        if (request.SyncRecommendationsToPlaylist.HasValue)
        {
            config.SyncRecommendationsToPlaylist = request.SyncRecommendationsToPlaylist.Value;
        }

        if (request.DiscoveryUserAccessEnabled.HasValue)
        {
            config.DiscoveryUserAccessEnabled = request.DiscoveryUserAccessEnabled.Value;
        }

        config.SeerrCleanupTaskMode = request.SeerrCleanupTaskMode;

        config.UseTrash = request.UseTrash;
        config.TrashFolderPath = string.IsNullOrWhiteSpace(request.TrashFolderPath)
            ? ".jellyfin-trash"
            : request.TrashFolderPath;
        config.TrashRetentionDays = request.TrashRetentionDays;

        config.Language = string.IsNullOrWhiteSpace(request.Language) ? "en" :
                          ConfigurationRequestValidator.IsLanguageSupported(request.Language) ? request.Language : "en";

        // Seerr settings
        config.SeerrUrl = string.IsNullOrWhiteSpace(request.SeerrUrl) ? string.Empty : request.SeerrUrl.Trim();
        // If the client echoes back the mask sentinel ("***"), the key was not changed — preserve the stored value.
        // Trim before comparing so a client that pads the sentinel (e.g. " *** ") is still recognised correctly
        // and never overwrites the real stored key with a literal "***".
        if (!string.Equals(request.SeerrApiKey?.Trim(), ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal))
        {
            config.SeerrApiKey = string.IsNullOrWhiteSpace(request.SeerrApiKey) ? string.Empty : request.SeerrApiKey.Trim();
        }

        config.SeerrCleanupAgeDays = string.IsNullOrEmpty(config.SeerrUrl)
            ? 0
            : Math.Clamp(request.SeerrCleanupAgeDays, 1, 3650);

        // PluginLogLevel is owned by the Logs tab and mutated exclusively via PUT /Configuration/LogLevel.
        // The Settings POST payload is intentionally IGNORED for this field to close a TOCTOU race
        // where the Settings page had captured a stale value at page load, then overwrote a
        // concurrently-changed level (from the Logs tab or another admin session) on save.
        // Keeping the merge server-side eliminates the need for a client-side preflight GET and
        // guarantees the invariant regardless of which caller sends the POST. Legacy configs that
        // arrive with an invalid persisted level are normalized to "INFO" as a self-healing
        // fallback so downstream log-filtering code never has to deal with garbage.
        if (string.IsNullOrWhiteSpace(config.PluginLogLevel)
            || Array.IndexOf(ValidLogLevels, config.PluginLogLevel.Trim().ToUpperInvariant()) < 0)
        {
            config.PluginLogLevel = "INFO";
        }
        else
        {
            // Persist the canonical UPPER form even if the on-disk value has drifted casing.
            config.PluginLogLevel = config.PluginLogLevel.Trim().ToUpperInvariant();
        }

        // Update Radarr instances (clear + re-add from request).
        // Snapshot existing instances BEFORE clearing so the sentinel guard can look up
        // the stored key by Name+Url rather than positional index. Index-based restoration
        // would silently assign the wrong key when the admin removes or reorders instances.
        var previousRadarrInstances = config.RadarrInstances.ToList();
        config.RadarrInstances.Clear();
        var radarrRequestList = request.RadarrInstances ?? [];
        for (var i = 0; i < radarrRequestList.Count; i++)
        {
            var instance = radarrRequestList[i];
            // Sentinel "***" means the UI echoed back the masked placeholder without
            // changing the field. Restore the key for the matching Name+Url pair;
            // fall back to empty string when no prior entry matches.
            var apiKey = string.Equals(instance.ApiKey?.Trim(), ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal)
                ? previousRadarrInstances
                    .FirstOrDefault(p => p.Url == instance.Url)?.ApiKey
                    ?? string.Empty
                : instance.ApiKey ?? string.Empty;
            config.RadarrInstances.Add(new ArrInstanceConfig
            {
                Name = instance.Name,
                Url = instance.Url,
                ApiKey = apiKey
            });
        }

        // Update Sonarr instances (clear + re-add from request).
        // Same sentinel-preservation pattern as Radarr above.
        var previousSonarrInstances = config.SonarrInstances.ToList();
        config.SonarrInstances.Clear();
        var sonarrRequestList = request.SonarrInstances ?? [];
        for (var i = 0; i < sonarrRequestList.Count; i++)
        {
            var instance = sonarrRequestList[i];
            var apiKey = string.Equals(instance.ApiKey?.Trim(), ConfigurationResponse.ApiKeyMask, StringComparison.Ordinal)
                ? previousSonarrInstances
                    .FirstOrDefault(p => p.Url == instance.Url)?.ApiKey
                    ?? string.Empty
                : instance.ApiKey ?? string.Empty;
            config.SonarrInstances.Add(new ArrInstanceConfig
            {
                Name = instance.Name,
                Url = instance.Url,
                ApiKey = apiKey
            });
        }
    }
}