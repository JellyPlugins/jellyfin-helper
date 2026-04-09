using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Represents the overall result of a .strm repair run.
/// </summary>
public class StrmRepairResult
{
    /// <summary>
    /// Gets the list of individual file results.
    /// </summary>
    public Collection<StrmFileResult> FileResults { get; } = new();

    /// <summary>
    /// Gets the number of valid .strm files.
    /// </summary>
    public int ValidCount => FileResults.Count(r => r.Status == StrmFileStatus.Valid);

    /// <summary>
    /// Gets the number of repaired .strm files.
    /// </summary>
    public int RepairedCount => FileResults.Count(r => r.Status == StrmFileStatus.Repaired);

    /// <summary>
    /// Gets the number of broken .strm files that could not be repaired.
    /// </summary>
    public int BrokenCount => FileResults.Count(r => r.Status == StrmFileStatus.Broken);

    /// <summary>
    /// Gets the number of ambiguous .strm files.
    /// </summary>
    public int AmbiguousCount => FileResults.Count(r => r.Status == StrmFileStatus.Ambiguous);

    /// <summary>
    /// Gets the number of .strm files with invalid content.
    /// </summary>
    public int InvalidContentCount => FileResults.Count(r => r.Status == StrmFileStatus.InvalidContent);
}