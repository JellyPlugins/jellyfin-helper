using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     Represents a single media directory entry for the library insights feature. Each entry corresponds to a top-level subdirectory (movie folder or TV show folder) within a library location.
/// </summary>
public sealed class LibraryInsightEntry
{
    /// <summary>
    ///     Gets or sets the display name of the media item (directory or file name).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the total size in bytes of all files within this directory.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    ///     Gets or sets the directory creation date (UTC), representing when the media was added.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    ///     Gets or sets the last write date (UTC) of the directory.
    /// </summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>
    ///     Gets or sets the name of the library this entry belongs to.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the collection type of the library (e.g. "movies", "tvshows").
    /// </summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the change type: "added" when the item is new, "changed" when it was modified after creation.
    /// </summary>
    public string ChangeType { get; set; } = string.Empty;
}