namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>A permanent user dismissal identity, containing neither prediction inputs nor personal history.</summary>
/// <param name="TmdbId">The dismissed provider title.</param>
/// <param name="MediaType">The movie/TV namespace.</param>
public sealed record DiscoveryDismissedItem(int TmdbId, string MediaType);
