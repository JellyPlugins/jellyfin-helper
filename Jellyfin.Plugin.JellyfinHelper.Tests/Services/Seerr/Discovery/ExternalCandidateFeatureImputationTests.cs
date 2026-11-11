using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for the discovery mean-imputation: features that cannot be computed for an external
///     (TMDb) candidate are imputed to the model's persisted training-set means instead of arbitrary
///     0/0.5 constants, so a discovery candidate scores against the trained distribution.
/// </summary>
public sealed class ExternalCandidateFeatureImputationTests
{
    private const int ActionTmdbGenreId = 28;
    private const string ActionGenre = "Action";

    // A recognizable, distinct mean per feature index so the index→property mapping can be asserted exactly.
    private static double[] DistinctMeans()
    {
        var means = new double[CandidateFeatures.FeatureCount];
        for (var i = 0; i < means.Length; i++)
        {
            // Keep values inside [0,1] so the CandidateFeatures setters (which clamp) do not distort them.
            means[i] = (i + 1) / 100.0;
        }

        return means;
    }

    [Fact]
    public void ApplyMeanImputation_SuppliedMeans_OverwritesContinuousPlaceholders()
    {
        var means = DistinctMeans();
        var features = new CandidateFeatures();

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, means);

        Assert.Equal(means[(int)FeatureIndex.CollaborativeScore], features.CollaborativeScore, 9);
        Assert.Equal(means[(int)FeatureIndex.TagSimilarity], features.TagSimilarity, 9);
        Assert.Equal(means[(int)FeatureIndex.ContentNearestNeighborScore], features.ContentNearestNeighborScore, 9);
        Assert.Equal(means[(int)FeatureIndex.LanguageAffinity], features.LanguageAffinity, 9);
        Assert.Equal(means[(int)FeatureIndex.FranchiseAffinity], features.FranchiseAffinity, 9);
        Assert.Equal(means[(int)FeatureIndex.BillingWeightedPeople], features.BillingWeightedPeople, 9);
        Assert.Equal(means[(int)FeatureIndex.GenreStudioIdfPrior], features.GenreStudioIdfPrior, 9);
        Assert.Equal(means[(int)FeatureIndex.SeriesCompletability], features.SeriesCompletability, 9);
    }

    [Fact]
    public void ApplyMeanImputation_BoolPlaceholders_StayFalse()
    {
        // StudioMatch / IsWeekend are bool-typed; a fractional mean cannot flow through them and false is the
        // correct value for an unknown external candidate, so imputation must leave them false.
        var features = new CandidateFeatures { StudioMatch = false, IsWeekend = false };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, DistinctMeans());

        Assert.False(features.StudioMatch);
        Assert.False(features.IsWeekend);
    }

    [Fact]
    public void ApplyMeanImputation_NullMeans_LeavesFeaturesUnchanged()
    {
        var features = new CandidateFeatures { CollaborativeScore = 0.5, TagSimilarity = 0.0 };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, featureMeans: null);

        Assert.Equal(0.5, features.CollaborativeScore, 9);
        Assert.Equal(0.0, features.TagSimilarity, 9);
    }

    [Fact]
    public void ApplyMeanImputation_WrongLengthMeans_LeavesFeaturesUnchanged()
    {
        var features = new CandidateFeatures { CollaborativeScore = 0.5 };

        ExternalCandidateFeatureBuilder.ApplyMeanImputation(features, [0.1, 0.2, 0.3]);

        Assert.Equal(0.5, features.CollaborativeScore, 9);
    }

    [Fact]
    public void Build_WithMeans_ImputesPlaceholdersButKeepsComputedFeatures()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);
        var means = DistinctMeans();

        var candidate = new TmdbDiscoverItem
        {
            Id = 700,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 8.0,
            Popularity = 100.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear: 2015,
            genreExposure,
            profile,
            means);

        // Placeholder features are imputed to their means.
        Assert.Equal(means[(int)FeatureIndex.CollaborativeScore], features.CollaborativeScore, 9);
        Assert.Equal(means[(int)FeatureIndex.LanguageAffinity], features.LanguageAffinity, 9);

        // Computed features are untouched: GenreSimilarity is high (candidate genre matches the profile) and
        // PopularityScore follows the 100/200 normalization, not a mean.
        Assert.True(features.GenreSimilarity > 0.0);
        Assert.Equal(0.5, features.PopularityScore, 9);
        Assert.Equal(0.8, features.CombinedCriticScore, 9);
    }

    [Fact]
    public void MissingPeopleMetadata_UsesEachModelsMeanAndRecomputesItsGenreInteraction()
    {
        var profile = BuildActionHeavyProfile();
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile);
        var firstMeans = DistinctMeans();
        var secondMeans = Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray();
        var features = ExternalCandidateFeatureBuilder.Build(new TmdbDiscoverItem { Id = 700, MediaType = "movie", GenreIds = [28], KnownPeople = null }, preferences, ["Favorite actor"], 2015, exposure, profile, firstMeans);
        Assert.False(features.HasPeopleMetadata);
        Assert.False(features.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.Equal(firstMeans[(int)FeatureIndex.PeopleSimilarity], features.PeopleSimilarity);
        var original = features.ToVector();
        foreach (var means in new[] { firstMeans, secondMeans })
        {
            var vector = features.ToModelVector(means);
            Assert.Equal(means[(int)FeatureIndex.PeopleSimilarity], vector[(int)FeatureIndex.PeopleSimilarity]);
            Assert.Equal(features.GenreSimilarity * means[(int)FeatureIndex.PeopleSimilarity], vector[(int)FeatureIndex.PeopleGenreInteraction]);
            Assert.Equal(vector, features.Copy().ToModelVector(means));
            Assert.Equal(vector, features.WithoutSignal(FeatureIndex.PeopleSimilarity).ToModelVector(means));
        }
        Assert.Equal(original, features.ToVector());
        Assert.Equal(0.0, features.ToModelVector(null)[(int)FeatureIndex.PeopleSimilarity]);
        Assert.Equal(0.0, features.ToModelVector(null)[(int)FeatureIndex.PeopleGenreInteraction]);
        var library = features.Copy();
        library.IsExternalCandidate = false;
        Assert.Equal(library.ToVector(), library.ToModelVector(secondMeans));
    }

    [Theory]
    [InlineData("empty", 0.0)]
    [InlineData("nonmatch", 0.0)]
    [InlineData("match", 1.0)]
    public void ObservedCredits_PreserveRealMatchesAndKnownZeroValuesAcrossModelMeans(string credits, double expected)
    {
        var profile = BuildActionHeavyProfile();
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile);
        List<string> people = credits switch { "empty" => [], "match" => ["Favorite actor"], _ => ["Other actor"] };
        var features = ExternalCandidateFeatureBuilder.Build(new TmdbDiscoverItem { Id = 700, GenreIds = [28], KnownPeople = people }, preferences, ["Favorite actor"], 2015, exposure, profile, DistinctMeans());
        Assert.True(features.HasPeopleMetadata);
        Assert.True(features.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.Equal(expected, features.PeopleSimilarity);
        var vector = features.ToModelVector(Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray());
        Assert.Equal(expected, vector[(int)FeatureIndex.PeopleSimilarity]);
        Assert.Equal(expected * features.GenreSimilarity, vector[(int)FeatureIndex.PeopleGenreInteraction]);
        Assert.Equal(0.0, features.WithoutSignal(FeatureIndex.PeopleSimilarity).ToModelVector(DistinctMeans())[(int)FeatureIndex.PeopleGenreInteraction]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconstructedDiscoveryFeedback_PreservesTheDifferenceBetweenMissingAndEmptyCredits(bool hasCredits)
    {
        var user = Guid.NewGuid();
        var shown = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var entry = new DiscoveryFeedbackEntry { TmdbId = 700, MediaType = "movie", Genres = ["Action"], ShownAtUtc = shown, RequestedAtUtc = shown.AddDays(1), Popularity = 50, KnownPeople = hasCredits ? [] : null! };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = shown.AddDays(-1), Genres = ["Action"] }] };
        var means = DistinctMeans();
        var features = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new DiscoveryFeedbackResult { UserId = user, Entries = [entry] }], new Dictionary<Guid, UserWatchProfile> { [user] = profile }, null, means, TestContext.Current.CancellationToken).Examples).Features;
        Assert.Equal(hasCredits, features.HasPeopleMetadata);
        Assert.Equal(hasCredits, features.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.Equal(hasCredits ? 0.0 : means[(int)FeatureIndex.PeopleSimilarity], features.PeopleSimilarity);
        var vector = features.ToModelVector(means);
        Assert.Equal(features.PeopleSimilarity * features.GenreSimilarity, vector[(int)FeatureIndex.PeopleGenreInteraction]);
    }

    [Fact]
    public void ConfirmedFeedback_RetainsPeopleProvenanceEvenWhenLaterMetadataOrModelMeansChange()
    {
        var user = Guid.NewGuid();
        var shown = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
        var stored = new CandidateFeatures { IsExternalCandidate = true, HasPeopleMetadata = false, GenreSimilarity = 0.8, PeopleSimilarity = 0.2 };
        var entry = new DiscoveryFeedbackEntry { TmdbId = 700, ShownAtUtc = shown, RequestedAtUtc = shown.AddDays(1), PredictionEvidenceAtUtc = shown, ScoringFeatures = stored, KnownPeople = ["Later actor"] };
        var restored = System.Text.Json.JsonSerializer.Deserialize<DiscoveryFeedbackResult>(System.Text.Json.JsonSerializer.Serialize(new DiscoveryFeedbackResult { UserId = user, Entries = [entry] }))!;
        var features = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([restored], new Dictionary<Guid, UserWatchProfile>(), null, DistinctMeans(), TestContext.Current.CancellationToken).Examples).Features;
        Assert.False(features.HasPeopleMetadata);
        Assert.Equal(stored.ToVector(), features.ToVector());
        var personalMeans = Enumerable.Repeat(0.6, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(stored.ToModelVector(personalMeans), features.ToModelVector(personalMeans));
        Assert.NotSame(restored.Entries[0].ScoringFeatures, features);
    }

    [Fact]
    public void LegacyPredictionSnapshots_RetainRawPeopleAndInteractionValuesWithoutRetroactiveImputation()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<CandidateFeatures>("""{"IsExternalCandidate":true,"GenreSimilarity":0.8,"PeopleSimilarity":0.3,"KnownExternalFeatures":1048576}""")!;
        var means = DistinctMeans();
        Assert.Null(legacy.HasPeopleMetadata);
        var vector = legacy.ToModelVector(means);
        Assert.Equal(0.3, vector[(int)FeatureIndex.PeopleSimilarity]);
        Assert.Equal(0.3 * 0.8, vector[(int)FeatureIndex.PeopleGenreInteraction]);
        var restored = System.Text.Json.JsonSerializer.Deserialize<CandidateFeatures>(System.Text.Json.JsonSerializer.Serialize(legacy.Copy()))!;
        Assert.Equal(vector, restored.ToModelVector(means));
        legacy.HasPeopleMetadata = false;
        var unknown = System.Text.Json.JsonSerializer.Deserialize<CandidateFeatures>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
        Assert.False(unknown.HasPeopleMetadata);
        Assert.Equal(means[(int)FeatureIndex.PeopleSimilarity], unknown.ToModelVector(means)[(int)FeatureIndex.PeopleSimilarity]);
    }

    [Fact]
    public void Build_WithoutMeans_KeepsLegacyNeutralConstants()
    {
        var profile = BuildActionHeavyProfile();
        var genrePrefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var genreExposure = PreferenceBuilder.BuildGenreExposureAnalysis(genrePrefs, profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 701,
            MediaType = "movie",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 8.0,
            Popularity = 100.0
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            genrePrefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            avgYear: 2015,
            genreExposure,
            profile);

        // Legacy neutral constants remain when no means are supplied.
        Assert.Equal(0.5, features.CollaborativeScore, 9);
        Assert.Equal(0.0, features.TagSimilarity, 9);
        Assert.Equal(0.0, features.FranchiseAffinity, 9);
    }

    private static UserWatchProfile BuildActionHeavyProfile()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 40; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = [ActionGenre]
            });
        }

        return profile;
    }
}
