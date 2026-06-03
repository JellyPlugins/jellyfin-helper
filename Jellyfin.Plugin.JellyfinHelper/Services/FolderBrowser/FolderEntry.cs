namespace Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;

/// <summary>
///     Represents a single directory entry in the folder browser.
/// </summary>
public class FolderEntry
{
    /// <summary>
    ///     Gets or sets the display name of the directory (just the folder name, not full path).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the full absolute path of the directory.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether this directory contains subdirectories.
    ///     Used by the UI to show expand indicators.
    /// </summary>
    public bool HasChildren { get; set; }
}