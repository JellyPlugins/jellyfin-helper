namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
/// Represents the result of a single link file inspection.
/// </summary>
public class LinkFileResult
{
    /// <summary>
    /// Gets the path to the link file (.strm or symlink).
    /// </summary>
    public string LinkFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the original target path read from the link file.
    /// </summary>
    public string OriginalTargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new target path that was found as a replacement.
    /// Null if no replacement was found or if the original path is still valid.
    /// </summary>
    public string? NewTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the status of this link file.
    /// </summary>
    public LinkFileStatus Status { get; set; }
}