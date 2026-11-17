using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Calibration-only serving measurements, separate from the preserved independent admission evidence and utility.</summary>
/// <param name="IndependentReason">The actual independent evidence limitation, never a provisional approval.</param>
/// <param name="IndependentCandidate">The candidate selected exclusively by independent calibration, when available.</param>
/// <param name="MaturityTarget">The requested maturity-bounded mixture before quality damping.</param>
/// <param name="MaturityTargetCalibration">Component losses measured for exactly that target.</param>
/// <param name="AttemptedMixture">The damped/approached mixture tested before any retained-parameter fallback.</param>
/// <param name="AttemptedCalibration">Component losses measured for exactly that attempt.</param>
/// <param name="SelectedCalibration">The final calibration measurements paired with the decision's serving mixture.</param>
internal sealed record ProvisionalServingAssessment(
    [property: JsonRequired] string IndependentReason,
    [property: JsonRequired] AdmissionMixture IndependentCandidate,
    [property: JsonRequired] AdmissionMixture MaturityTarget,
    [property: JsonRequired] EnsembleAdmissionEvaluator.ServingCalibration MaturityTargetCalibration,
    [property: JsonRequired] AdmissionMixture AttemptedMixture,
    [property: JsonRequired] EnsembleAdmissionEvaluator.ServingCalibration AttemptedCalibration,
    [property: JsonRequired] EnsembleAdmissionEvaluator.ServingCalibration SelectedCalibration)
{
    internal bool Valid()
        => IndependentReason is "HomogeneousObservedOutcomes" or "InsufficientIndependentRankingEvidence"
            && ValidMixture(IndependentCandidate) && ValidMixture(MaturityTarget) && ValidMixture(AttemptedMixture)
            && ValidCalibration(MaturityTargetCalibration) && ValidCalibration(AttemptedCalibration) && ValidCalibration(SelectedCalibration);

    private static bool ValidMixture(AdmissionMixture mixture)
        => double.IsFinite(mixture.Learned) && mixture.Learned >= 0 && double.IsFinite(mixture.Neural) && mixture.Neural >= 0 && mixture.Alpha <= 1 + 1e-12;

    private static bool ValidCalibration(EnsembleAdmissionEvaluator.ServingCalibration? calibration)
        => calibration is not null && calibration.Titles >= LearnedScoringStrategy.MinExamplesPerFold
            && ValidLoss(calibration.MixtureLoss) && ValidLoss(calibration.LearnedLoss) && ValidLoss(calibration.HeuristicLoss)
            && (calibration.NeuralLoss is null || ValidLoss(calibration.NeuralLoss.Value))
            && (calibration.IncumbentLoss is null || ValidLoss(calibration.IncumbentLoss.Value));

    private static bool ValidLoss(double value) => double.IsFinite(value) && value is >= 0 and <= 1 + 1e-12;
}
