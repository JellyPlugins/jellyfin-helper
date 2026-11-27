using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Exact v6 dimensions, fitting-only context means and detached known-versus-missing model inputs.</summary>
public sealed class ModelInputFeaturesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryExternalFittingCoordinateAndInteractionMatchesInferenceWithFreshOrRetainedMeans(bool retainedMeans)
    {
        var now = DateTime.UtcNow;
        CreditMetadataSnapshot Credits(Guid owner) => new()
        {
            SourceItemId = owner,
            Cast = new() { Complete = true, AcquiredAtUtc = now.AddDays(-5), People = [new() { Name = "Actor", Kind = PersonKind.Actor, Identities = [new("tmdb", "42")], BillingWeight = 1 }] },
            Crew = new() { Complete = true, AcquiredAtUtc = now.AddDays(-5), People = [] }
        };
        var profile = new UserWatchProfile
        {
            WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Drama"], LastPlayedDate = now.AddDays(-1), Credits = Credits(Guid.Empty) }]
        };
        var genres = new Dictionary<string, double> { ["Drama"] = 1 };
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile);
        var prior = retainedMeans ? Enumerable.Repeat(.7, 41).ToArray() : null;
        var observed = ExternalCandidateFeatureBuilder.Build(new() { Id = 700, MediaType = "movie", GenreIds = [18], VoteAverage = 7, Popularity = 80, KnownPeople = ["Actor"], Credits = Credits(Guid.Empty) }, genres, ["Actor"], 2000, exposure, profile, prior);
        var missing = ExternalCandidateFeatureBuilder.Build(new() { Id = 701, MediaType = "movie", GenreIds = [18] }, genres, ["Actor"], 2000, exposure, profile, prior);
        var holdout = observed.Copy();
        holdout.PeopleSimilarity = 1;
        TrainingExample[] examples = [new() { Features = observed }, new() { Features = missing }, new() { Features = holdout }];
        var originals = examples.Select(row => JsonSerializer.Serialize(row.Features)).ToArray();
        var vectors = examples.Select(row => row.Features.ToModelVector(prior)).ToArray();
        var means = ModelInputFeatures.FitImputation(examples, [0, 1], prior);
        ModelInputFeatures.ApplyMissing(examples, vectors, means);
        for (var index = 0; index < examples.Length; index++)
        {
            Assert.Equal(examples[index].Features.ToModelVector(means), vectors[index]);
            Assert.Equal(41, vectors[index].Length);
        }

        Assert.Equal(originals, examples.Select(row => JsonSerializer.Serialize(row.Features)));
        Assert.Equal(retainedMeans ? .7 : .5, means[(int)FeatureIndex.CombinedCriticScore]);
        Assert.Equal(observed.PeopleSimilarity, means[(int)FeatureIndex.PeopleSimilarity]);
        Assert.Equal(missing.GenreSimilarity * vectors[1][(int)FeatureIndex.CollaborativeScore], vectors[1][(int)FeatureIndex.GenreCollabInteraction]);
        Assert.Equal(missing.GenreSimilarity * vectors[1][(int)FeatureIndex.CombinedCriticScore], vectors[1][(int)FeatureIndex.GenreCriticInteraction]);
        Assert.False(missing.HasRatingMetadata);
        Assert.False(missing.HasPopularityMetadata);
        if (prior is not null) Assert.All(prior, value => Assert.Equal(.7, value));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(42)]
    public void WritingOrAllocatingUnsupportedInputDimensionsIsRejected(int count)
    {
        var features = new CandidateFeatures();
        Assert.Throws<ArgumentException>(() => ModelInputFeatures.Write(features, new double[count], null, count));
        Assert.Throws<ArgumentException>(() => ModelInputFeatures.Vector(features, null, count));
    }

    [Fact]
    public void CompleteV6NamesAndVectorsUseTheSharedContract()
    {
        var features = new CandidateFeatures { GenreSimilarity = .7, ContentFitAffinity = .6, ContentFitSupport = .4, AudienceMismatch = .2 };
        Assert.Equal(CandidateFeatures.FeatureCount, ModelInputFeatures.Names(CandidateFeatures.FeatureCount).Count);
        Assert.Equal("ContentFitAffinity", ModelInputFeatures.Name(38));
        Assert.Equal(features.ToModelVector(null), ModelInputFeatures.Vector(features, null, CandidateFeatures.FeatureCount));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong-schema")]
    [InlineData("nonfinite")]
    [InlineData("bounded")]
    public void MissingContextUsesOnlyFiniteSchemaMatchedBoundedMeans(string kind)
    {
        var features = new CandidateFeatures { GenreSimilarity = .7 };
        var captured = JsonSerializer.Serialize(features);
        var buffer = features.ToVector();
        double[]? means = kind == "none" ? null : new double[kind == "wrong-schema" ? 40 : CandidateFeatures.FeatureCount];
        if (kind == "nonfinite")
        {
            means![38] = double.NaN;
            means[39] = double.PositiveInfinity;
            means[40] = double.NegativeInfinity;
        }
        if (kind == "bounded")
        {
            means![38] = 2;
            means[39] = -1;
            means[40] = .4;
        }
        ModelInputFeatures.WriteContext(features, buffer, means);
        Assert.Equal(kind == "bounded" ? new[] { 1.0, 0, .4 } : new[] { 0.0, 0, 0 }, buffer.Skip(38));
        Assert.Equal(.7, buffer[(int)FeatureIndex.GenreSimilarity]);
        Assert.Equal(captured, JsonSerializer.Serialize(features));
    }

    [Fact]
    public void FittingMeansExcludeHoldoutAndUnknownRowsButRetainGenuineKnownZeroes()
    {
        TrainingExample[] examples =
        [
            new() { Features = new() { ContentFitAffinity = .2, ContentFitSupport = .6, AudienceMismatch = .4 } },
            new() { Features = new() { HasContentMetadata = true, ContentFitAffinity = 0 } },
            new() { Features = new() },
            new() { Features = new() { ContentFitAffinity = 1, ContentFitSupport = 1, AudienceMismatch = 1 } }
        ];
        var captured = examples.Select(example => JsonSerializer.Serialize(example.Features)).ToArray();
        var prior = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        var means = ModelInputFeatures.FitImputation(examples, [2, 1, 0], prior);
        Assert.Equal(.1, means[38], 12);
        Assert.Equal(.3, means[39], 12);
        Assert.Equal(.2, means[40], 12);
        Assert.Equal(means, ModelInputFeatures.FitImputation(examples, [0, 1, 2], prior));
        var vectors = examples.Select(example => example.Features.ToModelVector(prior)).ToArray();
        ModelInputFeatures.ApplyMissing(examples, vectors, means);
        Assert.Equal(means.Skip(38), vectors[2].Skip(38));
        Assert.Equal(new[] { 0.0, 0, 0 }, vectors[1].Skip(38));
        Assert.Equal(new[] { 1.0, 1, 1 }, vectors[3].Skip(38));
        Assert.Equal(captured, examples.Select(example => JsonSerializer.Serialize(example.Features)));
        Assert.All(prior, value => Assert.Equal(.8, value));
    }
}
