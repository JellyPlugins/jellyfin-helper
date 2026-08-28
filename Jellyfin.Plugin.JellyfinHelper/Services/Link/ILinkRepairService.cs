using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Link;

/// <summary>
///     Interface for the service that finds and repairs broken link references
///     (.strm files and symbolic links).
/// </summary>
public interface ILinkRepairService
{
    /// <summary>
    ///     Scans the given library paths for links (.strm files and symlinks), validates their target paths, and repairs broken references by searching the parent directory for a media file.
    /// </summary>
    /// <param name="libraryPaths">The library paths to scan for links (.strm files and symlinks).</param>
    /// <param name="dryRun">If true, no files will be modified.</param>
    /// <param name="cancellationToken">Cancellation token to stop the operation.</param>
    /// <returns>The result of the repair operation.</returns>
    LinkRepairResult RepairLinks(
        IEnumerable<string> libraryPaths,
        bool dryRun,
        CancellationToken cancellationToken = default);
}