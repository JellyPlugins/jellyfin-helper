using System.Net.Mime;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Minimal liveness endpoint used by the plugin UI to distinguish between
///     "backend unreachable" (e.g. reverse proxy / WAF / firewall blocking the request)
///     and "backend reachable but the specific request failed" (e.g. validation error).
///     Intentionally has no dependencies and does no work beyond returning a small JSON
///     document, so it is safe to call even when other services are misconfigured.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinHelper/Ping")]
[Produces(MediaTypeNames.Application.Json)]
public class PingController : ControllerBase
{
    /// <summary>
    ///     Returns a small liveness payload. The UI calls this endpoint after a failed
    ///     mutating request to determine whether the entire backend path is blocked
    ///     (Ping also fails) or whether only the mutating request itself was rejected
    ///     (Ping succeeds). Uses the same authorization policy as the other admin-only
    ///     configuration endpoints so a successful ping is a genuine proof that the
    ///     admin's auth + routing + proxy chain is intact.
    /// </summary>
    /// <returns>A JSON object with <c>ok</c>, <c>plugin</c>, and <c>version</c> fields.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPing()
    {
        var version = typeof(PingController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(PingController).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        return Ok(new
        {
            ok = true,
            plugin = "JellyfinHelper",
            version
        });
    }
}