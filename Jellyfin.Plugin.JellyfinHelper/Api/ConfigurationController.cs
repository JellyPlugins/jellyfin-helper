using System;
using System.Collections.Generic;
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
    private readonly IArrIntegrationService _arrService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IPluginConfigurationService _configService;
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
    public ConfigurationController(
        IArrIntegrationService arrService,
        IPluginLogService pluginLog,
        ILogger<ConfigurationController> logger,
        ICleanupConfigHelper configHelper,
        IPluginConfigurationService configService,
        ISeerrIntegrationService seerrService)
    {
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
        _configService = configService;
        _seerrService = seerrService;
    }

    /// <summary>
    ///     Gets the current plugin configuration.
    /// </summary>
    /// <returns>The plugin configuration.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        var config = _configHelper.GetConfig();
        return Ok(config);
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

        var validLevels = new[] { "DEBUG", "INFO", "WARN", "ERROR" };
        var level = string.IsNullOrWhiteSpace(request.PluginLogLevel)
            ? "INFO"
            : request.PluginLogLevel.Trim().ToUpperInvariant();

        if (Array.IndexOf(validLevels, level) < 0)
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateConfigurationAsync(
        [FromBody] ConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!_configService.IsInitialized)
        {
            return BadRequest(new { message = "Plugin not initialized." });
        }

        var validationError = ConfigurationRequestValidator.Validate(request);
        if (validationError != null)
        {
            return BadRequest(new { message = validationError });
        }

        // Apply request values to the existing config (preserves accumulated statistics and internal state)
        var config = _configService.GetConfiguration();

        ApplyRequestToConfig(request, config);
        _configService.SaveConfiguration();

        _pluginLog.LogInfo("API", "Plugin configuration updated.", _logger);

        // After saving, test all configured instance connections and log warnings
        var warnings = await TestAllConnectionsAsync(request, cancellationToken).ConfigureAwait(false);

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
            // User cancelled via token — stop testing without logging a warning
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
                return; // User cancelled — stop testing remaining instances
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
        config.IncludedLibraries = request.IncludedLibraries;
        config.ExcludedLibraries = request.ExcludedLibraries;
        config.OrphanMinAgeDays = request.OrphanMinAgeDays;

        config.TrickplayTaskMode = request.TrickplayTaskMode;
        config.EmptyMediaFolderTaskMode = request.EmptyMediaFolderTaskMode;
        config.OrphanedSubtitleTaskMode = request.OrphanedSubtitleTaskMode;
        config.LinkRepairTaskMode = request.LinkRepairTaskMode;
        config.RecommendationsTaskMode = request.RecommendationsTaskMode;
        config.SeerrCleanupTaskMode = request.SeerrCleanupTaskMode;

        config.UseTrash = request.UseTrash;
        config.TrashFolderPath = string.IsNullOrWhiteSpace(request.TrashFolderPath)
            ? ".jellyfin-trash"
            : request.TrashFolderPath;
        config.TrashRetentionDays = request.TrashRetentionDays;

        config.Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;

        // Seerr settings
        config.SeerrUrl = request.SeerrUrl?.Trim() ?? string.Empty;
        config.SeerrApiKey = request.SeerrApiKey?.Trim() ?? string.Empty;
        config.SeerrCleanupAgeDays = string.IsNullOrEmpty(config.SeerrUrl)
            ? 0
            : Math.Clamp(request.SeerrCleanupAgeDays, 1, 3650);

        // Validate and normalize log level (same rules as UpdateLogLevel endpoint)
        var validLevels = new[] { "DEBUG", "INFO", "WARN", "ERROR" };
        var normalizedLevel = string.IsNullOrWhiteSpace(request.PluginLogLevel)
            ? "INFO"
            : request.PluginLogLevel.Trim().ToUpperInvariant();
        config.PluginLogLevel = Array.IndexOf(validLevels, normalizedLevel) >= 0
            ? normalizedLevel
            : "INFO";

        // Update Radarr instances (clear + re-add from request)
        config.RadarrInstances.Clear();
        foreach (var instance in request.RadarrInstances ?? Array.Empty<ArrInstanceConfig>())
        {
            config.RadarrInstances.Add(instance);
        }

        // Update Sonarr instances (clear + re-add from request)
        config.SonarrInstances.Clear();
        foreach (var instance in request.SonarrInstances ?? Array.Empty<ArrInstanceConfig>())
        {
            config.SonarrInstances.Add(instance);
        }
    }
}