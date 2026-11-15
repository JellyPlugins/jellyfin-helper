using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Semantic metadata parity without overwriting unknown external inputs or recorded feedback snapshots.</summary>
public sealed class ExternalContentFitTests
{
    private static readonly string[] Tags = ["anime", "shounen", "pirate", "friendship"];
    private static readonly Dictionary<string, double> Genres = new() { ["Animation"] = 1, ["Action"] = 1, ["Adventure"] = 1 };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownKeywordsAreComputedWhileUnknownKeywordsKeepModelSpecificMeans(bool known)
    {
        var profile = Profile();
        var candidate = Candidate();
        candidate.KnownKeywords = known ? [.. Tags] : null;
        var means = Enumerable.Repeat(.42, 38).ToArray();
        var features = ExternalCandidateFeatureBuilder.Build(candidate, Genres, [], 2000, PreferenceBuilder.BuildGenreExposureAnalysis(Genres, profile), profile, means);
        Assert.Equal(known ? 1 : .42, features.TagSimilarity, 12);
        Assert.Equal(known, features.IsExternalFeatureKnown(FeatureIndex.TagSimilarity));
        Assert.Equal(38, features.ToVector().Length);
        Assert.Equal(features.GenreSimilarity * features.CombinedCriticScore, features.ToVector()[(int)FeatureIndex.GenreCriticInteraction]);
    }

    [Fact]
    public void KnownEmptyKeywordsStayKnownEmptyRatherThanBeingImputedAsPositiveAffinity()
    {
        var profile = Profile();
        var candidate = Candidate();
        candidate.KnownKeywords = [];
        var features = ExternalCandidateFeatureBuilder.Build(candidate, Genres, [], 2000, PreferenceBuilder.BuildGenreExposureAnalysis(Genres, profile), profile, Enumerable.Repeat(.8, 38).ToArray());
        Assert.Equal(0, features.TagSimilarity);
        Assert.True(features.IsExternalFeatureKnown(FeatureIndex.TagSimilarity));
    }

    [Fact]
    public void EquivalentKnownLocalAndExternalKeywordsProduceTheSameContentCoordinates()
    {
        var profile = Profile();
        var candidate = Candidate();
        candidate.KnownKeywords = [.. Tags];
        var external = ExternalCandidateFeatureBuilder.Build(candidate, Genres, [], 2000, PreferenceBuilder.BuildGenreExposureAnalysis(Genres, profile), profile);
        var local = new CandidateFeatures
        {
            GenreSimilarity = SimilarityComputer.ComputeGenreSimilarity(["Animation", "Action & Adventure"], Genres),
            TagSimilarity = SimilarityComputer.ComputeJaccardFromSets(new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase), PreferenceBuilder.BuildTagPreferenceSet(profile, []))
        };
        ContentFitProfile.Create(profile).Apply(local, ["Animation", "Action & Adventure"], Tags, "FSK-12", Genres);
        Assert.Equal(local.TagSimilarity, external.TagSimilarity);
        Assert.Equal(local.GenreSimilarity, external.GenreSimilarity);
        Assert.Equal(local.ToVector()[(int)FeatureIndex.TagSimilarity], external.ToVector()[(int)FeatureIndex.TagSimilarity]);
    }

    [Fact]
    public void ExplicitPreschoolKeywordsAreNotInterpretedAsTheSameAudienceAsShounen()
    {
        var profile = Profile();
        var narrative = Candidate();
        narrative.KnownKeywords = [.. Tags];
        var preschool = Candidate();
        preschool.KnownKeywords = ["anime", "preschool", "children's television"];
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(Genres, profile);
        var battle = ExternalCandidateFeatureBuilder.Build(narrative, Genres, [], 2000, exposure, profile);
        var child = ExternalCandidateFeatureBuilder.Build(preschool, Genres, [], 2000, exposure, profile);
        Assert.True(battle.GenreSimilarity > child.GenreSimilarity);
        Assert.True(battle.TagSimilarity > child.TagSimilarity);
        Assert.InRange(child.GenreSimilarity, double.Epsilon, 1);
    }

    [Fact]
    public void ConfirmedFeedbackRetainsItsOriginalV5PredictionAfterMetadataImproves()
    {
        var profile = Profile();
        var original = new CandidateFeatures { IsExternalCandidate = true, GenreSimilarity = .7, TagSimilarity = .001, ContentNearestNeighborScore = .3 };
        var entry = new DiscoveryFeedbackEntry
        {
            TmdbId = 123, MediaType = "tv", Genres = ["Animation", "Action & Adventure"],
            RequestedAtUtc = DateTime.UtcNow.AddDays(-1), PredictionEvidenceAtUtc = DateTime.UtcNow.AddDays(-1), ScoringFeatures = original
        };
        var (examples, _) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new DiscoveryFeedbackResult { UserId = profile.UserId, Entries = [entry] }],
            new Dictionary<Guid, UserWatchProfile> { [profile.UserId] = profile }, null, Enumerable.Repeat(.8, 38).ToArray(), TestContext.Current.CancellationToken);
        var retained = Assert.Single(examples).Features;
        Assert.Equal(original.ToVector(), retained.ToVector());
        Assert.NotSame(original, retained);
        Assert.Equal(.001, retained.TagSimilarity);
    }

    private static TmdbDiscoverItem Candidate() => new() { Id = 123, MediaType = "tv", GenreIds = [16, 10759], VoteAverage = 8 };

    private static UserWatchProfile Profile()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, Genres = ["Animation", "Action & Adventure"], Tags = Tags });
        return profile;
    }
}
