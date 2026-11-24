namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>One provider-qualified production or broadcast entity; labels are presentation, never identity.</summary>
/// <param name="Provider">The entity ID namespace, independent of the acquisition server.</param>
/// <param name="Kind">The provider entity kind, for example production-company or network.</param>
/// <param name="Id">The positive provider entity ID.</param>
/// <param name="Name">An optional localized label, not an identity key.</param>
public sealed record ProviderStudioReference(string Provider, string Kind, int Id, string? Name);
