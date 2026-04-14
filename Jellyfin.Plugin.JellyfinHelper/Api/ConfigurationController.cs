using System;
using System.Collections.Generic;
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
    private readonly ArrIntegrationService _arrService;
    private readonly ILogger<ConfigurationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="arrService">The Arr integration service for connection testing.</param>
    /// <param name="logger">The controller logger.</param>
    public ConfigurationController(ArrIntegrationService arrService, ILogger<ConfigurationController> logger)
    {
        _arrService = arrService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    /// <returns>The plugin configuration.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration> GetConfiguration()
    {
        var config = CleanupConfigHelper.GetConfig();
        return Ok(config);
    }

    /// <summary>
    /// Updates the plugin configuration. After saving, performs connection tests
    /// against all configured Arr instances and logs warnings for unreachable ones.
    /// The configuration is always saved regardless of connection test results.
    /// </summary>
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

        if (request.RadarrInstances.Count > maxArrInstances)
        {
            return BadRequest(new { message = $"Maximum {maxArrInstances} Radarr instances allowed." });
        }

        if (request.SonarrInstances.Count > maxArrInstances)
        {
            return BadRequest(new { message = $"Maximum {maxArrInstances} Sonarr instances allowed." });
        }

        var arrValidationError = ValidateArrInstances(request.RadarrInstances, "Radarr")
                                 ?? ValidateArrInstances(request.SonarrInstances, "Sonarr");
        if (arrValidationError != null)
        {
            return BadRequest(new { message = arrValidationError });
        }

        // Apply request values to the existing config (preserves accumulated statistics and internal state)
        var config = plugin.Configuration;

        config.IncludedLibraries = request.IncludedLibraries;
        config.ExcludedLibraries = request.ExcludedLibraries;
        config.OrphanMinAgeDays = request.OrphanMinAgeDays;

        config.TrickplayTaskMode = request.TrickplayTaskMode;
        config.EmptyMediaFolderTaskMode = request.EmptyMediaFolderTaskMode;
        config.OrphanedSubtitleTaskMode = request.OrphanedSubtitleTaskMode;
        config.StrmRepairTaskMode = request.StrmRepairTaskMode;

        config.UseTrash = request.UseTrash;
        config.TrashFolderPath = request.TrashFolderPath;
        config.TrashRetentionDays = request.TrashRetentionDays;

        config.RadarrUrl = request.RadarrUrl;
        config.RadarrApiKey = request.RadarrApiKey;
        config.SonarrUrl = request.SonarrUrl;
        config.SonarrApiKey = request.SonarrApiKey;

        config.Language = request.Language;

        // Update Radarr instances (clear + re-add from request)
        config.RadarrInstances.Clear();
        foreach (var instance in request.RadarrInstances)
        {
            config.RadarrInstances.Add(instance);
        }

        // Update Sonarr instances (clear + re-add from request)
        config.SonarrInstances.Clear();
        foreach (var instance in request.SonarrInstances)
        {
            config.SonarrInstances.Add(instance);
        }

        plugin.SaveConfiguration();

        PluginLogService.LogInfo("API", "Plugin configuration updated.", _logger);

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

        // Test Radarr instances
        for (int i = 0; i < request.RadarrInstances.Count; i++)
        {
            var instance = request.RadarrInstances[i];
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            try
            {
                var (success, message) = await _arrService.TestConnectionAsync(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);

                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"Radarr #{i + 1}";

                if (success)
                {
                    PluginLogService.LogInfo("API", $"Connection test OK for {label}: {message}", _logger);
                }
                else
                {
                    var warning = $"Radarr instance '{label}' ({instance.Url}) is not reachable: {message}";
                    warnings.Add(warning);
                    PluginLogService.LogWarning("API", warning, logger: _logger);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"Radarr #{i + 1}";
                var warning = $"Radarr instance '{label}' ({instance.Url}) connection test failed: {ex.Message}";
                warnings.Add(warning);
                PluginLogService.LogWarning("API", warning, ex, _logger);
            }
        }

        // Test Sonarr instances
        for (int i = 0; i < request.SonarrInstances.Count; i++)
        {
            var instance = request.SonarrInstances[i];
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            try
            {
                var (success, message) = await _arrService.TestConnectionAsync(
                    instance.Url, instance.ApiKey, cancellationToken).ConfigureAwait(false);

                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"Sonarr #{i + 1}";

                if (success)
                {
                    PluginLogService.LogInfo("API", $"Connection test OK for {label}: {message}", _logger);
                }
                else
                {
                    var warning = $"Sonarr instance '{label}' ({instance.Url}) is not reachable: {message}";
                    warnings.Add(warning);
                    PluginLogService.LogWarning("API", warning, logger: _logger);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"Sonarr #{i + 1}";
                var warning = $"Sonarr instance '{label}' ({instance.Url}) connection test failed: {ex.Message}";
                warnings.Add(warning);
                PluginLogService.LogWarning("API", warning, ex, _logger);
            }
        }

        return warnings;
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
            if (!string.IsNullOrWhiteSpace(instance.Url))
            {
                if (!Uri.TryCreate(instance.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"#{i + 1}";
                    return $"{typeName} instance '{label}' has an invalid URL. Only http:// and https:// URLs are allowed.";
                }
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