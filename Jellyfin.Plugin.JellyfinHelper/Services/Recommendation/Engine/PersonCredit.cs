using System.Collections.Immutable;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Immutable credit identity, display name, role and derived billing; not an ML coordinate.</summary>
public sealed record PersonCredit
{
    /// <summary>Gets the original display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the actual credit role; actor/director identity is intentionally shared.</summary>
    public PersonKind Kind { get; init; }

    /// <summary>Gets corroborated provider person identities, never title IDs.</summary>
    public ImmutableArray<ProviderPersonIdentity> Identities { get; init; } = [];

    /// <summary>Gets the derived candidate billing weight, separate from personal occurrence counts.</summary>
    public double BillingWeight { get; init; }
}
