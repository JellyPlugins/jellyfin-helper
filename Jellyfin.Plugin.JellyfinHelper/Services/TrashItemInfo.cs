using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Represents a single item in the trash folder with metadata for display.
/// </summary>
public class TrashItemInfo
{
    /// <summary>
    /// Gets or sets the original name of the item (without timestamp prefix).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full name including the timestamp prefix.
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size of the item in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this item is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the item was moved to trash.
    /// Null if the timestamp could not be parsed.
    /// </summary>
    public DateTime? TrashedAt { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the item will be permanently purged.
    /// Null if the trashed-at timestamp could not be parsed.
    /// </summary>
    public DateTime? PurgesAt { get; set; }
}