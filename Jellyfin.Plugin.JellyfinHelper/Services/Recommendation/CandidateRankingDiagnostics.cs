using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>Bounded prediction-time ranking information, never outcomes or additional training examples.</summary>
public sealed record CandidateRankingDiagnostics
{
    /// <summary>Gets the number of scored, deduplicated candidates before selection.</summary>
    public int ScoredCandidateCount { get; init; }

    /// <summary>Gets a value indicating whether every scored candidate is represented; absence is inconclusive when false.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Gets the retained scalar ranking entries. Full feature vectors remain limited to selected items and existing contrasts.</summary>
    public IReadOnlyList<CandidateRankingEntry> Entries { get; init; } = [];
}
