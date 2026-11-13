using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Attempt diagnostics and bounded unique-observation maturity, never an active model checkpoint.</summary>
internal sealed class ModelTrainingAssessment
{
    /// <summary>Gets or sets the assessment metadata version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets when this assessment was produced, not when incumbent weights were trained.</summary>
    public DateTime EvaluatedAtUtc { get; set; }

    /// <summary>Gets or sets the bounded distinct fitting observation identities.</summary>
    public Guid[] ObservedTrainingIds { get; set; } = [];

    /// <summary>Gets or sets the latest independent challenger decision.</summary>
    public AdmissionDecision? Decision { get; set; }

    /// <summary>Gets or sets the cumulative distinct observed fitting count.</summary>
    public int TrainingExampleCount { get; set; }

    /// <summary>Gets or sets a value indicating whether validated mode must survive even without an accepted checkpoint.</summary>
    public bool IndependentAdmissionEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether an unadmitted/unknown legacy fallback must remain heuristic-only on restart.</summary>
    public bool HeuristicFallbackRequired { get; set; }

    /// <summary>Gets or sets a value indicating whether losing the accepted bundle must not recycle a missing spent-identity ledger.</summary>
    public bool AcceptanceLedgerRequired { get; set; }

    /// <summary>Gets or sets the accepted revision observed by this assessment, preventing an older journal from overriding a newer commit.</summary>
    public long AcceptedModelRevision { get; set; }

    /// <summary>Gets or sets a value indicating whether fresh counterevidence suppressed a legacy v5 model without overwriting it.</summary>
    public bool LegacyModelSuppressed { get; set; }

    /// <summary>Gets or sets actual independent evidence for legacy withdrawal, not a fabricated original acceptance.</summary>
    public AdmissionUtility? LegacyWithdrawalUtility { get; set; }

    /// <summary>Gets or sets the observed independent block supporting legacy suppression.</summary>
    public Guid[] LegacyWithdrawalObservationIds { get; set; } = [];

    /// <summary>Gets or sets at most ten actual mixture assessments, including explicitly unknown metrics.</summary>
    public List<EnsembleScoringStrategy.MetricsSnapshot> MetricsHistory { get; set; } = [];
}
