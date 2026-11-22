using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;

/// <summary>Shares current library configuration and virtual-media detection for playlist delivery.</summary>
/// <param name="Configuration">Current excluded-library configuration.</param>
/// <param name="VirtualItems">Shared virtual-media detector.</param>
public sealed record PlaylistLibraryServices(IPluginConfigurationService? Configuration, VirtualItemDetector? VirtualItems);
