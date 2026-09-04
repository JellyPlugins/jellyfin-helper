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
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
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
    // Single source of truth for accepted plugin log levels. Previously duplicated between UpdateLogLevel and ApplyRequestToConfig; hoisted to a shared constant so adding / removing a level touches one place instead of two that could silently drift.
    private static readonly string[] ValidLogLevels = ["DEBUG", "INFO", "WARN", "ERROR"];

    private readonly IArrIntegrationService _arrService;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly IPluginConfigurationService _configService;
    private readonly EnsembleScoringStrategy _ensemble;
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
    /// <param name="ensemble">The ensemble scoring strategy - notified on config save so alpha bounds take effect without restart.</param>
    public ConfigurationController(
        IArrIntegrationService arrService,
        IPluginLogService pluginLog,
        ILogger<ConfigurationController> logger,
        ICleanupConfigHelper configHelper,
        IPluginConfigurationService configService,
        ISeerrIntegrationService seerrService,
        ILibraryManager libraryManager,
        EnsembleScoringStrategy ensemble)
    {
        _arrService = arrService;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
        _configService = configService;
        _seerrService = seerrService;
        _libraryManager = libraryManager;
        _ensemble = ensemble;
    }

    /// <summary>
    ///     Gets the current plugin configuration. API keys are replaced with a fixed-length masked placeholder (ApiKeyMask) so they never leave the server in plain text.
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
    /// </summary>
    /// <returns>A list of library names.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(typeof(LibraryListResponse), StatusCodes.Status200OK)]
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
            .Select(f => new LibraryEntry
            {
                Name = f.Name ?? string.Empty,
                CollectionType = string.IsNullOrWhiteSpace(f.CollectionType?.ToString())
                    ? "unknown"
                    : f.CollectionType?.ToString() ?? "unknown",
            })
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new LibraryListResponse { Libraries = libraries });
    }

    /// <summary>
    ///     Updates only the plugin log level without touching any other configuration fields.
    /// </summary>
    /// <param name="request">The log level update request containing the new level.</param>
    /// <returns>A status result.</returns>
    [HttpPut("LogLevel")]
    [ProducesResponseType(typeof(LogLevelResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LogLevelResponse), StatusCodes.Status400BadRequest)]
    public ActionResult UpdateLogLevel([FromBody] LogLevelUpdateRequest request)
    {
        // A literal `null` JSON body binds `request` to null on this [ApiController] (this endpoint has no ModelBindingLogFilter, unlike the main PUT), so guard explicitly to return a clean 400 instead of a NullReferenceException -> 500.
        if (request is null)
        {
            return BadRequest(new LogLevelResponse { Message = "Request body is required." });
        }

        if (!_configService.IsInitialized)
        {
            return BadRequest(new LogLevelResponse { Message = "Plugin not initialized." });
        }

        var level = string.IsNullOrWhiteSpace(request.PluginLogLevel)
            ? "INFO"
            : request.PluginLogLevel.Trim().ToUpperInvariant();

        if (Array.IndexOf(ValidLogLevels, level) < 0)
        {
            return BadRequest(
                new LogLevelResponse { Message = $"Invalid log level '{request.PluginLogLevel}'. Allowed: DEBUG, INFO, WARN, ERROR." });
        }

        _configService.ReadAndMutate(config => config.PluginLogLevel = level);

        _pluginLog.LogInfo("API", $"Plugin log level updated to {level}.", _logger);

        return Ok(new LogLevelResponse { Message = "Log level updated.", PluginLogLevel = level });
    }

    /// <summary>
    ///     Updates the plugin configuration. After saving, performs connection tests against all configured Arr instances and logs warnings for unreachable ones.
    /// </summary>
    /// <param name="request">The configuration update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A status result with optional connection warnings.</returns>
    [HttpPut]
    [ServiceFilter(typeof(ModelBindingLogFilter))]
    [ProducesResponseType(typeof(ConfigurationSaveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfigurationSaveResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateConfigurationAsync(
        [FromBody] ConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        // Model-binding and null-body diagnostics are handled by ModelBindingLogFilter, which orders below [ApiController]'s built-in ModelStateInvalidFilter (-2000) so it fires *before* the automatic 400.
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

        // Apply request values to the existing config (preserves accumulated statistics and internal state) Both the read and the mutation must happen inside ReadAndMutate so no other caller can interleave its own writes between GetConfiguration and SaveConfiguration.
        PluginConfiguration config = null!;
        string persistedLogLevel = string.Empty;
        _configService.ReadAndMutate(cfg =>
        {
            config = cfg;
            persistedLogLevel = cfg.PluginLogLevel;
            ApplyRequestToConfig(request, cfg);
            _ensemble.Reconfigure(cfg.EnsembleAlphaMin, cfg.EnsembleAlphaMax, cfg.EnsembleGenrePenaltyFloor);
        });

        _pluginLog.LogInfo("API", "Plugin configuration updated.", _logger);

        // After saving, test all configured instance connections and log warnings
        var warnings = await TestAllConnectionsAsync(request, cancellationToken).ConfigureAwait(false);

        // Surface the dropped PluginLogLevel so the client doesn't think the change stuck. The Settings POST intentionally does not mutate the log level (owned by the Logs tab); callers that need to change it must use PUT /Configuration/LogLevel.
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

        // Warn (do not block) when ExcludedLibraries names libraries that do not currently exist. A stale/typo'd name is benign at runtime (it simply never matches during cleanup) but surfacing it helps the admin catch a mistake.
        if (!string.IsNullOrWhiteSpace(request.ExcludedLibraries))
        {
            var excluded = CleanupConfigHelper.ParseCommaSeparated(request.ExcludedLibraries);
            if (excluded.Count > 0)
            {
                var existingNames = _libraryManager.GetVirtualFolders()
                    .Select(f => f.Name ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unknown = excluded.Where(name => !existingNames.Contains(name)).ToList();
                if (unknown.Count > 0)
                {
                    var libWarning = $"ExcludedLibraries references {unknown.Count} library name(s) that do not currently exist: {string.Join(", ", unknown)}. They will have no effect until a matching library exists.";
                    warnings.Add(libWarning);
                    _pluginLog.LogWarning("API", libWarning, logger: _logger);
                }
            }
        }

        return Ok(new ConfigurationSaveResponse { Message = "Configuration saved.", Warnings = warnings });
    }

    /// <summary>
    ///     Tests all configured instance connections (Arr + Seerr) and returns warnings for unreachable ones.
    /// </summary>
    /// <param name="request">The configuration request containing instances to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of warning messages for failed connections (empty if all succeeded).</returns>
    private async Task<List<string>> TestAllConnectionsAsync(
        ConfigurationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        // Each group gets its own list to avoid shared-state races when run concurrently.
        var radarrWarnings = new List<string>();
        var sonarrWarnings = new List<string>();
        var seerrWarnings = new List<string>();

        await Task.WhenAll(
            TestArrInstanceGroupAsync(request.RadarrInstances, "Radarr", radarrWarnings, cancellationToken),
            TestArrInstanceGroupAsync(request.SonarrInstances, "Sonarr", sonarrWarnings, cancellationToken),
            TestSeerrConnectionAsync(request, seerrWarnings, cancellationToken)).ConfigureAwait(false);

        var warnings = new List<string>(radarrWarnings.Count + sonarrWarnings.Count + seerrWarnings.Count);
        warnings.AddRange(radarrWarnings);
        warnings.AddRange(sonarrWarnings);
        warnings.AddRange(seerrWarnings);
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

        // Credential-safe label (scheme+host+port only) for anything echoed to the client or logged;
        // the raw URL can embed user-info credentials (https://user:password@host).
        var seerrLabel = Services.Common.SsrfGuard.SafeEndpointLabel(seerrUrl);

        // When the client echoes back the mask sentinel, the key was not changed - skip the test. ApplyRequestToConfig already preserved the real stored key; using the mask as a live credential would produce a guaranteed 401 from Seerr and a misleading warning.
        if (ApiKeyMaskResolver.IsMask(seerrApiKey))
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
                // Generic client-facing warning; the upstream `message` (which can reveal reachability/credential details) is logged server-side only.
                warnings.Add($"Seerr instance ({seerrLabel}) is not reachable. Verify the URL and API Key.");
                _pluginLog.LogWarning("API", $"Seerr instance ({seerrLabel}) is not reachable: {message}", logger: _logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancelled via token - stop testing without logging a warning
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            // Handles network errors, timeouts, and non-token OperationCanceledException (e.g., HttpClient timeout).
            warnings.Add($"Connection test failed for Seerr ({seerrLabel}). Verify the URL and API Key.");
            _pluginLog.LogWarning("API", $"Connection test failed for Seerr ({seerrLabel}): {ex.Message}", ex, _logger);
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

        // ConfigurationRequestValidator.Validate (run before this method) already rejects lists longer than the allowed maximum, so this cap is a runtime no-op; it makes the loop bound provably constant for taint analysis and guards against an unbounded test fan-out.
        const int MaxInstances = 3;
        var count = Math.Min(instances.Count, MaxInstances);
        for (var i = 0; i < count; i++)
        {
            var instance = instances[i];
            if (string.IsNullOrWhiteSpace(instance.Url) || string.IsNullOrWhiteSpace(instance.ApiKey))
            {
                continue;
            }

            // Skip the live test when the client echoed back the mask sentinel - same guard as TestSeerrConnectionAsync.
            if (ApiKeyMaskResolver.IsMask(instance.ApiKey))
            {
                continue;
            }

            var cancelled = await TestSingleArrInstanceAsync(instance, typeName, i, warnings, cancellationToken)
                .ConfigureAwait(false);
            if (cancelled)
            {
                return; // User cancelled - stop testing remaining instances
            }
        }
    }

    /// <summary>
    ///     Tests a single Arr instance and appends a warning when it is unreachable or the test fails.
    /// </summary>
    /// <param name="instance">The instance to test.</param>
    /// <param name="typeName">The type label (e.g. "Radarr" or "Sonarr").</param>
    /// <param name="index">The zero-based index of the instance within its group.</param>
    /// <param name="warnings">The warnings list to append to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the caller should stop testing remaining instances (cancelled); otherwise <c>false</c>.</returns>
    private async Task<bool> TestSingleArrInstanceAsync(
        ArrInstanceConfig instance,
        string typeName,
        int index,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message) = await _arrService.TestConnectionAsync(
                instance.Url,
                instance.ApiKey,
                cancellationToken).ConfigureAwait(false);

            var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"{typeName} #{index + 1}";

            if (success)
            {
                _pluginLog.LogInfo("API", $"Connection test OK for {label}: {message}", _logger);
            }
            else
            {
                // Generic client-facing warning; the upstream `message` (which can reveal reachability details) is logged server-side only.
                var urlLabel = Services.Common.SsrfGuard.SafeEndpointLabel(instance.Url);
                warnings.Add($"{typeName} instance '{label}' ({urlLabel}) is not reachable. Verify the URL and API Key.");
                _pluginLog.LogWarning("API", $"{typeName} instance '{label}' ({urlLabel}) is not reachable: {message}", logger: _logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true; // User cancelled - stop testing remaining instances
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException)
        {
            var label = !string.IsNullOrWhiteSpace(instance.Name) ? instance.Name : $"{typeName} #{index + 1}";
            // Generic client-facing warning; the raw ex.Message is logged server-side only.
            // The credential-safe label strips any user-info password embedded in instance.Url.
            var urlLabel = Services.Common.SsrfGuard.SafeEndpointLabel(instance.Url);
            warnings.Add($"{typeName} instance '{label}' ({urlLabel}) connection test failed. Verify the URL and API Key.");
            _pluginLog.LogWarning("API", $"{typeName} instance '{label}' ({urlLabel}) connection test failed: {ex.Message}", ex, _logger);
        }

        return false;
    }

    /// <summary>
    ///     Maps all user-editable fields from the update request onto the existing plugin configuration.
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

        ApplyEnsembleSettings(request, config);

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

        string language;
        if (string.IsNullOrWhiteSpace(request.Language))
        {
            language = "en";
        }
        else
        {
            language = ConfigurationRequestValidator.IsLanguageSupported(request.Language) ? request.Language : "en";
        }

        config.Language = language;

        // Seerr settings
        config.SeerrUrl = string.IsNullOrWhiteSpace(request.SeerrUrl) ? string.Empty : request.SeerrUrl.Trim();
        // If the client echoes back the mask sentinel, the key was not changed - preserve the stored value. Trim before comparing so a client that pads the sentinel (e.g.
        if (!ApiKeyMaskResolver.IsMask(request.SeerrApiKey))
        {
            config.SeerrApiKey = string.IsNullOrWhiteSpace(request.SeerrApiKey) ? string.Empty : request.SeerrApiKey.Trim();
        }

        config.SeerrCleanupAgeDays = string.IsNullOrEmpty(config.SeerrUrl)
            ? 0
            : Math.Clamp(request.SeerrCleanupAgeDays, 1, 3650);

        NormalizePluginLogLevel(config);

        // Update Radarr instances (clear + re-add from request). Snapshot existing instances BEFORE clearing so the sentinel guard can look up the stored key by Name+Url rather than positional index.
        config.RadarrInstances = RebuildArrInstances(request.RadarrInstances, config.RadarrInstances);

        // Update Sonarr instances (clear + re-add from request).
        // Same sentinel-preservation pattern as Radarr above.
        config.SonarrInstances = RebuildArrInstances(request.SonarrInstances, config.SonarrInstances);
    }

    /// <summary>
    ///     Applies the ML ensemble tuning fields (alpha bounds and genre-penalty floor) from the request, clamping each to the valid [0,1] range and enforcing the min &lt;= max invariant.
    /// </summary>
    /// <param name="request">The incoming configuration update request.</param>
    /// <param name="config">The existing plugin configuration to update.</param>
    private static void ApplyEnsembleSettings(ConfigurationUpdateRequest request, PluginConfiguration config)
    {
        if (request.EnsembleAlphaMin.HasValue)
        {
            config.EnsembleAlphaMin = Math.Clamp(request.EnsembleAlphaMin.Value, 0.0, 1.0);
        }

        if (request.EnsembleAlphaMax.HasValue)
        {
            config.EnsembleAlphaMax = Math.Clamp(request.EnsembleAlphaMax.Value, 0.0, 1.0);
        }

        if (request.EnsembleGenrePenaltyFloor.HasValue)
        {
            config.EnsembleGenrePenaltyFloor = Math.Clamp(request.EnsembleGenrePenaltyFloor.Value, 0.0, 1.0);
        }

        // The min/max invariant is enforced by the EnsembleAlphaMin/Max setters, which swap an
        // inverted pair on assignment; no additional reconciliation is needed here.
    }

    /// <summary>
    ///     Normalizes the persisted plugin log level to a canonical UPPER-cased value, self-healing
    ///     legacy or invalid values to "INFO".
    /// </summary>
    /// <param name="config">The plugin configuration whose log level is normalized in place.</param>
    private static void NormalizePluginLogLevel(PluginConfiguration config)
    {
        // PluginLogLevel is owned by the Logs tab and mutated exclusively via PUT /Configuration/LogLevel.
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
    }

    /// <summary>
    ///     Rebuilds an Arr instance list from the request, preserving stored API keys when the client echoes back the mask sentinel.
    /// </summary>
    /// <param name="requestInstances">The instances from the incoming request (may be null).</param>
    /// <param name="existingInstances">The currently stored instances (may be null).</param>
    /// <returns>A new list of resolved <see cref="ArrInstanceConfig" /> entries.</returns>
    private static List<ArrInstanceConfig> RebuildArrInstances(
        IEnumerable<ArrInstanceConfig>? requestInstances,
        List<ArrInstanceConfig>? existingInstances)
    {
        var previousInstances = (existingInstances ?? []).ToList();
        var result = new List<ArrInstanceConfig>();
        foreach (var instance in requestInstances ?? [])
        {
            result.Add(new ArrInstanceConfig
            {
                Name = instance.Name,
                Url = instance.Url,
                ApiKey = ResolveApiKey(instance, previousInstances)
            });
        }

        return result;
    }

    /// <summary>
    ///     Resolves the API key for an incoming ArrInstanceConfig from a configuration update.
    /// </summary>
    private static string ResolveApiKey(
        ArrInstanceConfig incoming,
        List<ArrInstanceConfig> previousInstances)
    {
        // Delegates to the shared resolver so the save path and the stateless Test-Connection endpoints (ArrIntegrationController / SeerrController) use one implementation of the mask-sentinel semantics.
        return ApiKeyMaskResolver.ResolveArrKey(
            incoming.ApiKey,
            incoming.Url,
            incoming.Name,
            previousInstances);
    }
}