using System.Net.Mime;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Minimal liveness endpoint used by the plugin UI to distinguish between "backend unreachable" (e.g.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Ping")]
[Produces(MediaTypeNames.Application.Json)]
public class PingController : ControllerBase
{
    /// <summary>
    ///     Returns a small liveness payload. The UI calls this endpoint after a failed mutating request to determine whether the entire backend path is blocked (Ping also fails) or whether only the mutating request itself was rejected (Ping succeeds).
    /// </summary>
    /// <returns>A JSON object with <c>ok</c>, <c>plugin</c>, and <c>version</c> fields.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PingResponse), StatusCodes.Status200OK)]
    public ActionResult GetPing()
    {
        var version = typeof(PingController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(PingController).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        return Ok(new PingResponse { Ok = true, Plugin = "JellyfinHelper", Version = version });
    }
}