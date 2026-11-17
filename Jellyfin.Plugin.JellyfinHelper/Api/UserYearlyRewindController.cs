using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Provides the caller's single stored rewind; display visibility does not delete or restrict it.</summary>
[ApiController]
[Authorize]
[Route("JellyfinHelper/YearlyRewind/My")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class UserYearlyRewindController : ControllerBase
{
    private readonly YearlyRewindService _rewind;
    private readonly IPluginConfigurationService _configuration;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly TimeProvider _clock;
    private readonly ILogger<UserYearlyRewindController> _logger;

    /// <summary>Initializes a new instance of the <see cref="UserYearlyRewindController"/> class.</summary>
    /// <param name="rewind">Persistent rewind service.</param>
    /// <param name="configuration">Feature configuration.</param>
    /// <param name="users">User directory.</param>
    /// <param name="library">Current item access checks.</param>
    /// <param name="clock">Server clock.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public UserYearlyRewindController(
        YearlyRewindService rewind,
        IPluginConfigurationService configuration,
        IUserManager users,
        ILibraryManager library,
        TimeProvider clock,
        ILogger<UserYearlyRewindController> logger)
    {
        _rewind = rewind;
        _configuration = configuration;
        _users = users;
        _library = library;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets availability and seasonal UI visibility without generating a report.</summary>
    /// <returns>The current status for the authenticated user.</returns>
    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        Response.Headers.CacheControl = "private, no-store";
        var userId = CurrentUser();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var now = _clock.GetUtcNow();
        var config = _configuration.IsInitialized ? _configuration.GetConfiguration() : null;
        var enabled = config?.YearlyRewindEnabled == true;
        var visible = enabled && YearlyRewindWindow.IsTabVisible(now, config!.YearlyRewindStartDate, config.YearlyRewindEndDate, TimeZoneInfo.Local);
        try
        {
            var report = enabled ? _rewind.Load(userId.Value) : null;
            var accessible = report is not null && CanRead(report, userId.Value);
            return Ok(new
            {
                Enabled = enabled,
                TabVisible = visible,
                Available = accessible,
                SnapshotId = accessible ? report!.SnapshotId : (Guid?)null,
                SnapshotAtUtc = accessible ? report!.SnapshotAtUtc : (DateTimeOffset?)null,
                ServerTimeUtc = now,
                TimeZoneId = YearlyRewindWindow.ClientTimeZoneId(TimeZoneInfo.Local),
                NextCheckAtUtc = enabled
                    ? YearlyRewindWindow.NextVisibilityChange(now, config!.YearlyRewindStartDate, config.YearlyRewindEndDate, TimeZoneInfo.Local)
                    : now.AddMinutes(1),
                DataState = accessible ? report!.DataState : "Unavailable"
            });
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "Could not read YearlyRewind status for user {UserId}.", userId.Value);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Gets the existing report, including outside the seasonal UI window.</summary>
    /// <returns>The caller’s stored report or the corresponding access/error result.</returns>
    [HttpGet]
    public ActionResult GetMyRewind()
    {
        Response.Headers.CacheControl = "private, no-store";
        var userId = CurrentUser();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (!_configuration.IsInitialized || !_configuration.GetConfiguration().YearlyRewindEnabled)
        {
            return Forbid();
        }

        try
        {
            var report = _rewind.Load(userId.Value);
            if (report is null)
            {
                return NotFound();
            }

            if (!CanRead(report, userId.Value))
            {
                return Forbid();
            }

            return Ok(report);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _logger.LogWarning(ex, "Could not read YearlyRewind for user {UserId}.", userId.Value);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private Guid? CurrentUser()
    {
        var id = DiscoverySupport.GetCurrentUserId(User);
        if (id is null || id == Guid.Empty)
        {
            return null;
        }

        var user = _users.GetUserById(id.Value);
        return user is not null && !user.HasPermission(PermissionKind.IsDisabled) ? id : null;
    }

    private bool CanRead(YearlyRewindSnapshot report, Guid userId)
    {
        if (report.UserId != userId)
        {
            return false;
        }

        var user = _users.GetUserById(userId)!;
        return report.Items.All(item =>
        {
            var existing = _library.GetItemById(item.ItemId);
            return existing is null || existing.IsVisible(user);
        });
    }
}
