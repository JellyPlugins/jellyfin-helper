using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>Loads optional Arr ownership without making filesystem-based protection depend on Arr.</summary>
public interface IArrOrphanProtectionService
{
    /// <summary>Loads a bounded snapshot for the configured, eligible instances and libraries.</summary>
    /// <param name="config">The configuration snapshot used by this scan.</param>
    /// <param name="libraries">The Jellyfin library snapshot used by this scan.</param>
    /// <param name="cancellationToken">User cancellation.</param>
    /// <returns>Positive ownership and the scoped uncertainties, never deletion authorization.</returns>
    Task<OrphanProtectionSnapshot> GetSnapshotAsync(
        PluginConfiguration config,
        IReadOnlyList<VirtualFolderInfo> libraries,
        CancellationToken cancellationToken = default);
}
