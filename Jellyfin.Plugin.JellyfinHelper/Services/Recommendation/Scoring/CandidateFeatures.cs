using System;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Named indices for the feature vector produced by ToVector. Use these instead of magic numbers when accessing vector elements.
/// </summary>
public enum FeatureIndex
{
    /// <summary>Genre similarity (0-1).</summary>
    GenreSimilarity = 0,

    /// <summary>Collaborative filtering score (0-1).</summary>
    CollaborativeScore = 1,

    /// <summary>Combined critic score (0-1). Blends TMDb community rating (55%) and Rotten Tomatoes Tomatometer (45%).</summary>
    CombinedCriticScore = 2,

    /// <summary>Recency score (0-1).</summary>
    RecencyScore = 3,

    /// <summary>Year proximity score (0-1).</summary>
    YearProximityScore = 4,

    /// <summary>Normalized genre count (0-1).</summary>
    GenreCountNormalized = 5,

    /// <summary>Is series flag (0 or 1).</summary>
    IsSeries = 6,

    /// <summary>Genre × CombinedCritic interaction term.</summary>
    GenreCriticInteraction = 7,

    /// <summary>Genre × Collaborative interaction term.</summary>
    GenreCollabInteraction = 8,

    /// <summary>User personal rating score (0-1).</summary>
    UserRatingScore = 9,

    /// <summary>Genre average completion (0-1). Mean completion for this genre.</summary>
    CompletionRatio = 10,

    /// <summary>Genre abandon rate (0-1). Fraction abandoned in this genre.</summary>
    IsAbandoned = 11,

    /// <summary>Genre familiarity (0-1). Whether user has watched this genre before.</summary>
    HasInteraction = 12,

    /// <summary>People similarity score (0-1). Measures overlap of cast/directors with user's preferred people.</summary>
    PeopleSimilarity = 13,

    /// <summary>Studio similarity flag (0 or 1). Whether the item is from a studio the user has watched before.</summary>
    StudioMatch = 14,

    /// <summary>Series affinity (0-1). Max Jaccard to progressing series the user follows.</summary>
    SeriesAffinity = 15,

    /// <summary>Legacy name for SeriesAffinity.</summary>
    SeriesProgressionBoost = SeriesAffinity,

    /// <summary>Popularity score (0-1). Based on how many users have watched this item globally. Helps cold-start users.</summary>
    PopularityScore = 16,

    /// <summary>Day-of-week affinity (0-1). How well this content type matches the user's typical viewing pattern for the current day.</summary>
    DayOfWeekAffinity = 17,

    /// <summary>Hour-of-day affinity (0-1). How well this content matches the user's viewing patterns for the current time of day.</summary>
    HourOfDayAffinity = 18,

    /// <summary>Weekend flag (0 or 1). Whether the current request is on a weekend day (Sat/Sun).</summary>
    IsWeekend = 19,

    /// <summary>Tag-based content similarity (0-1). Jaccard overlap of candidate tags with user's preferred tags.</summary>
    TagSimilarity = 20,

    /// <summary>People × Genre interaction: actors/directors you like in genres you prefer.</summary>
    PeopleGenreInteraction = 21,

    /// <summary>Recency × CombinedCritic interaction: new + highly rated = trending content.</summary>
    RecencyCriticInteraction = 22,

    /// <summary>
    ///     Genre underexposure ratio (0-1). Fraction of the candidate's genres that fall in the bottom tier of the user's watch distribution (below 2% watch share).
    /// </summary>
    GenreUnderexposure = 23,

    /// <summary>
    ///     Genre dominance ratio (0-1). Fraction of the candidate's genres that appear in the user's top-3 most-watched genres.
    /// </summary>
    GenreDominanceRatio = 24,

    /// <summary>
    ///     Genre affinity gap (0-1). How far below the user's average genre preference the candidate's genres are.
    /// </summary>
    GenreAffinityGap = 25,

    /// <summary>
    ///     Library-added recency score (0-1). How recently the item was added to the Jellyfin library (based on DateCreated).
    /// </summary>
    LibraryAddedRecency = 26,

