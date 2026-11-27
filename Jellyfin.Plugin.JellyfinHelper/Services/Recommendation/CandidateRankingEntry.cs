using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>A detached initial-ranking observation, not an independent acceptance or rejection label.</summary>
public sealed record CandidateRankingEntry
{
    /// <summary>Gets the library item identity.</summary>
    public Guid ItemId { get; init; }

    /// <summary>Gets the captured display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the provider title identity, or zero when unavailable.</summary>
    public int TmdbId { get; init; }

    /// <summary>Gets the item type; movie and TV provider namespaces remain distinct.</summary>
    public string ItemType { get; init; } = string.Empty;

    /// <summary>Gets the score order before diversity and exploration.</summary>
    public int InitialRank { get; init; }

    /// <summary>Gets the initial final score, or null for an invalid prediction. Selected snapshots may be rescored after a metadata refresh.</summary>
    public double? InitialScore { get; init; }

    /// <summary>Gets the derived pre-content score, including the existing genre penalty; null means this scorer's context semantics are unknown.</summary>
    public double? PreContentScore { get; init; }

    /// <summary>Gets captured coherent narrative support; null is neutral or unavailable.</summary>
    public double? ContentFitSupport { get; init; }

    /// <summary>Gets the captured soft personal audience mismatch; null is neutral or unavailable.</summary>
    public double? AudienceMismatch { get; init; }

    /// <summary>Gets the captured mismatch with positively evidenced content families, independent of age permission.</summary>
    public double? ContentFamilyMismatch { get; init; }

    /// <summary>Gets captured support for the next unfinished, playable collection member; null means neutral or unavailable.</summary>
    public double? CollectionContinuationSupport { get; init; }

    /// <summary>Gets the actual selection status, including a captured content-family mismatch before diversity or exploration.</summary>
    public string SelectionStage { get; init; } = string.Empty;
}
