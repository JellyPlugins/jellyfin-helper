namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Captured provider context; only its digest is persisted, never credentials.</summary>
/// <param name="Source">The configured provider kind.</param>
/// <param name="Url">The configured endpoint.</param>
/// <param name="ApiKey">The captured protected credential or public application identity.</param>
internal sealed record MetadataSourceScope(string Source, string Url, string ApiKey);