    /// <summary>
    ///     Content-based nearest-neighbor score (0-1). Measures how similar this candidate is to the user's most similar watched item using a composite of genre Jaccard (50%), people/cast Jaccard (30%), and studio overlap (20%).
    /// </summary>
    ContentNearestNeighborScore = 27,

    /// <summary>
    ///     Audio language affinity (0-1). How well the candidate's available audio languages match the user's language preferences.
    /// </summary>
    LanguageAffinity = 28,

    /// <summary>
    ///     Collection/BoxSet progression boost (0-1). Rewards items that belong to a collection (BoxSet) where the user has already watched other entries.
    /// </summary>
    CollectionProgressionBoost = 29,

    /// <summary>
    ///     Subtitle language affinity (0-1). How well the candidate's available subtitle languages match the user's subtitle language preferences.
    /// </summary>
    SubtitleLanguageAffinity = 30,

    /// <summary>
    ///     Franchise affinity (0-1). How strongly the candidate belongs to a TMDb collection (franchise) the user has already engaged with.
    /// </summary>
    FranchiseAffinity = 31,

    /// <summary>
    ///     Production-location affinity (0-1). Weighted overlap of the candidate's production countries with the user's watched-country distribution (K-drama, Bollywood, Euro arthouse ...).
    /// </summary>
    ProductionLocationAffinity = 32,

    /// <summary>
    ///     Inherited-tag similarity (0-1). Jaccard overlap of the candidate's INHERITED tags (own tags unioned with parent/collection/library-folder tags) with the user's preferred inherited tags.
    /// </summary>
    InheritedTagSimilarity = 33,

    /// <summary>
    ///     Series completability (0-1). Encodes a series' lifecycle: Ended to 1.0, Continuing to 0.5, Unreleased to 0.0.
    /// </summary>
    SeriesCompletability = 34,

    /// <summary>
    ///     Writer affinity (0-1). Name-overlap of the candidate's writers/creators with the user's preferred writers (a lightweight profile kept separately from cast/director so it does not dilute PeopleSimilarity).
    /// </summary>
    WriterAffinity = 35,

    /// <summary>
    ///     Billing-weighted people affinity (0-1). Like PeopleSimilarity but weighted by each person's billing position (PersonInfo.SortOrder): top-billed cast the user favours count for more than deep-cast/bit-part entries.
    /// </summary>
    BillingWeightedPeople = 36,

    /// <summary>
    ///     Genre/studio IDF (inverse-document-frequency) rarity prior (0-1). Library-wide rarity of the candidate's genres and studios: ubiquitous genres are down-weighted, rare ones up-weighted.
    /// </summary>
    GenreStudioIdfPrior = 37,
}

/// <summary>
///     Pre-computed feature signals for a recommendation candidate.
///     All values are normalized to approximately 0-1 range.
/// </summary>
public sealed class CandidateFeatures
{
    /// <summary>
    ///     The number of features produced by <see cref="ToVector"/>.
    /// </summary>
    public const int FeatureCount = 38;

    /// <summary>
    ///     Normalization ceiling for genre count (items with >= this many genres map to 1.0).
    /// </summary>
    internal const double GenreCountNormalizationCeiling = 5.0;

    /// <summary>
    ///     Watch completion ratio below which an item is considered "abandoned". Items with CompletionRatio &lt; this threshold have IsAbandoned = 1 in the feature vector, which applies a negative weight penalty during scoring.
    /// </summary>
    internal const double AbandonedThreshold = 0.25;

    private double _genreSimilarity;
    private double _collaborativeScore;
    private double _ratingScore;
    private double _recencyScore;
    private double _yearProximityScore;
    private double _userRatingScore = 0.5;
    private double _completionRatio = 0.5;
    private double _isAbandoned;
    private bool _isAbandonedSet;
    private double _peopleSimilarity;
    private double _seriesAffinity;
    private double _popularityScore;
    private double _dayOfWeekAffinity;
    private double _hourOfDayAffinity;
    private double _tagSimilarity;
    private double _genreUnderexposure;
    private double _genreDominanceRatio;
    private double _genreAffinityGap;
    private double _libraryAddedRecency;
    private double _contentNearestNeighborScore;
    private double _languageAffinity = 0.5;
    private double _collectionProgressionBoost;
    private double _subtitleLanguageAffinity = 0.5;
    private double _franchiseAffinity;
    private double _productionLocationAffinity;
    private double _inheritedTagSimilarity;
    private double _seriesCompletability = 0.5;
    private double _writerAffinity;
    private double _billingWeightedPeople;
    private double _genreStudioIdfPrior;

