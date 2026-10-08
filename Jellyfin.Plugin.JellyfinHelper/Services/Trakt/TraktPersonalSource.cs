namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     The resolved per-user personal source: which Trakt app (client id) and bearer token to fetch with. The
///     token is read-only from the official Trakt plugin; the Helper never refreshes or unlinks it.
/// </summary>
/// <param name="ClientId">The Trakt client id (app) the request is attributed to.</param>
/// <param name="AccessToken">The bearer token for the request.</param>
public sealed record TraktPersonalSource(string ClientId, string AccessToken);
