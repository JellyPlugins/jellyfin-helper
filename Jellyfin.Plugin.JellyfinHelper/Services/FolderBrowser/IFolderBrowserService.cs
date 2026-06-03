namespace Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;

/// <summary>
///     Service interface for browsing server-side directories.
///     Used by the settings UI folder picker to allow admins to navigate
///     the server filesystem and select a valid trash folder path.
/// </summary>
public interface IFolderBrowserService
{
    /// <summary>
    ///     Gets the filesystem root directories available for browsing.
    ///     On Windows this returns drive letters; on Linux/macOS this returns "/".
    /// </summary>
    /// <returns>A browse result starting at the filesystem roots.</returns>
    FolderBrowseResult GetRoots();

    /// <summary>
    ///     Gets the immediate subdirectories of the given path.
    ///     Returns only directories the server process can read.
    /// </summary>
    /// <param name="path">The absolute directory path to list children of.</param>
    /// <returns>A browse result with the subdirectories, or an error result if the path is invalid/inaccessible.</returns>
    FolderBrowseResult GetChildren(string path);

    /// <summary>
    ///     Validates whether a path is safe and accessible for browsing.
    ///     Rejects path traversal, non-existent paths, and inaccessible directories.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <returns>Null if valid, or an error message string if invalid.</returns>
    string? ValidatePath(string path);
}