using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>One atomic accepted model version. Parameter schemas remain v5; transaction metadata is additive.</summary>
internal sealed class AcceptedModelBundle
{
    /// <summary>Gets or sets the compound-model metadata version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the accepted learned coordinates.</summary>
    public LearnedScoringStrategy.WeightsData Learned { get; set; } = new();

    /// <summary>Gets or sets the pinned Neural version, including borrowed personal-model coordinates.</summary>
    public NeuralScoringStrategy.NeuralWeightsData? Neural { get; set; }

    /// <summary>Gets or sets the accepted complete scoring mixture.</summary>
    public AdmissionMixture Mixture { get; set; }

    /// <summary>Gets or sets the unchanged maturity/configuration ceiling at acceptance.</summary>
    public double AlphaCap { get; set; }

    /// <summary>Gets or sets the absolute Neural influence ceiling.</summary>
    public double NeuralCap { get; set; }

    /// <summary>Gets or sets the model revision; diagnostics refresh must never invent a trained revision.</summary>
    public long Revision { get; set; }

    /// <summary>Gets or sets when these accepted weights were fitted.</summary>
    public DateTime TrainedAtUtc { get; set; }

    /// <summary>Gets or sets this version's evaluated title block, or the cumulative set in pre-ledger bundles.</summary>
    public Guid[] AcceptanceObservationIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the exact external reuse ledger must exist before further admissions.</summary>
    public bool AcceptanceLedgerRequired { get; set; }

    /// <summary>Gets or sets the independently measured utility attached to this version.</summary>
    public AdmissionUtility? Utility { get; set; }

    /// <summary>Gets or sets the bounded maturity/trend metadata paired with these weights.</summary>
    public EnsembleScoringStrategy.EnsembleStateData State { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether fresh counterevidence withdrew influence without deleting weights.</summary>
    public bool Retired { get; set; }

    /// <summary>Gets or sets independent evidence that the heuristic now outperforms this incumbent.</summary>
    public AdmissionUtility? RetirementUtility { get; set; }
}
