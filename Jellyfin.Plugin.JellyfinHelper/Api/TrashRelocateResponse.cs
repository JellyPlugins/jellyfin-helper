namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for POST /JellyfinHelper/Trash/Relocate.</summary>
public sealed class TrashRelocateResponse
{
    /// <summary>Gets or sets the number of items successfully moved.</summary>
    public int Moved { get; set; }

    /// <summary>Gets or sets the number of items that failed to move.</summary>
    public int Failed { get; set; }
}
