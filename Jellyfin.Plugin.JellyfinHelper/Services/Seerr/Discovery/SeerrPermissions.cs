using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Defines the known permission flags used by Overseerr/Jellyseerr.
///     These are bitmask values stored in <see cref="SeerrUser.Permissions"/>.
///     A user's effective permissions are the bitwise OR of all their granted flags.
///     Values sourced from Overseerr server/lib/permissions.ts (sct/overseerr@develop, 2024-12).
///     Also verified against Fallenbagel/jellyseerr@develop (same values).
///     Re-validate against upstream when upgrading target Seerr compatibility.
/// </summary>
[Flags]
public enum SeerrPermissions : long
{
    /// <summary>No permissions granted.</summary>
    None = 0,

    /// <summary>Full administrator access - implies all other permissions.</summary>
    Admin = 2,

    /// <summary>Can manage users (create, modify, delete).</summary>
    ManageUsers = 8,

    /// <summary>Can manage (approve/deny/modify) other users' requests.</summary>
    ManageRequests = 16,

    /// <summary>Can submit requests (movies + TV).</summary>
    Request = 32,

    /// <summary>Can vote on media requests (approval workflow).</summary>
    Vote = 64,

    /// <summary>Can auto-approve their own requests (bypasses admin review).</summary>
    AutoApprove = 128,

    /// <summary>Can auto-approve movie requests specifically.</summary>
    AutoApproveMovie = 256,

    /// <summary>Can auto-approve TV requests specifically.</summary>
    AutoApproveTv = 512,

    /// <summary>Can submit movie requests (granular).</summary>
    RequestMovie = 1024,

    /// <summary>Can submit TV requests (granular).</summary>
    RequestTv = 2048,

    /// <summary>Can manage all issues/reports.</summary>
    ManageIssues = 4096,

    /// <summary>Can view other users' issues.</summary>
    ViewIssues = 8192,

    /// <summary>Can create issues/reports.</summary>
    CreateIssues = 16384,

    /// <summary>Can auto-approve 4K requests.</summary>
    AutoApprove4K = 32768,

    /// <summary>Can auto-approve 4K movie requests specifically.</summary>
    AutoApprove4KMovie = 65536,

    /// <summary>Can auto-approve 4K TV requests specifically.</summary>
    AutoApprove4KTv = 131072,

    /// <summary>Can submit 4K requests.</summary>
    Request4K = 262144,

    /// <summary>Can submit 4K movie requests (granular).</summary>
    Request4KMovie = 524288,

    /// <summary>Can submit 4K TV requests (granular).</summary>
    Request4KTv = 1048576,

    /// <summary>Indicates the user uses the "advanced" request flow (can select quality profile).</summary>
    RequestAdvanced = 2097152,

    /// <summary>Can manage DNS settings.</summary>
    ManageDns = 4194304,

    /// <summary>Can view the watchlist feature.</summary>
    Watchlist = 8388608,

    /// <summary>Can view recent requests on the dashboard.</summary>
    RecentView = 16777216,

    /// <summary>Can auto-request media (automatic request based on watchlist).</summary>
    AutoRequest = 33554432,

    /// <summary>Can auto-request movies specifically.</summary>
    AutoRequestMovie = 67108864,

    /// <summary>Can auto-request TV shows specifically.</summary>
    AutoRequestTv = 134217728,

    /// <summary>Can view the watchlist view page.</summary>
    WatchlistView = 268435456,
}
