namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Observed-outcome screening evidence, not a distribution-free guarantee or prediction of unobserved taste.</summary>
internal sealed record AdmissionUtility(double? CandidateLoss, double? CandidateNdcg, double? LowerHeuristicImprovement, double? LowerNeuralImprovement, double? LowerIncumbentImprovement)
{
    /// <summary>Gets the evaluated mixture's macro-user Precision@10, absent when evidence is unknown.</summary>
    public double? CandidatePrecisionAtK { get; init; }

    /// <summary>Gets the evaluated mixture's macro-user Recall@10, absent when evidence is unknown.</summary>
    public double? CandidateRecallAtK { get; init; }

    /// <summary>Gets the evaluated mixture's macro-user NDCG@10; CandidateNdcg remains the admission NDCG@20.</summary>
    public double? CandidateNdcgAtK { get; init; }
}
