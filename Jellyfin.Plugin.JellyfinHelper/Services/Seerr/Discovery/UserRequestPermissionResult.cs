using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     The evaluated request permissions for a specific user and service type. Returned by the permission-checking endpoint to inform the frontend whether a quality profile popup is necessary and which profiles are available.
/// </summary>
public sealed class UserRequestPermissionResult
{
    /// <summary>
    ///     Gets or sets a value indicating whether the user is allowed to submit requests. When false, the request should be blocked and DeniedReason explains why.
    /// </summary>
    public bool CanRequest { get; set; }

    /// <summary>
    ///     Gets or sets the human-readable reason when <see cref="CanRequest"/> is <c>false</c>.
    ///     Null when the user has permission.
    /// </summary>
    public string? DeniedReason { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the denial is due to a transient upstream failure (e.g., Seerr server temporarily unavailable).
    /// </summary>
    public bool IsTransient { get; set; }

    /// <summary>
    ///     Gets or sets the list of quality profiles the user is permitted to choose from. Empty list: user should submit with server defaults (no profile override).
    /// </summary>
    public IReadOnlyList<AllowedQualityProfile> Profiles { get; set; } = [];
}