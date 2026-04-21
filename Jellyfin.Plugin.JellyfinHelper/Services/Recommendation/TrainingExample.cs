using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     A labelled training example for strategies that support learning.
/// </summary>
public sealed class TrainingExample
{
    private double _label;

    /// <summary>Gets or sets the feature signals for this example.</summary>
    public required CandidateFeatures Features { get; set; }

    /// <summary>
    ///     Gets or sets the label: 1.0 = user watched/liked this item, 0.0 = user skipped/ignored.
    ///     Values are clamped to [0, 1].
    /// </summary>
    public double Label
    {
        get => _label;
        set => _label = Math.Clamp(value, 0.0, 1.0);
    }
}