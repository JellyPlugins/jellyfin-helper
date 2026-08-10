namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for GET /JellyfinHelper/Trash/Size.</summary>
public sealed class TrashSizeResponse
{
    /// <summary>Gets or sets the total bytes used by trash items.</summary>
    public long TotalSize { get; set; }

    /// <summary>Gets or sets the total number of items in the trash.</summary>
    public int TotalItems { get; set; }
}
