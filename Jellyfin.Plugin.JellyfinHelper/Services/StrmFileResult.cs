namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Represents the result of a single .strm file inspection.
/// </summary>
public class StrmFileResult
{
    /// <summary>
    /// Gets or sets the path to the .strm file.
    /// </summary>
    public string StrmFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original target path read from the .strm file.
    /// </summary>
    public string OriginalTargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new target path that was found as a replacement.
    /// Null if no replacement was found or if the original path is still valid.
    /// </summary>
    public string? NewTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the status of this .strm file.
    /// </summary>
    public StrmFileStatus Status { get; set; }
}