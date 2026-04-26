using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

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

    /// <summary>Abandoned flag (1 if user interacted AND CompletionRatio &lt; 25%, else 0). Penalizes items the user started but stopped watching early.</summary>
    IsAbandoned = 11,

    /// <summary>Has user interaction flag (1 if user has watched/started the item, 0 for new candidates). Allows the model to distinguish unrated from disliked.</summary>
    HasInteraction = 12,

    /// <summary>People similarity score (0–1). Measures overlap of cast/directors with user's preferred people.</summary>
    PeopleSimilarity = 13,

    /// <summary>Studio similarity flag (0 or 1). Whether the item is from a studio the user has watched before.</summary>
    StudioMatch = 14,

    /// <summary>Series progression boost (0–1). Higher when the user has watched previous seasons and this is a follow-up.</summary>
    SeriesProgressionBoost = 15,

    /// <summary>Popularity score (0–1). Based on how many users have watched this item globally. Helps cold-start users.</summary>
    PopularityScore = 16,

    /// <summary>Day-of-week affinity (0–1). How well this content type matches the user's typical viewing pattern for the current day.</summary>
    DayOfWeekAffinity = 17,

    /// <summary>Hour-of-day affinity (0–1). How well this content matches the user's viewing patterns for the current time of day.</summary>
    HourOfDayAffinity = 18,

    /// <summary>Weekend flag (0 or 1). Whether the current request is on a weekend day (Sat/Sun).</summary>
    IsWeekend = 19,

    /// <summary>Tag-based content similarity (0–1). Jaccard overlap of candidate tags with user's preferred tags.</summary>
    TagSimilarity = 20,

    /// <summary>People × Genre interaction: actors/directors you like in genres you prefer.</summary>
    PeopleGenreInteraction = 21,

    /// <summary>Recency × Rating interaction: new + highly rated = trending content.</summary>
    RecencyRatingInteraction = 22,

    /// <summary>
    ///     Genre underexposure ratio (0–1). Fraction of the candidate's genres that fall
    ///     in the bottom tier of the user's watch distribution (below 2% watch share).
    ///     0 = all candidate genres are regularly watched, 1 = all candidate genres are rarely watched.
    ///     Defaults to 0 (neutral) when watch history is too small (&lt; 30 items).
    /// </summary>
    GenreUnderexposure = 23,

    /// <summary>
    ///     Genre dominance ratio (0–1). Fraction of the candidate's genres that appear
    ///     in the user's top-3 most-watched genres.
    ///     0 = no overlap with core genres, 1 = all candidate genres are in the user's top-3.
    ///     Defaults to 0 (neutral) when watch history is too small (&lt; 30 items).
    /// </summary>
    GenreDominanceRatio = 24,

    /// <summary>
    ///     Genre affinity gap (0–1). How far below the user's average genre preference
    ///     the candidate's genres are. Measures the "distance from comfort zone."
    ///     0 = candidate genres are at or above the user's average preference,
    ///     1 = candidate genres are far below the user's average.
    ///     Defaults to 0 (neutral) when watch history is too small (&lt; 30 items).
    /// </summary>
    GenreAffinityGap = 25,

    /// <summary>
    ///     Library-added recency score (0-1). How recently the item was added to the
    ///     Jellyfin library (based on DateCreated). Separate from RecencyScore
    ///     which measures content release date (PremiereDate).
    /// </summary>
    LibraryAddedRecency = 26,
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
    public const int FeatureCount = 27;

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
    private double _completionRatio = 0.5;
    private double _peopleSimilarity;
    private double _seriesProgressionBoost;
    private double _popularityScore;
    private double _dayOfWeekAffinity;
    private double _hourOfDayAffinity;
    private double _tagSimilarity;
    private double _genreUnderexposure;
    private double _genreDominanceRatio;
    private double _genreAffinityGap;
    private double _libraryAddedRecency;

    /// <summary>Gets or sets the genre similarity score (0–1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double GenreSimilarity
    {
        get => _genreSimilarity;
        set => _genreSimilarity = Clamp01(value);
    }

    /// <summary>Gets or sets the collaborative filtering score (0–1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double CollaborativeScore
    {
        get => _collaborativeScore;
        set => _collaborativeScore = Clamp01(value);
    }

    /// <summary>Gets or sets the normalized community rating (0–1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double RatingScore
    {
        get => _ratingScore;
        set => _ratingScore = Clamp01(value);
    }

    /// <summary>Gets or sets the recency score (0–1, newer = higher). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double RecencyScore
    {
        get => _recencyScore;
        set => _recencyScore = Clamp01(value);
    }

    /// <summary>Gets or sets the year proximity score (0–1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double YearProximityScore
    {
        get => _yearProximityScore;
        set => _yearProximityScore = Clamp01(value);
    }

    /// <summary>Gets or sets the number of genres the candidate has (raw, for interaction terms). Normalized to [0, 1] in <see cref="WriteToVector"/>.</summary>
    public int GenreCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series (vs movie).</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets the user's personal rating score (0–1), or 0.5 if unrated. Values are clamped to [0, 1].</summary>
    public double UserRatingScore
    {
        get => _userRatingScore;
        set => _userRatingScore = Clamp01(value, 0.5);
    }

    /// <summary>Gets or sets a value indicating whether the user has interacted with this item (watched, started, or rated).</summary>
    public bool HasUserInteraction { get; set; }

    /// <summary>Gets or sets the watch completion ratio (0–1). 1.0 = fully watched, 0.5 = neutral (no interaction). Values are clamped to [0, 1].</summary>
    public double CompletionRatio
    {
        get => _completionRatio;
        set => _completionRatio = Clamp01(value, 0.5);
    }

    /// <summary>Gets or sets the people (cast/director) similarity score (0–1). Values are clamped to [0, 1].</summary>
    public double PeopleSimilarity
    {
        get => _peopleSimilarity;
        set => _peopleSimilarity = Clamp01(value);
    }

    /// <summary>Gets or sets a value indicating whether the item is from a studio the user has watched before.</summary>
    public bool StudioMatch { get; set; }

    /// <summary>Gets or sets the series progression boost (0–1). Higher when this is a follow-up season the user hasn't watched yet. Values are clamped to [0, 1].</summary>
    public double SeriesProgressionBoost
    {
        get => _seriesProgressionBoost;
        set => _seriesProgressionBoost = Clamp01(value);
    }

    /// <summary>Gets or sets the popularity score (0–1). Based on global watch count, helps cold-start users. Values are clamped to [0, 1].</summary>
    public double PopularityScore
    {
        get => _popularityScore;
        set => _popularityScore = Clamp01(value);
    }

    /// <summary>Gets or sets the day-of-week affinity (0–1). How well this content matches the user's viewing patterns for the current day. Values are clamped to [0, 1].</summary>
    public double DayOfWeekAffinity
    {
        get => _dayOfWeekAffinity;
        set => _dayOfWeekAffinity = Clamp01(value);
    }

    /// <summary>Gets or sets the hour-of-day affinity (0–1). How well this content matches the user's viewing patterns for the current time of day (e.g. evening vs morning). Values are clamped to [0, 1].</summary>
    public double HourOfDayAffinity
    {
        get => _hourOfDayAffinity;
        set => _hourOfDayAffinity = Clamp01(value);
    }

    /// <summary>Gets or sets a value indicating whether the current request is on a weekend day (Saturday or Sunday).</summary>
    public bool IsWeekend { get; set; }

    /// <summary>Gets or sets the tag-based content similarity (0–1). Jaccard overlap of candidate tags with user's preferred tags. Values are clamped to [0, 1].</summary>
    public double TagSimilarity
    {
        get => _tagSimilarity;
        set => _tagSimilarity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre underexposure ratio (0–1).
    ///     Fraction of the candidate's genres that fall in the bottom tier of the user's
    ///     watch distribution (below the underexposure threshold, typically 2% watch share).
    ///     0 = all candidate genres are regularly watched, 1 = all candidate genres are rarely watched.
    ///     Defaults to 0 (neutral) when watch history is too small.
    /// </summary>
    public double GenreUnderexposure
    {
        get => _genreUnderexposure;
        set => _genreUnderexposure = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre dominance ratio (0–1).
    ///     Fraction of the candidate's genres that appear in the user's top-3 most-watched genres.
    ///     0 = no overlap with core genres, 1 = all candidate genres are in the user's top-3.
    ///     Defaults to 0 (neutral) when watch history is too small.
    /// </summary>
    public double GenreDominanceRatio
    {
        get => _genreDominanceRatio;
        set => _genreDominanceRatio = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre affinity gap (0–1).
    ///     How far below the user's average genre preference the candidate's genres are.
    ///     0 = candidate genres are at or above the user's average preference,
    ///     1 = candidate genres are far below the user's average.
    ///     Defaults to 0 (neutral) when watch history is too small.
    /// </summary>
    public double GenreAffinityGap
    {
        get => _genreAffinityGap;
        set => _genreAffinityGap = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the library-added recency score (0-1).
    ///     How recently the item was added to the Jellyfin library (DateCreated).
    ///     Separate from RecencyScore which uses content release date (PremiereDate).
    ///     Values are clamped to [0, 1]; NaN defaults to 0.
    /// </summary>
    public double LibraryAddedRecency
    {
        get => _libraryAddedRecency;
        set => _libraryAddedRecency = Clamp01(value);
    }

    /// <summary>
    ///     Clamps a value to [0, 1], returning <paramref name="defaultWhenNaN"/> if the value is NaN or Infinity.
    ///     Math.Clamp does not normalize NaN — it preserves it — so this helper prevents
    ///     NaN from flowing into interaction terms and poisoning learned/neural scoring.
    /// </summary>
    private static double Clamp01(double value, double defaultWhenNaN = 0.0) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : defaultWhenNaN;

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
        ArgumentNullException.ThrowIfNull(buffer);

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
        buffer[(int)FeatureIndex.IsAbandoned] = HasUserInteraction && CompletionRatio < AbandonedThreshold ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.HasInteraction] = HasUserInteraction ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.PeopleSimilarity] = PeopleSimilarity;
        buffer[(int)FeatureIndex.StudioMatch] = StudioMatch ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.SeriesProgressionBoost] = SeriesProgressionBoost;
        buffer[(int)FeatureIndex.PopularityScore] = PopularityScore;
        buffer[(int)FeatureIndex.DayOfWeekAffinity] = DayOfWeekAffinity;
        buffer[(int)FeatureIndex.HourOfDayAffinity] = HourOfDayAffinity;
        buffer[(int)FeatureIndex.IsWeekend] = IsWeekend ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.TagSimilarity] = TagSimilarity;
        buffer[(int)FeatureIndex.PeopleGenreInteraction] = PeopleSimilarity * GenreSimilarity;
        buffer[(int)FeatureIndex.RecencyRatingInteraction] = RecencyScore * RatingScore;
        buffer[(int)FeatureIndex.GenreUnderexposure] = GenreUnderexposure;
        buffer[(int)FeatureIndex.GenreDominanceRatio] = GenreDominanceRatio;
        buffer[(int)FeatureIndex.GenreAffinityGap] = GenreAffinityGap;
        buffer[(int)FeatureIndex.LibraryAddedRecency] = LibraryAddedRecency;
    }
}
