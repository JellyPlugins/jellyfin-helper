namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>A single library name + path entry for the folder-browser response.</summary>
public sealed class LibraryPathEntry
{
    /// <summary>Gets or sets the library display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the physical filesystem path of the library location.</summary>
    public string Path { get; set; } = string.Empty;
}
