using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>A captured alternative in the same library opportunity; it has no observed rejection label.</summary>
/// <param name="ObservationId">The stable user/title identity.</param>
/// <param name="Features">The original detached prediction inputs.</param>
public sealed record RecommendationChoiceAlternative(Guid ObservationId, CandidateFeatures Features)
{
    /// <summary>Gets the original recommended library copy, so current access checks cannot admit a hidden edition through a shared provider ID.</summary>
    public Guid? LibraryItemId { get; init; }
}
