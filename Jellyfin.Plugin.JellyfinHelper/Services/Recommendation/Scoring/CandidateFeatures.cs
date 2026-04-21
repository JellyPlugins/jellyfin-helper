using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Named indices for the feature vector produced by <see cref="CandidateFeatures.ToVector"/>.
///     Use these instead of magic numbers when accessing vector elements.
/// </summary>
public enum FeatureIndex
{
    /// <summary>Genre similarity (0–1).</summary>
    GenreSimilarity = 0,

    /// <summary>Collaborative filtering score (0–1).</summary>
    CollaborativeScore = 1,

    /// <summary>Normalized community rating (0–1).</summary>
    RatingScore = 2,

    /// <summary>Recency score (0–1).</summary>
    RecencyScore = 3,

    /// <summary>Year proximity score (0–1).</summary>
    YearProximityScore = 4,

    /// <summary>Normalized genre count (0–1).</summary>
    GenreCountNormalized = 5,

    /// <summary>Is series flag (0 or 1).</summary>
    IsSeries = 6,

    /// <summary>Genre × Rating interaction term.</summary>
    GenreRatingInteraction = 7,

    /// <summary>Genre × Collaborative interaction term.</summary>
    GenreCollabInteraction = 8,

    /// <summary>User personal rating score (0–1).</summary>
    UserRatingScore = 9,

    /// <summary>Watch completion ratio (0–1).</summary>
    CompletionRatio = 10,

    /// <summary>Abandoned flag (1 if CompletionRatio &lt; 25%, else 0). Penalizes items the user started but stopped watching early.</summary>
    IsAbandoned = 11,

    /// <summary>Novelty score (1 − GenreSimilarity). Rewards items outside the user's usual genre preferences to promote serendipity.</summary>
    NoveltyScore = 12,
}

/// <summary>
///     Pre-computed feature signals for a recommendation candidate.
///     All values are normalized to approximately 0–1 range.
/// </summary>
public sealed class CandidateFeatures
{
    /// <summary>
    ///     The number of features produced by <see cref="ToVector"/>.
    /// </summary>
    public const int FeatureCount = 13;

    /// <summary>
    ///     Normalization ceiling for genre count (items with ≥ this many genres map to 1.0).
    /// </summary>
    internal const double GenreCountNormalizationCeiling = 5.0;

    /// <summary>
    ///     Watch completion ratio below which an item is considered "abandoned".
    ///     Items with CompletionRatio &lt; this threshold have IsAbandoned = 1 in the feature vector,
    ///     which applies a negative weight penalty during scoring.
    /// </summary>
    internal const double AbandonedThreshold = 0.25;

    private double _genreSimilarity;
    private double _collaborativeScore;
    private double _ratingScore;
    private double _recencyScore;
    private double _yearProximityScore;
    private double _userRatingScore = 0.5;
    private double _completionRatio;

    /// <summary>Gets or sets the genre similarity score (0–1). Values are clamped to [0, 1].</summary>
    public double GenreSimilarity
    {
        get => _genreSimilarity;
        set => _genreSimilarity = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the collaborative filtering score (0–1). Values are clamped to [0, 1].</summary>
    public double CollaborativeScore
    {
        get => _collaborativeScore;
        set => _collaborativeScore = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the normalized community rating (0–1). Values are clamped to [0, 1].</summary>
    public double RatingScore
    {
        get => _ratingScore;
        set => _ratingScore = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the recency score (0–1, newer = higher). Values are clamped to [0, 1].</summary>
    public double RecencyScore
    {
        get => _recencyScore;
        set => _recencyScore = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the year proximity score (0–1). Values are clamped to [0, 1].</summary>
    public double YearProximityScore
    {
        get => _yearProximityScore;
        set => _yearProximityScore = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the number of genres the candidate has (raw, for interaction terms). Negative values are clamped to 0.</summary>
    public int GenreCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series (vs movie).</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets the user's personal rating score (0–1), or 0.5 if unrated. Values are clamped to [0, 1].</summary>
    public double UserRatingScore
    {
        get => _userRatingScore;
        set => _userRatingScore = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>Gets or sets the watch completion ratio (0–1). 1.0 = fully watched, 0 = not started. Values are clamped to [0, 1].</summary>
    public double CompletionRatio
    {
        get => _completionRatio;
        set => _completionRatio = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    ///     Converts the features into a fixed-size double array for ML processing.
    ///     Order is defined by <see cref="FeatureIndex"/>.
    ///     Note: This allocates a new array on each call. For hot paths, prefer
    ///     <see cref="WriteToVector(double[])"/> with a reusable buffer.
    /// </summary>
    /// <returns>An <see cref="FeatureCount"/>-element feature vector.</returns>
    public double[] ToVector()
    {
        var vector = new double[FeatureCount];
        WriteToVector(vector);
        return vector;
    }

    /// <summary>
    ///     Writes the feature values into an existing buffer to avoid allocation.
    ///     The buffer must have at least <see cref="FeatureCount"/> elements.
    /// </summary>
    /// <param name="buffer">A pre-allocated array with at least <see cref="FeatureCount"/> elements.</param>
    /// <exception cref="ArgumentException">Thrown when the buffer is too small.</exception>
    public void WriteToVector(double[] buffer)
    {
        if (buffer.Length < FeatureCount)
        {
            throw new ArgumentException(
                $"Buffer too small: need {FeatureCount} elements, got {buffer.Length}",
                nameof(buffer));
        }

        var normalizedGenreCount = Math.Clamp(GenreCount / GenreCountNormalizationCeiling, 0.0, 1.0);

        buffer[(int)FeatureIndex.GenreSimilarity] = GenreSimilarity;
        buffer[(int)FeatureIndex.CollaborativeScore] = CollaborativeScore;
        buffer[(int)FeatureIndex.RatingScore] = RatingScore;
        buffer[(int)FeatureIndex.RecencyScore] = RecencyScore;
        buffer[(int)FeatureIndex.YearProximityScore] = YearProximityScore;
        buffer[(int)FeatureIndex.GenreCountNormalized] = normalizedGenreCount;
        buffer[(int)FeatureIndex.IsSeries] = IsSeries ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.GenreRatingInteraction] = GenreSimilarity * RatingScore;
        buffer[(int)FeatureIndex.GenreCollabInteraction] = GenreSimilarity * CollaborativeScore;
        buffer[(int)FeatureIndex.UserRatingScore] = UserRatingScore;
        buffer[(int)FeatureIndex.CompletionRatio] = CompletionRatio;
        buffer[(int)FeatureIndex.IsAbandoned] = CompletionRatio < AbandonedThreshold ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.NoveltyScore] = 1.0 - GenreSimilarity;
    }
}