    /// <summary>Gets or sets the genre similarity score (0-1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double GenreSimilarity
    {
        get => _genreSimilarity;
        set => _genreSimilarity = Clamp01(value);
    }

    /// <summary>Gets or sets the collaborative filtering score (0-1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double CollaborativeScore
    {
        get => _collaborativeScore;
        set => _collaborativeScore = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the combined critic score (0-1). Blends TMDb community rating (55%) and Rotten Tomatoes Tomatometer (45%).
    /// </summary>
    public double CombinedCriticScore
    {
        get => _ratingScore;
        set => _ratingScore = Clamp01(value);
    }

    /// <summary>Gets or sets the recency score (0-1, newer = higher). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double RecencyScore
    {
        get => _recencyScore;
        set => _recencyScore = Clamp01(value);
    }

    /// <summary>Gets or sets the year proximity score (0-1). Values are clamped to [0, 1]; NaN defaults to 0.</summary>
    public double YearProximityScore
    {
        get => _yearProximityScore;
        set => _yearProximityScore = Clamp01(value);
    }

    /// <summary>Gets or sets the number of genres the candidate has (raw, for interaction terms). Normalized to [0, 1] in <see cref="WriteToVector"/>.</summary>
    public int GenreCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a series (vs movie).</summary>
    public bool IsSeries { get; set; }

    /// <summary>Gets or sets the user's personal rating score (0-1), or 0.5 if unrated. Values are clamped to [0, 1].</summary>
    public double UserRatingScore
    {
        get => _userRatingScore;
        set => _userRatingScore = Clamp01(value, 0.5);
    }

    /// <summary>Gets or sets a value indicating whether the user is familiar with this genre.</summary>
    public bool HasUserInteraction { get; set; }

    /// <summary>Gets or sets genre average completion (0-1). Mean for this genre.</summary>
    public double CompletionRatio
    {
        get => _completionRatio;
        set => _completionRatio = Clamp01(value, 0.5);
    }

    /// <summary>Gets or sets genre abandon rate (0-1). Fraction abandoned in this genre.</summary>
    public double IsAbandoned
    {
        get => _isAbandoned;
        set
        {
            _isAbandoned = Clamp01(value);
            _isAbandonedSet = true;
        }
    }

    /// <summary>Gets or sets the people (cast/director) similarity score (0-1). Values are clamped to [0, 1].</summary>
    public double PeopleSimilarity
    {
        get => _peopleSimilarity;
        set => _peopleSimilarity = Clamp01(value);
    }

    /// <summary>Gets or sets a value indicating whether the item is from a studio the user has watched before.</summary>
    public bool StudioMatch { get; set; }

    /// <summary>Gets or sets series affinity (0-1). Max Jaccard to progressing series.</summary>
    public double SeriesAffinity
    {
        get => _seriesAffinity;
        set => _seriesAffinity = Clamp01(value);
    }

    /// <summary>Gets or sets the series progression boost (0-1). Legacy name for SeriesAffinity.</summary>
    public double SeriesProgressionBoost
    {
        get => SeriesAffinity;
        set => SeriesAffinity = value;
    }

    /// <summary>Gets or sets the popularity score (0-1). Based on global watch count, helps cold-start users. Values are clamped to [0, 1].</summary>
    public double PopularityScore
    {
        get => _popularityScore;
        set => _popularityScore = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the day-of-week affinity (0-1). How well this content matches the user's viewing patterns for the current day.
    /// </summary>
    public double DayOfWeekAffinity
    {
        get => _dayOfWeekAffinity;
        set => _dayOfWeekAffinity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the hour-of-day affinity (0-1). How well this content matches the user's viewing patterns for the current time of day (e.g.
    /// </summary>
    public double HourOfDayAffinity
    {
        get => _hourOfDayAffinity;
        set => _hourOfDayAffinity = Clamp01(value);
    }

    /// <summary>Gets or sets a value indicating whether the current request is on a weekend day (Saturday or Sunday).</summary>
    public bool IsWeekend { get; set; }

    /// <summary>Gets or sets the tag-based content similarity (0-1). Jaccard overlap of candidate tags with user's preferred tags. Values are clamped to [0, 1].</summary>
    public double TagSimilarity
    {
        get => _tagSimilarity;
        set => _tagSimilarity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre underexposure ratio (0-1). Fraction of the candidate's genres that fall in the bottom tier of the user's watch distribution (below the underexposure threshold, typically 2% watch share).
    /// </summary>
    public double GenreUnderexposure
    {
        get => _genreUnderexposure;
        set => _genreUnderexposure = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre dominance ratio (0-1). Fraction of the candidate's genres that appear in the user's top-3 most-watched genres.
    /// </summary>
    public double GenreDominanceRatio
    {
        get => _genreDominanceRatio;
        set => _genreDominanceRatio = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre affinity gap (0-1). How far below the user's average genre preference the candidate's genres are.
    /// </summary>
    public double GenreAffinityGap
    {
        get => _genreAffinityGap;
        set => _genreAffinityGap = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the library-added recency score (0-1). How recently the item was added to the Jellyfin library (DateCreated).
    /// </summary>
    public double LibraryAddedRecency
    {
        get => _libraryAddedRecency;
        set => _libraryAddedRecency = Clamp01(value, 0.5);
    }

    /// <summary>
    ///     Gets or sets the content-based nearest-neighbor score (0-1). Composite similarity between this candidate and the user's most similar watched item, combining genre Jaccard (50%), people/cast Jaccard (30%), and studio overlap (20%).
    /// </summary>
    public double ContentNearestNeighborScore
    {
        get => _contentNearestNeighborScore;
        set => _contentNearestNeighborScore = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the audio language affinity (0-1). How well the candidate's available audio languages match the user's preferences.
    /// </summary>
    public double LanguageAffinity
    {
        get => _languageAffinity;
        set => _languageAffinity = Clamp01(value, 0.5);
    }

    /// <summary>
    ///     Gets or sets the collection/BoxSet progression boost (0-1). Rewards items belonging to a collection where the user has watched other entries.
    /// </summary>
    /// <remarks>
    ///     Live scoring (Engine.ScoreCandidate) pre-computes BoxSet membership counts for all watched items once per user, then performs an O(1) lookup per candidate via the candidate's resolved BoxSet IDs.
    /// </remarks>
    public double CollectionProgressionBoost
    {
        get => _collectionProgressionBoost;
        set => _collectionProgressionBoost = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the subtitle language affinity (0-1). How well the candidate's available subtitle languages match the user's preferences.
    /// </summary>
    public double SubtitleLanguageAffinity
    {
        get => _subtitleLanguageAffinity;
        set => _subtitleLanguageAffinity = Clamp01(value, 0.5);
    }

    /// <summary>
    ///     Gets or sets the franchise affinity (0-1). How strongly the candidate belongs to a TMDb collection (franchise) the user has engaged with.
    /// </summary>
    public double FranchiseAffinity
    {
        get => _franchiseAffinity;
        set => _franchiseAffinity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the production-location affinity (0-1). Weighted overlap of the candidate's production countries with the user's watched-country distribution.
    /// </summary>
    public double ProductionLocationAffinity
    {
        get => _productionLocationAffinity;
        set => _productionLocationAffinity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the inherited-tag similarity (0-1). Jaccard overlap of the candidate's inherited tags with the user's preferred inherited tags.
    /// </summary>
    public double InheritedTagSimilarity
    {
        get => _inheritedTagSimilarity;
        set => _inheritedTagSimilarity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the series completability (0-1). Ended to 1.0, Continuing to 0.5, Unreleased to 0.0.
    /// </summary>
    public double SeriesCompletability
    {
        get => _seriesCompletability;
        set => _seriesCompletability = Clamp01(value, 0.5);
    }

    /// <summary>
    ///     Gets or sets the writer affinity (0-1). Name-overlap of the candidate's writers/creators with the user's preferred writers.
    /// </summary>
    public double WriterAffinity
    {
        get => _writerAffinity;
        set => _writerAffinity = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the billing-weighted people affinity (0-1). Billing-position-weighted overlap of the candidate's cast/directors with the user's favoured billed people.
    /// </summary>
    public double BillingWeightedPeople
    {
        get => _billingWeightedPeople;
        set => _billingWeightedPeople = Clamp01(value);
    }

    /// <summary>
    ///     Gets or sets the genre/studio IDF rarity prior (0-1). Library-wide rarity of the candidate's genres and studios (rare to higher).
    /// </summary>
    public double GenreStudioIdfPrior
    {
        get => _genreStudioIdfPrior;
        set => _genreStudioIdfPrior = Clamp01(value);
    }

    /// <summary>
    ///     Clamps a value to [0, 1], returning if the value is NaN or Infinity. Math.Clamp does not normalize NaN - it preserves it - so this helper prevents NaN from flowing into interaction terms and poisoning learned/neural scoring.
    /// </summary>
    private static double Clamp01(double value, double defaultWhenNaN = 0.0) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : defaultWhenNaN;

    /// <summary>
    ///     Converts the features into a fixed-size double array for ML processing. Order is defined by FeatureIndex.
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
        buffer[(int)FeatureIndex.CombinedCriticScore] = CombinedCriticScore;
        buffer[(int)FeatureIndex.RecencyScore] = RecencyScore;
        buffer[(int)FeatureIndex.YearProximityScore] = YearProximityScore;
        buffer[(int)FeatureIndex.GenreCountNormalized] = normalizedGenreCount;
        buffer[(int)FeatureIndex.IsSeries] = IsSeries ? 1.0 : 0.0;
        buffer[(int)FeatureIndex.GenreCriticInteraction] = GenreSimilarity * CombinedCriticScore;
        buffer[(int)FeatureIndex.GenreCollabInteraction] = GenreSimilarity * CollaborativeScore;
        buffer[(int)FeatureIndex.UserRatingScore] = UserRatingScore;
        buffer[(int)FeatureIndex.CompletionRatio] = CompletionRatio;
        double abandonedValue;
        if (_isAbandonedSet)
        {
            abandonedValue = IsAbandoned;
        }
        else
        {
            abandonedValue = HasUserInteraction
                && CompletionRatio > 0.0
                && CompletionRatio < AbandonedThreshold
                    ? 1.0
                    : 0.0;
        }

        buffer[(int)FeatureIndex.IsAbandoned] = abandonedValue;
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
        buffer[(int)FeatureIndex.RecencyCriticInteraction] = RecencyScore * CombinedCriticScore;
        buffer[(int)FeatureIndex.GenreUnderexposure] = GenreUnderexposure;
        buffer[(int)FeatureIndex.GenreDominanceRatio] = GenreDominanceRatio;
        buffer[(int)FeatureIndex.GenreAffinityGap] = GenreAffinityGap;
        buffer[(int)FeatureIndex.LibraryAddedRecency] = LibraryAddedRecency;
        buffer[(int)FeatureIndex.ContentNearestNeighborScore] = ContentNearestNeighborScore;
        buffer[(int)FeatureIndex.LanguageAffinity] = LanguageAffinity;
        buffer[(int)FeatureIndex.CollectionProgressionBoost] = CollectionProgressionBoost;
        buffer[(int)FeatureIndex.SubtitleLanguageAffinity] = SubtitleLanguageAffinity;
        buffer[(int)FeatureIndex.FranchiseAffinity] = FranchiseAffinity;
        buffer[(int)FeatureIndex.ProductionLocationAffinity] = ProductionLocationAffinity;
        buffer[(int)FeatureIndex.InheritedTagSimilarity] = InheritedTagSimilarity;
        buffer[(int)FeatureIndex.SeriesCompletability] = SeriesCompletability;
        buffer[(int)FeatureIndex.WriterAffinity] = WriterAffinity;
        buffer[(int)FeatureIndex.BillingWeightedPeople] = BillingWeightedPeople;
        buffer[(int)FeatureIndex.GenreStudioIdfPrior] = GenreStudioIdfPrior;
    }
}
