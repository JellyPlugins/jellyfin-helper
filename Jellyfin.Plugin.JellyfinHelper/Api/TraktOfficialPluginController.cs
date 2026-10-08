using System.Net.Mime;
using Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Reports whether the official Jellyfin Trakt plugin is installed and active, so the config page can offer
///     the "source through the official plugin" mode. Split out of <see cref="TraktController"/> (which validates
///     the Helper's own Trakt app): each admin Trakt concern has exactly one controller.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Trakt")]
[Produces(MediaTypeNames.Application.Json)]
public class TraktOfficialPluginController : ControllerBase
{
    private readonly IOfficialTraktPluginReader _officialPlugin;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktOfficialPluginController"/> class.
    /// </summary>
    /// <param name="officialPlugin">Reader used to report whether the official Trakt plugin is present.</param>
    public TraktOfficialPluginController(IOfficialTraktPluginReader officialPlugin)
    {
        _officialPlugin = officialPlugin;
    }

    /// <summary>
    ///     Reports whether the official Jellyfin Trakt plugin is installed and active. Admin-only (shares the
    ///     RequiresElevation policy) so plugin inventory is never exposed to normal users.
    /// </summary>
    /// <returns>A payload with a single <c>present</c> flag.</returns>
    [HttpGet("OfficialPluginStatus")]
    [ProducesResponseType(typeof(OfficialTraktPluginStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<OfficialTraktPluginStatusResponse> GetOfficialPluginStatus()
    {
        return Ok(new OfficialTraktPluginStatusResponse { Present = _officialPlugin.IsPresent() });
    }
}
