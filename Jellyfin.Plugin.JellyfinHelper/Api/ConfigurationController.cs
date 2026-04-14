using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
/// API controller for settings.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Configuration")]
[Produces(MediaTypeNames.Application.Json)]
public class ConfigurationController : ControllerBase
{
    private readonly IArrIntegrationService _arrService;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<ConfigurationController> _logger;
    private readonly ICleanupConfigHelper _configHelper;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="arrService">The Arr integration service for connection testing.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The controller logger.</param>
    /// <param name="configHelper">The cleanup configuration helper.</param>
    public ConfigurationController(IArrIntegrationService arrService, IPluginLogService pluginLog, ILogger<ConfigurationController> logger, ICleanupConfigHelper configHelper)
    {
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
    }

    /// <summary>
    /// Gets the current plugin configuration.
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
    /// Updates the plugin configuration. After saving, performs connection tests
    /// against all configured Arr instances and logs warnings for unreachable ones.
    /// The configuration is always saved regardless of connection test results.
    /// </summary>
    /// <remarks>
    /// This method accesses <see cref="Plugin.Instance"/> directly because
    /// <c>BasePlugin&lt;T&gt;.SaveConfiguration()</c> is an instance method on the
    /// plugin singleton and cannot be abstracted behind <see cref="ICleanupConfigHelper"/>.
    /// Read-only access uses <c>_configHelper</c> (see <see cref="GetConfiguration"/>).
    /// </remarks>
    /// <param name="request">The configuration update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status result with optional connection warnings.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateConfigurationAsync([FromBody] ConfigurationUpdateRequest request, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return BadRequest(new { message = "Plugin not initialized." });
        }

        // Validate numeric fields (aligned with BackupService.Validate range 0–3650)
        if (request.OrphanMinAgeDays < 0 || request.OrphanMinAgeDays > 3650)
        {
            return BadRequest(new { message = "OrphanMinAgeDays must be 0–3650." });
        }

        if (request.TrashRetentionDays < 0 || request.TrashRetentionDays > 3650)
        {
            return BadRequest(new { message = "TrashRetentionDays must be 0–3650." });
        }

        // Validate Arr instances (max 3 per type, URL format, non-empty API key)
        const int maxArrInstances = 3;

        if (request.RadarrInstances is { Count: > maxArrInstances })
        {
            return BadRequest(new { message = $"Maximum {maxArrInstances} Radarr instances allowed." });
        }

        if (request.SonarrInstances is { Count: > maxArrInstances })
        {
            return BadRequest(new { message = $"Maximum {maxArrInstances} Sonarr instances allowed." });
        }

        var arrValidationError = (request.RadarrInstances is not null ? ValidateArrInstances(request.RadarrInstances, "Radarr") : null)
                                 ?? (request.SonarrInstances is not null ? ValidateArrInstances(request.SonarrInstances, "Sonarr") : null);
        if (arrValidationError != null)
        {
            return BadRequest(new { message = arrValidationError });
        }

        // Apply request values to the existing config (preserves accumulated statistics and internal state)
        var config = plugin.Configuration;
        if (config == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Plugin configuration not initialized." });
        }

        ApplyRequestToConfig(request, config);
        plugin.SaveConfiguration();

        _pluginLog.LogInfo("API", "Plugin configuration updated.", _logger);

        // After saving, test all configured Arr instance connections and log warnings
        var warnings = await TestArrConnectionsAsync(request, cancellationToken).ConfigureAwait(false);

        return Ok(new { message = "Configuration saved.", warnings });
    }

    /// <summary>
    /// Tests all configured Arr instance connections and returns warnings for unreachable ones.
    /// Results are also logged to the PluginLogs so they appear in the log viewer.
    /// </summary>
    /// <param name="request">The configuration request containing instances to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of warning messages for failed connections (empty if all succeeded).</returns>
    private async Task<List<string>> TestArrConnectionsAsync(ConfigurationUpdateRequest request, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        await TestArrInstanceGroupAsync(request.RadarrInstances, "Radarr", warnings, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            return warnings;
        }

        await TestArrInstanceGroupAsync(request.SonarrInstances, "Sonarr", warnings, cancellationToken).ConfigureAwait(false);

        return warnings;
    }

    /// <summary>
    /// Tests a group of Arr instances (Radarr or Sonarr) and appends warnings for unreachable ones.
    /// </summary>
    /// <param name="instances">The instances to test (may be null).</param>
    /// <param name="typeName">The type label (e.g. "Radarr" or "Sonarr").</param>
    /// <param name="warnings">The warnings list to append to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task TestArrInstanceGroupAsync(IReadOnlyList<ArrInstanceConfig>? instances, string typeName, List<string> warnings, CancellationToken cancellationToken)
    {
        if (instances is null)
        {
            return;
        }

        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            try
            {
                var (success, message) = await _arrService.TestConnectionAsync(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);

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
    /// Maps all user-editable fields from the update request onto the existing plugin configuration.
    /// Preserves accumulated statistics and internal state that are not part of the request.
    /// </summary>
    /// <param name="request">The incoming configuration update request.</param>
    /// <param name="config">The existing plugin configuration to update.</param>
    private static void ApplyRequestToConfig(ConfigurationUpdateRequest request, PluginConfiguration config)
    {
        // Normalize nullable strings to prevent downstream NREs from explicit JSON null values
        config.IncludedLibraries = request.IncludedLibraries ?? string.Empty;
        config.ExcludedLibraries = request.ExcludedLibraries ?? string.Empty;
        config.OrphanMinAgeDays = request.OrphanMinAgeDays;

        config.TrickplayTaskMode = request.TrickplayTaskMode;
        config.EmptyMediaFolderTaskMode = request.EmptyMediaFolderTaskMode;
        config.OrphanedSubtitleTaskMode = request.OrphanedSubtitleTaskMode;
        config.StrmRepairTaskMode = request.StrmRepairTaskMode;

        config.UseTrash = request.UseTrash;
        config.TrashFolderPath = string.IsNullOrWhiteSpace(request.TrashFolderPath) ? ".jellyfin-trash" : request.TrashFolderPath;
        config.TrashRetentionDays = request.TrashRetentionDays;

        config.RadarrUrl = request.RadarrUrl ?? string.Empty;
        config.RadarrApiKey = request.RadarrApiKey ?? string.Empty;
        config.SonarrUrl = request.SonarrUrl ?? string.Empty;
        config.SonarrApiKey = request.SonarrApiKey ?? string.Empty;

        config.Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;
        config.PluginLogLevel = string.IsNullOrWhiteSpace(request.PluginLogLevel) ? "INFO" : request.PluginLogLevel;

        // Update Radarr instances (clear + re-add from request)
        config.RadarrInstances.Clear();
        if (request.RadarrInstances is not null)
        {
            foreach (var instance in request.RadarrInstances)
            {
                config.RadarrInstances.Add(instance);
            }
        }

        // Update Sonarr instances (clear + re-add from request)
        config.SonarrInstances.Clear();
        if (request.SonarrInstances is not null)
        {
            foreach (var instance in request.SonarrInstances)
            {
                config.SonarrInstances.Add(instance);
            }
        }
    }

    /// <summary>
    /// Validates a list of Arr instances for URL format and non-empty API keys.
    /// </summary>
    /// <param name="instances">The instances to validate.</param>
    /// <param name="typeName">The type name (Radarr/Sonarr) for error messages.</param>
    /// <returns>An error message string, or null if all instances are valid.</returns>
    private static string? ValidateArrInstances(IReadOnlyList<ArrInstanceConfig> instances, string typeName)
    {
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];

            // Skip completely empty instances (user may have added a blank row)
            if (string.IsNullOrWhiteSpace(instance.Url) && string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            // If URL is provided, validate format
            if (!string.IsNullOrWhiteSpace(instance.Url) &&
                (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https")))
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return $"{typeName} instance '{label}' has an invalid URL. Only http:// and https:// URLs are allowed.";
            }

            // If URL is set, API key must also be set
            if (!string.IsNullOrWhiteSpace(instance.Url) && string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                return $"{typeName} instance '{label}' has a URL but no API key.";
            }
        }

        return null;
    }
}