namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>A single library entry for the available-libraries response.</summary>
public sealed class LibraryEntry
{
    /// <summary>Gets or sets the library display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type (e.g. "movies", "tvshows", "unknown").</summary>
    public string CollectionType { get; set; } = string.Empty;
}
