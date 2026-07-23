namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Response for DELETE /JellyfinHelper/Trash.</summary>
public sealed class TrashDeleteResponse
{
    /// <summary>Gets or sets the number of items successfully deleted.</summary>
    public int Deleted { get; set; }

    /// <summary>Gets or sets the number of items that failed to delete.</summary>
    public int Failed { get; set; }
}
