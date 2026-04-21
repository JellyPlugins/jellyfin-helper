using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Pre-computed feature signals for a recommendation candidate.
///     All values are normalized to approximately 0–1 range.
/// </summary>
public sealed class CandidateFeatures
{
    /// <summary>
    ///     Normalization ceiling for genre count (items with ≥ this many genres map to 1.0).
    /// </summary>
    internal const double GenreCountNormalizationCeiling = 5.0;

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
    ///     Order: [genre, collab, rating, recency, yearProx, genreCount_norm, isSeries,
    ///             genre×rating (interaction), genre×collab (interaction), userRating, completionRatio].
    /// </summary>
    /// <returns>An 11-element feature vector.</returns>
    public double[] ToVector()
    {
        var normalizedGenreCount = Math.Clamp(GenreCount / GenreCountNormalizationCeiling, 0.0, 1.0);

        return
        [
            GenreSimilarity,
            CollaborativeScore,
            RatingScore,
            RecencyScore,
            YearProximityScore,
            normalizedGenreCount,
            IsSeries ? 1.0 : 0.0,
            // Interaction features — capture non-linear relationships
            GenreSimilarity * RatingScore, // high genre match + high rating = extra boost
            GenreSimilarity * CollaborativeScore, // high genre match + popular with similar users = extra boost
            UserRatingScore, // personal rating signal (stronger than community rating)
            CompletionRatio // watch completion — low values for candidates indicate no prior abandonment
        ];
    }
}