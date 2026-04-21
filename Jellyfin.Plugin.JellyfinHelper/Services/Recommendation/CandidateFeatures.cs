using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;

/// <summary>
///     Pre-computed feature signals for a recommendation candidate.
///     All values are normalized to approximately 0–1 range.
/// </summary>
public sealed class CandidateFeatures
{
    /// <summary>Gets or sets the genre similarity score (0–1).</summary>
    public double GenreSimilarity { get; set; }

    /// <summary>Gets or sets the collaborative filtering score (0–1).</summary>
    public double CollaborativeScore { get; set; }

    /// <summary>Gets or sets the normalized community rating (0–1).</summary>
    public double RatingScore { get; set; }

    /// <summary>Gets or sets the recency score (0–1, newer = higher).</summary>
    public double RecencyScore { get; set; }

    /// <summary>Gets or sets the year proximity score (0–1).</summary>
    public double YearProximityScore { get; set; }

    /// <summary>Gets or sets the number of genres the candidate has (raw, for interaction terms).</summary>
    public int GenreCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series (vs movie).</summary>
    public bool IsSeries { get; set; }

    /// <summary>
    ///     Converts the features into a fixed-size double array for ML processing.
    ///     Order: [genre, collab, rating, recency, yearProx, genreCount_norm, isSeries].
    /// </summary>
    /// <returns>A 7-element feature vector.</returns>
    public double[] ToVector()
    {
        return
        [
            GenreSimilarity,
            CollaborativeScore,
            RatingScore,
            RecencyScore,
            YearProximityScore,
            Math.Min(GenreCount / 5.0, 1.0), // normalize genre count
            IsSeries ? 1.0 : 0.0
        ];
    }
}