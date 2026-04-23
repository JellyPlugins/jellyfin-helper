using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     A labelled training example for strategies that support learning.
/// </summary>
public sealed class TrainingExample
{
    /// <summary>
    ///     Half-life for temporal decay weighting in days (~90 days).
    ///     Examples older than this receive exponentially less weight during training.
    /// </summary>
    internal const double TemporalDecayHalfLifeDays = 90.0;

    /// <summary>Decay constant derived from half-life: ln(2) / halfLife.</summary>
    internal static readonly double TemporalDecayConstant = Math.Log(2.0) / TemporalDecayHalfLifeDays;

    private double _label;
    private double _sampleWeight = 1.0;
    private DateTime _generatedAtUtc = DateTime.UtcNow;

    /// <summary>Gets or sets the feature signals for this example.</summary>
    public required CandidateFeatures Features { get; set; }

    /// <summary>
    ///     Gets or sets the label: 1.0 = user watched/liked this item, 0.0 = user skipped/ignored.
    ///     Values are clamped to [0, 1].
    /// </summary>
    public double Label
    {
        get => _label;
        set => _label = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    ///     Gets or sets the sample weight for this training example (0–1).
    ///     Higher weights mean the example has more influence during training.
    ///     Default is 1.0. Values are clamped to [0, 1].
    /// </summary>
    public double SampleWeight
    {
        get => _sampleWeight;
        set => _sampleWeight = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    }

    /// <summary>
    ///     Gets or sets the UTC timestamp when the recommendation that produced this example was generated.
    ///     Used for temporal decay weighting — newer examples are more relevant.
    ///     Values are normalized to <see cref="DateTimeKind.Utc"/> on assignment.
    /// </summary>
    public DateTime GeneratedAtUtc
    {
        get => _generatedAtUtc;
        set => _generatedAtUtc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    ///     Computes a temporal decay weight based on the age of this example.
    ///     Newer examples get weight closer to 1.0, older examples decay exponentially.
    /// </summary>
    /// <param name="referenceTimeUtc">
    ///     The reference point in time to compute age against.
    ///     If <c>null</c>, <see cref="DateTime.UtcNow"/> is used.
    ///     Passing an explicit value ensures consistent weights within a single training batch.
    /// </param>
    /// <returns>A decay weight between 0 and 1.</returns>
    public double ComputeTemporalWeight(DateTime? referenceTimeUtc = null)
    {
        var reference = referenceTimeUtc ?? DateTime.UtcNow;
        var ageDays = (reference - GeneratedAtUtc).TotalDays;
        if (ageDays <= 0)
        {
            return 1.0;
        }

        return Math.Exp(-TemporalDecayConstant * ageDays);
    }

    /// <summary>
    ///     Computes the effective weight for this example, combining the explicit sample weight
    ///     with the temporal decay weight.
    /// </summary>
    /// <param name="referenceTimeUtc">
    ///     The reference point in time for temporal decay.
    ///     If <c>null</c>, <see cref="DateTime.UtcNow"/> is used.
    /// </param>
    /// <returns>The effective weight between 0 and 1.</returns>
    public double ComputeEffectiveWeight(DateTime? referenceTimeUtc = null)
    {
        return SampleWeight * ComputeTemporalWeight(referenceTimeUtc);
    }
}