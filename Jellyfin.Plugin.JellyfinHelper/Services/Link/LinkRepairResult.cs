using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
/// Represents the overall result of a link repair run.
/// </summary>
public class LinkRepairResult
{
    /// <summary>
    /// Gets the list of individual file results.
    /// </summary>
    public Collection<LinkFileResult> FileResults { get; } = [];

    /// <summary>
    /// Gets the number of valid link files.
    /// </summary>
    public int ValidCount => FileResults.Count(r => r.Status == LinkFileStatus.Valid);

    /// <summary>
    /// Gets the number of repaired link files.
    /// </summary>
    public int RepairedCount => FileResults.Count(r => r.Status == LinkFileStatus.Repaired);

    /// <summary>
    /// Gets the number of broken link files that could not be repaired.
    /// </summary>
    public int BrokenCount => FileResults.Count(r => r.Status == LinkFileStatus.Broken);

    /// <summary>
    /// Gets the number of ambiguous link files.
    /// </summary>
    public int AmbiguousCount => FileResults.Count(r => r.Status == LinkFileStatus.Ambiguous);

    /// <summary>
    /// Gets the number of link files with invalid content.
    /// </summary>
    public int InvalidContentCount => FileResults.Count(r => r.Status == LinkFileStatus.InvalidContent);
}