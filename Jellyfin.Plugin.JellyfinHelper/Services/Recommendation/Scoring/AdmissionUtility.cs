namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Observed-outcome screening evidence, not a distribution-free guarantee or prediction of unobserved taste.</summary>
internal sealed record AdmissionUtility(double? CandidateLoss, double? CandidateNdcg, double? LowerHeuristicImprovement, double? LowerNeuralImprovement, double? LowerIncumbentImprovement)
{
    /// <summary>Gets or initializes a value indicating whether utility tests revealed choices rather than absolute positive/negative discrimination.</summary>
    public bool IsChoiceRanking { get; init; }

    /// <summary>Gets relative choice loss; absent on ordinary observed-outcome assessments.</summary>
    public double? ChoiceRankingLoss { get; init; }

    /// <summary>Gets macro-user retrieval of the chosen title at K; alternatives are not asserted to be disliked.</summary>
    public double? ChoiceRecallAtK { get; init; }

    /// <summary>Gets the evaluated mixture's macro-user Precision@10, absent when evidence is unknown.</summary>
    public double? CandidatePrecisionAtK { get; init; }

    /// <summary>Gets the evaluated mixture's macro-user Recall@10, absent when evidence is unknown.</summary>
    public double? CandidateRecallAtK { get; init; }

    /// <summary>Gets the evaluated mixture's macro-user NDCG@10; CandidateNdcg remains the admission NDCG@20.</summary>
    public double? CandidateNdcgAtK { get; init; }
}
