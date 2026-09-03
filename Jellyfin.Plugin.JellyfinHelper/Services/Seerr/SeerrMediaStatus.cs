namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
/// Overseerr/Jellyseerr media availability status values.
/// </summary>
/// <remarks>
/// Overseerr's values <c>MediaStatus</c> enum as surfaced on a media object's
/// <c>status</c> field: 1 = unknown, 2 = pending, 3 = processing, 4 = partially available,
/// 5 = available. This is distinct from a request's own status (pending/approved/declined);
/// a title counts as already in the library only when its status is
/// <c>PartiallyAvailable</c> or <c>Available</c>.
/// </remarks>
internal static class SeerrMediaStatus
{
    /// <summary>The media exists in the library but not every requested part is present yet.</summary>
    public const int PartiallyAvailable = 4;

    /// <summary>The media is fully available in the library.</summary>
    public const int Available = 5;
}
