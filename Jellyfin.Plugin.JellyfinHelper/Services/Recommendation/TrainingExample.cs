namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     A labelled training example for strategies that support learning.
/// </summary>
public sealed class TrainingExample
{
    /// <summary>Gets or sets the feature signals for this example.</summary>
    public required CandidateFeatures Features { get; set; }

    /// <summary>
    ///     Gets or sets the label: 1.0 = user watched/liked this item, 0.0 = user skipped/ignored.
    /// </summary>
    public double Label { get; set; }
}