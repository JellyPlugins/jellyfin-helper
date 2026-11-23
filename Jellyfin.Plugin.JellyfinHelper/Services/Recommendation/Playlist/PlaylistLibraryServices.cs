using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>Shares current library configuration and virtual-media detection for playlist delivery.</summary>
/// <param name="Configuration">Current excluded-library configuration.</param>
/// <param name="VirtualItems">Shared virtual-media detector.</param>
public sealed record PlaylistLibraryServices(IPluginConfigurationService? Configuration, VirtualItemDetector? VirtualItems)
{
    /// <summary>Gets the optional authoritative linked-child service used for collection-only movie identities.</summary>
    public ILinkedChildrenService? LinkedChildren { get; init; }

    /// <summary>Gets the host-side owner inventory, deliberately independent of viewer visibility.</summary>
    public Func<Guid, IReadOnlyList<BaseItem>>? OwnedPlaylists { get; init; }

    /// <summary>Gets the durable journal directory owned by the plugin data folder.</summary>
    public string? RecoveryDirectory { get; init; }
}
