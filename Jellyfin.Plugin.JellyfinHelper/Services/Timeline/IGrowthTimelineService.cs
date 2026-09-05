using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
/// Interface for the service that computes a cumulative growth timeline
/// based on media file creation dates.
/// </summary>
public interface IGrowthTimelineService
{
    /// <summary>
    /// Computes the growth timeline by scanning top-level media directories.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The growth timeline result.</returns>
    Task<GrowthTimelineResult> ComputeTimelineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the last computed timeline from disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The cached timeline or null.</returns>
    Task<GrowthTimelineResult?> LoadTimelineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the same exclusive gate the service uses around its read-compute-write sequence.
    /// Backup export and restore hold it while reading or writing the timeline files so they cannot
    /// race a scheduled scan and clobber a concurrent write.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A disposable that releases the gate when disposed.</returns>
    Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken);
}