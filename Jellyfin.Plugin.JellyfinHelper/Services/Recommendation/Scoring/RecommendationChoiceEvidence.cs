using System;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>A revealed choice among previously available titles, not an assertion that alternatives were disliked.</summary>
public sealed record RecommendationChoiceEvidence
{
    internal const int MaximumAlternatives = 20;

    /// <summary>Gets the stable chosen user/title identity; repeated runs and episodes do not create new evidence.</summary>
    public Guid ChosenObservationId { get; init; }

    /// <summary>Gets the original physical item supplying the completed outcome, including the specific episode when applicable.</summary>
    public Guid? ChosenItemId { get; init; }

    /// <summary>Gets the earlier recommendation instant defining the available alternatives.</summary>
    public DateTime RecommendedAtUtc { get; init; }

    /// <summary>Gets the known time the selected material was available in the library.</summary>
    public DateTime AvailableAtUtc { get; init; }

    /// <summary>Gets the subsequently completed watch instant.</summary>
    public DateTime ChosenAtUtc { get; init; }

    /// <summary>Gets the chosen title's outcome-free features at the recommendation boundary.</summary>
    public required CandidateFeatures ChosenFeatures { get; init; }

    /// <summary>Gets detached previously recommended alternatives, without negative outcome labels.</summary>
    public ImmutableArray<RecommendationChoiceAlternative> Alternatives { get; init; }

    internal bool IsValid(Guid observationId, DateTime referenceTime)
        => observationId != Guid.Empty && ChosenObservationId == observationId && ChosenFeatures is not null
            && HasValidTime(referenceTime) && HasValidAlternatives(observationId);

    private bool HasValidTime(DateTime referenceTime)
        => RecommendedAtUtc > DateTime.UnixEpoch && AvailableAtUtc > DateTime.UnixEpoch
            && AvailableAtUtc <= RecommendedAtUtc && ChosenAtUtc > RecommendedAtUtc && ChosenAtUtc <= referenceTime
            && (ChosenAtUtc - RecommendedAtUtc).TotalDays <= EngineConstants.RecommendationInfluenceWindowDays;

    private bool HasValidAlternatives(Guid observationId)
        => !Alternatives.IsDefaultOrEmpty && Alternatives.Length <= MaximumAlternatives
            && Alternatives.All(alternative => alternative is not null && alternative.ObservationId != Guid.Empty
                && alternative.ObservationId != observationId && alternative.Features is not null)
            && Alternatives.Select(alternative => alternative.ObservationId).Distinct().Count() == Alternatives.Length;

    internal RecommendationChoiceEvidence Copy()
        => this with
        {
            ChosenFeatures = ChosenFeatures.Copy(),
            Alternatives = Alternatives.Select(alternative => alternative with { Features = alternative.Features.Copy() }).ToImmutableArray()
        };
}
