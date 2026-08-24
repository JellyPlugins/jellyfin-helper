using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     The evaluated request permissions for a specific user and service type.
///     Returned by the permission-checking endpoint to inform the frontend whether a
///     quality profile popup is necessary and which profiles are available.
/// </summary>
public sealed class UserRequestPermissionResult
{
    /// <summary>
    ///     Gets or sets a value indicating whether the user is allowed to submit requests.
    ///     When <c>false</c>, the request should be blocked and <see cref="DeniedReason"/> explains why.
    /// </summary>
    public bool CanRequest { get; set; }

    /// <summary>
    ///     Gets or sets the human-readable reason when <see cref="CanRequest"/> is <c>false</c>.
    ///     Null when the user has permission.
    /// </summary>
    public string? DeniedReason { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the denial is due to a transient upstream failure
    ///     (e.g., Seerr server temporarily unavailable). When <c>true</c>, the client should retry
    ///     rather than treating the denial as a permanent permission issue.
    ///     Used by the controller to distinguish 503 (retry) from 403 (forbidden) responses.
    /// </summary>
    public bool IsTransient { get; set; }

    /// <summary>
    ///     Gets or sets the list of quality profiles the user is permitted to choose from.
    ///     <list type="bullet">
    ///         <item>Empty list: user should submit with server defaults (no profile override).</item>
    ///         <item>Single entry: frontend should auto-select without showing a popup.</item>
    ///         <item>Multiple entries: frontend should display a selection popup.</item>
    ///     </list>
    /// </summary>
    public IReadOnlyList<AllowedQualityProfile> Profiles { get; set; } = [];
}