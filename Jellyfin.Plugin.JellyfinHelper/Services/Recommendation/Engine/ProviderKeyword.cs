namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>An immutable keyword reference; a label alone does not invent a provider identity.</summary>
/// <param name="Id">The provider keyword ID, or zero when unavailable.</param>
/// <param name="Name">The optional provider label.</param>
public readonly record struct ProviderKeyword(int Id, string? Name);
