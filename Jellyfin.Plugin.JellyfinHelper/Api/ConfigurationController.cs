using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
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
    private readonly ILogger<ConfigurationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="logger">The controller logger.</param>
    public ConfigurationController(ILogger<ConfigurationController> logger)
    {
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
    /// Updates the plugin configuration.
    /// </summary>
    /// <param name="updatedConfig">The updated configuration.</param>
    /// <returns>A status result.</returns>
    [HttpPost]
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

        // Preserve accumulated statistics and internal state (don't let the UI overwrite them)
        var currentConfig = plugin.Configuration;
        updatedConfig.TotalBytesFreed = currentConfig.TotalBytesFreed;
        updatedConfig.TotalItemsDeleted = currentConfig.TotalItemsDeleted;
        updatedConfig.LastCleanupTimestamp = currentConfig.LastCleanupTimestamp;
        updatedConfig.ConfigVersion = currentConfig.ConfigVersion;

        plugin.UpdateConfiguration(updatedConfig);

        PluginLogService.LogInfo("API", "Plugin configuration updated.", _logger);
        return Ok(new { message = "Configuration saved." });
    }
}
