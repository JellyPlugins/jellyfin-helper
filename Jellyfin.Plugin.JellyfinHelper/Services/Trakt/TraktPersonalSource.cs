namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     The resolved per-user personal source: which Trakt app (client id) and bearer token to fetch with, and
///     whether it is the user's own device-flow link (which owns the 401-driven refresh/unlink lifecycle) or the
///     read-only official-plugin token (which the Helper must never refresh or unlink).
/// </summary>
/// <param name="ClientId">The Trakt client id (app) the request is attributed to.</param>
/// <param name="AccessToken">The bearer token for the request.</param>
/// <param name="IsOwnFlow">True for the user's own device-flow link; false for the official plugin's token.</param>
public sealed record TraktPersonalSource(string ClientId, string AccessToken, bool IsOwnFlow);
