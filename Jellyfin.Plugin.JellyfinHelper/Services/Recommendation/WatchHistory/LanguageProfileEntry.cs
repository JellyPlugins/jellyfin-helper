namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

/// <summary>
///     Tracks how often a user chose or was forced to use a specific audio language.
///     "Chosen" means the user actively selected this language when alternatives were available.
///     "Forced" means this was the only audio language on the item.
///     This distinction is critical: watching English-only content does not mean the user
///     prefers English — it means they tolerate it when no other option exists.
/// </summary>
public sealed class LanguageProfileEntry
{
    /// <summary>
    ///     Gets or sets how many times the user actively chose this language
    ///     when other audio languages were available on the same item.
    ///     This is a strong preference signal.
    /// </summary>
    public int ChosenCount { get; set; }

    /// <summary>
    ///     Gets or sets how many times the user watched content in this language
    ///     because it was the only audio option available.
    ///     This is a weak tolerance signal, not a preference signal.
    /// </summary>
    public int ForcedCount { get; set; }

    /// <summary>
    ///     Gets the weighted preference score. Chosen counts at full weight,
    ///     forced counts at 25% weight (tolerance, not preference).
    /// </summary>
    public double WeightedScore => ChosenCount + (ForcedCount * 0.25);
}