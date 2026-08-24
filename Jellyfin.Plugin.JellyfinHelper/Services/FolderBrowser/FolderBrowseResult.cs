using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;

/// <summary>
///     Result of a folder browse operation.
/// </summary>
public class FolderBrowseResult
{
    /// <summary>
    ///     Gets or sets the current directory path being browsed.
    ///     Null when showing filesystem roots.
    /// </summary>
    public string? CurrentPath { get; set; }

    /// <summary>
    ///     Gets or sets the parent directory path (for "go up" navigation).
    ///     Null when at a filesystem root.
    /// </summary>
    public string? ParentPath { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the user can navigate up from the current path.
    /// </summary>
    public bool CanGoUp { get; set; }

    /// <summary>
    ///     Gets or sets the list of subdirectories in the current path.
    /// </summary>
    public IReadOnlyList<FolderEntry> Directories { get; set; } = [];

    /// <summary>
    ///     Gets or sets an error message if the browse operation failed.
    ///     Null when the operation succeeded.
    /// </summary>
    public string? Error { get; set; }
}