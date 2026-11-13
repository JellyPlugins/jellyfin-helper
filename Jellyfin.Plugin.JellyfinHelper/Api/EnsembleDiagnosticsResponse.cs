using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>
///     Response for GET /JellyfinHelper/Recommendations/Diagnostics/Ensemble. Read-only view of the ensemble
///     scoring strategy's live adaptive state so operators can see which mechanism is currently active.
/// </summary>
public sealed class EnsembleDiagnosticsResponse
{
    /// <summary>
    ///     Gets or sets a value indicating whether ensemble diagnostics are available. False when the active
    ///     strategy is not an ensemble or no state has been produced yet; the remaining fields carry defaults.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets the stored ML target before quality suppression and user exploration.</summary>
    public double Alpha { get; set; }

    /// <summary>Gets or sets the selected user's requested exploration offset.</summary>
    public double AlphaOffset { get; set; }

    /// <summary>Gets or sets the effective ML coefficient used for scoring this user now.</summary>
    public double EffectiveAlpha { get; set; }

    /// <summary>Gets or sets the operative Neural fraction of ML, zero when Neural is absent.</summary>
    public double EffectiveNeuralBeta { get; set; }

    /// <summary>Gets or sets the current heuristic coefficient.</summary>
    public double HeuristicShare { get; set; }

    /// <summary>Gets or sets the current Learned coefficient.</summary>
    public double LearnedShare { get; set; }

    /// <summary>Gets or sets the current Neural coefficient.</summary>
    public double NeuralShare { get; set; }

    /// <summary>Gets or sets the accepted model revision, separate from fitting attempts.</summary>
    public long ModelRevision { get; set; }

    /// <summary>Gets or sets the latest independent admission/retention reason.</summary>
    public string? AdmissionReason { get; set; }

    /// <summary>Gets or sets unique calibration title support.</summary>
    public int CalibrationTitleCount { get; set; }

    /// <summary>Gets or sets unique independent acceptance support.</summary>
    public int AcceptanceTitleCount { get; set; }

    /// <summary>Gets or sets observed candidate loss, null when no usable acceptance measurement exists.</summary>
    public double? CandidateValidationLoss { get; set; }

    /// <summary>Gets or sets the current neural blending factor beta (fraction of the ML budget allocated to the neural strategy).</summary>
    public double NeuralBeta { get; set; }

    /// <summary>Gets or sets a value indicating whether alpha progression is currently frozen by the quality gate.</summary>
    public bool QualityGateFrozen { get; set; }

    /// <summary>Gets or sets the adaptive sigmoid midpoint offset (negative trusts ML sooner, positive is more conservative).</summary>
    public double SigmoidMidpointOffset { get; set; }

    /// <summary>Gets or sets the effective sigmoid midpoint (default midpoint plus offset).</summary>
    public double EffectiveSigmoidMidpoint { get; set; }

    /// <summary>Gets or sets the detected training-quality trend as its string name (e.g. "Improving", "Stable").</summary>
    public string Trend { get; set; } = EnsembleScoringStrategy.MetricsTrend.InsufficientData.ToString();

    /// <summary>Gets or sets the cumulative number of training examples seen so far.</summary>
    public int TrainingExampleCount { get; set; }

    /// <summary>Gets or sets the number of metrics snapshots currently retained in the rolling history.</summary>
    public int MetricsHistoryCount { get; set; }

    /// <summary>Gets or sets the minimum blending factor alpha (heuristic-dominant lower bound).</summary>
    public double AlphaMin { get; set; }

    /// <summary>Gets or sets the maximum blending factor alpha (ML-dominant upper bound).</summary>
    public double AlphaMax { get; set; }

    /// <summary>Gets or sets a value indicating whether a neural sub-strategy was constructed for this ensemble.</summary>
    public bool NeuralEnabled { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether these diagnostics describe a per-user model. False means
    ///     the values come from the shared global model (the user has no dedicated model yet / cold-start).
    /// </summary>
    public bool IsPerUser { get; set; }

    /// <summary>
    ///     Gets or sets the display name of the user these diagnostics describe, when the request was scoped
    ///     to a user. Null for the global (non-user-scoped) snapshot.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    ///     Maps an <see cref="EnsembleDiagnostics"/> snapshot to a populated response with <see cref="Available"/> set to true.
    /// </summary>
    /// <param name="diagnostics">The ensemble diagnostics snapshot to map.</param>
    /// <returns>A populated <see cref="EnsembleDiagnosticsResponse"/>.</returns>
    public static EnsembleDiagnosticsResponse FromDiagnostics(EnsembleDiagnostics diagnostics) =>
        FromDiagnostics(diagnostics, isPerUser: false, userName: null);

    /// <summary>
    ///     Maps an <see cref="EnsembleDiagnostics"/> snapshot to a populated response, tagging whether it is a
    ///     per-user model and for which user.
    /// </summary>
    /// <param name="diagnostics">The ensemble diagnostics snapshot to map.</param>
    /// <param name="isPerUser">Whether the snapshot describes a per-user model.</param>
    /// <param name="userName">The display name of the scoped user, or null.</param>
    /// <returns>A populated <see cref="EnsembleDiagnosticsResponse"/>.</returns>
    public static EnsembleDiagnosticsResponse FromDiagnostics(
        EnsembleDiagnostics diagnostics,
        bool isPerUser,
        string? userName)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new EnsembleDiagnosticsResponse
        {
            Available = true,
            Alpha = diagnostics.Alpha,
            AlphaOffset = diagnostics.AlphaOffset,
            EffectiveAlpha = diagnostics.EffectiveAlpha,
            EffectiveNeuralBeta = diagnostics.EffectiveNeuralBeta,
            HeuristicShare = diagnostics.HeuristicShare,
            LearnedShare = diagnostics.LearnedShare,
            NeuralShare = diagnostics.NeuralShare,
            ModelRevision = diagnostics.ModelRevision,
            AdmissionReason = diagnostics.AdmissionReason,
            CalibrationTitleCount = diagnostics.CalibrationTitleCount,
            AcceptanceTitleCount = diagnostics.AcceptanceTitleCount,
            CandidateValidationLoss = diagnostics.CandidateValidationLoss,
            NeuralBeta = diagnostics.NeuralBeta,
            QualityGateFrozen = diagnostics.QualityGateFrozen,
            SigmoidMidpointOffset = diagnostics.SigmoidMidpointOffset,
            EffectiveSigmoidMidpoint = diagnostics.EffectiveSigmoidMidpoint,
            Trend = diagnostics.Trend.ToString(),
            TrainingExampleCount = diagnostics.TrainingExampleCount,
            MetricsHistoryCount = diagnostics.MetricsHistoryCount,
            AlphaMin = diagnostics.AlphaMin,
            AlphaMax = diagnostics.AlphaMax,
            NeuralEnabled = diagnostics.NeuralEnabled,
            IsPerUser = isPerUser,
            UserName = userName
        };
    }
}
