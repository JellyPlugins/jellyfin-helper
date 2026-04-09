namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Status of a .strm file inspection.
/// </summary>
public enum StrmFileStatus
{
    /// <summary>
    /// The target path in the .strm file is valid.
    /// </summary>
    Valid,

    /// <summary>
    /// The target path was broken and has been repaired (or would be in dry-run mode).
    /// </summary>
    Repaired,

    /// <summary>
    /// The target path is broken but no replacement could be found.
    /// </summary>
    Broken,

    /// <summary>
    /// The target path is broken and multiple candidates were found (ambiguous).
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The .strm file is empty or contains invalid content.
    /// </summary>
    InvalidContent,
}