namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>A provider person identity; title, company and network IDs never inhabit this namespace.</summary>
/// <param name="Provider">The canonical provider namespace.</param>
/// <param name="Id">The provider's person identifier.</param>
public readonly record struct ProviderPersonIdentity(string Provider, string Id);
