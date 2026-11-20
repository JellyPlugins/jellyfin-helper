using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Natural choices, chronology and genuine available alternatives; publication alone never becomes rejection.</summary>
public sealed class RecommendationChoiceBuilderTests
{
    private static readonly DateTime Prediction = DateTime.UtcNow.AddDays(-3);

    [Fact]
    public void ChoosingALowerRankedRecommendationPreservesItsPredictionAndContrastsTheAvailableAlternative()
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, heldout: false);
        var other = Title(user, heldout: false);
        var features = new CandidateFeatures { GenreSimilarity = .2, CombinedCriticScore = .8 };
        var result = Result(user, Item(other, .95), Item(chosen, .2, features));
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)));
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var positive = Assert.Single(examples, example => example.IsObserved);
        var contrast = Assert.Single(examples, example => !example.IsObserved);
        Assert.Equal(features.ToVector(), positive.Features.ToVector());
        Assert.NotSame(features, positive.Features);
        Assert.NotNull(positive.Choice);
        Assert.Single(positive.Choice.Alternatives);
        Assert.True(contrast.IsContrastSample);
        Assert.Equal(.25, contrast.SampleWeight);
        Assert.Equal(.05, contrast.Label);
        Assert.True(positive.Label > .5);
    }

    [Theory]
    [InlineData(20, 3, 8)]
    [InlineData(100, 3, 8)]
    [InlineData(100, 60, 8)]
    [InlineData(100, 180, 6)]
    [InlineData(100, 400, 1)]
    [InlineData(100, 450, 0)]
    public void LargeOrAgedFittingSetsKeepABoundedEligibleContrastSignalWithOriginalTimes(int count, int age, int expectedCount)
    {
        var user = Guid.NewGuid();
        var predictedAt = DateTime.UtcNow.AddDays(-age);
        var selected = Enumerable.Range(0, 7).Select(_ => Title(user, false)).ToArray();
        var alternatives = Enumerable.Range(0, count).Select(_ => Item(Title(user, false), .95)).ToArray();
        var result = Result(user, selected.Select(id => Item(id, .2)).Concat(alternatives).ToArray());
        result.GeneratedAt = predictedAt;
        var profile = Profile(user, selected.Select((id, index) => Watch(id, predictedAt.AddDays(-10), predictedAt.AddDays(1).AddHours(index))).ToArray());
        var reference = DateTime.UtcNow;
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var contrasts = examples.Where(example => example.IsContrastSample).ToArray();
        Assert.Equal(expectedCount, contrasts.Length);
        Assert.All(contrasts, example =>
        {
            Assert.False(example.IsObserved);
            Assert.Equal(predictedAt, example.GeneratedAtUtc);
            Assert.True(example.ComputeEffectiveWeight(reference) >= LearnedScoringStrategy.MinSampleWeight);
        });
        Assert.Equal(expectedCount > 0 ? .25 : 0, contrasts.Sum(example => example.SampleWeight), 12);
        Assert.Equal(contrasts.Length, TrainingMath.FilterEffectiveExamples(contrasts, reference, LearnedScoringStrategy.MinSampleWeight).Count);
        Assert.All(examples.Where(example => example.IsObserved), example => Assert.NotNull(example.Choice));
        Assert.Equal(20, examples.Where(example => example.IsObserved).SelectMany(example => example.Choice!.Alternatives)
            .Select(alternative => alternative.ObservationId).Distinct().Count());
        var observedIds = examples.Where(example => example.IsObserved).Select(example => example.ObservationId).ToHashSet();
        Assert.All(examples.Where(example => example.Choice is not null).SelectMany(example => example.Choice!.Alternatives),
            alternative => Assert.DoesNotContain(alternative.ObservationId, observedIds));
        Assert.All(examples.Where(example => !example.IsObserved && !example.IsContrastSample), example =>
            Assert.Empty(TrainingMath.FilterEffectiveExamples([example], reference, LearnedScoringStrategy.MinSampleWeight)));
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(0.5, false)]
    public void AnOrganicWatchComparesWithThePlaylistOnlyWhenItsMaterialAlreadyExisted(double addedDay, bool comparison)
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, heldout: false);
        var other = Title(user, heldout: false);
        var result = Result(user, Item(other, .95));
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(addedDay), Prediction.AddDays(1)));
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var positive = Assert.Single(examples, example => example.IsObserved);
        var unpublished = Assert.Single(examples, example => !example.IsObserved);
        Assert.Equal(comparison, positive.Choice is not null);
        Assert.Equal(comparison, unpublished.IsContrastSample);
        Assert.True(positive.Label > .5);
        Assert.Single(TrainingMath.FilterEffectiveExamples([positive], DateTime.UtcNow, .01));
        if (!comparison) Assert.Empty(TrainingMath.FilterEffectiveExamples([unpublished], DateTime.UtcNow, .01));
    }

    [Fact]
    public void NoPlaybackAndRepeatedTaskPublicationCannotManufactureNegativeEvidence()
    {
        var user = Guid.NewGuid();
        var result = Result(user, Item(Title(user, false), .95));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user)], TestContext.Current.CancellationToken).Examples;
        var sample = Assert.Single(examples);
        Assert.False(sample.IsObserved);
        Assert.False(sample.IsContrastSample);
        Assert.Null(sample.Choice);
        Assert.Empty(TrainingMath.FilterEffectiveExamples(examples, DateTime.UtcNow, .01));
    }

    [Fact]
    public void ANewEpisodeOfAnExistingListedSeriesIsNotEvidenceAgainstThePlaylist()
    {
        var user = Guid.NewGuid();
        var series = Title(user, false);
        var other = Title(user, false);
        var episode = Watch(Guid.NewGuid(), Prediction.AddHours(1), Prediction.AddDays(1));
        episode.SeriesId = series;
        episode.ItemType = "Episode";
        var result = Result(user, Item(other, .9), Item(series, .2));
        result.Recommendations[1].ItemType = "Series";
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, episode)], TestContext.Current.CancellationToken).Examples;
        Assert.Single(examples, example => example.IsObserved);
        Assert.All(examples, example => Assert.Null(example.Choice));
        Assert.False(Assert.Single(examples, example => !example.IsObserved).IsContrastSample);
    }

    [Fact]
    public void AnUnknownLibraryAdditionTimeForAnOrganicWatchDoesNotInventAnOpportunity()
    {
        var user = Guid.NewGuid();
        var watch = Watch(Title(user, false), Prediction.AddDays(-10), Prediction.AddDays(1));
        watch.DateCreated = null;
        var result = Result(user, Item(Title(user, false), .9));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, watch)], TestContext.Current.CancellationToken).Examples;
        Assert.Null(Assert.Single(examples, example => example.IsObserved).Choice);
        Assert.False(Assert.Single(examples, example => !example.IsObserved).IsContrastSample);
    }

    [Fact]
    public void HeldOutChoiceAlternativesCannotLeakThatChoiceIntoFitting()
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, true);
        var result = Result(user, Item(Title(user, false), .9), Item(chosen, .2));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)))], TestContext.Current.CancellationToken).Examples;
        var positive = Assert.Single(examples, example => example.IsObserved);
        Assert.True(positive.IsPermanentHoldout);
        Assert.NotNull(positive.Choice);
        Assert.False(Assert.Single(examples, example => !example.IsObserved).IsContrastSample);
    }

    [Fact]
    public void AnUndatedSeriesFavoriteIsNotAnUnobservedChoiceAlternativeOrPartOfTheContrastBudget()
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var favorite = Title(user, false);
        var other = Title(user, false);
        var favoriteItem = Item(favorite, .9);
        favoriteItem.ItemType = "Series";
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)));
        profile.FavoriteSeriesIds.Add(favorite);
        var result = Result(user, favoriteItem, Item(other, .8), Item(chosen, .2));
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var observation = Assert.Single(examples, example => example.Choice is not null);
        Assert.Equal(TrainingExample.CreateTitleObservationId(user, other, 0, false), Assert.Single(observation.Choice!.Alternatives).ObservationId);
        Assert.Equal(.25, Assert.Single(examples, example => example.IsContrastSample).SampleWeight);
        Assert.Equal(2, examples.Count(example => example.IsObserved));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void AWatchOutsideTheOpportunityWindowDoesNotLabelThePlaylistIgnored(int playedDay)
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var result = Result(user, Item(Title(user, false), .9), Item(chosen, .2));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(playedDay)))], TestContext.Current.CancellationToken).Examples;
        Assert.All(examples, example => Assert.Null(example.Choice));
        Assert.False(Assert.Single(examples, example => !example.IsObserved).IsContrastSample);
    }

    [Fact]
    public void ACompletedButExplicitlyDislikedWatchDoesNotClaimPreferenceOverAlternatives()
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var watch = Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1));
        watch.UserRating = 1;
        var result = Result(user, Item(Title(user, false), .9), Item(chosen, .2));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, watch)], TestContext.Current.CancellationToken).Examples;
        var observed = Assert.Single(examples, example => example.IsObserved);
        Assert.Equal(.1, observed.Label);
        Assert.Null(observed.Choice);
        Assert.False(Assert.Single(examples, example => !example.IsObserved).IsContrastSample);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAlternativeWithUnusableEngagementTimeCannotSupplyAChronologicallyUnobservedComparison(bool future)
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var other = Title(user, false);
        var undated = Watch(other, Prediction.AddDays(-10), Prediction.AddDays(1));
        undated.LastPlayedDate = future ? Prediction.AddDays(10) : null;
        var result = Result(user, Item(other, .9), Item(chosen, .2));
        var examples = TrainingDataBuilder.BuildExamples([result], [Profile(user, undated, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)))], TestContext.Current.CancellationToken).Examples;
        Assert.All(examples, example => Assert.Null(example.Choice));
        Assert.All(examples, example => Assert.False(example.IsContrastSample));
        Assert.Equal(2, examples.Count(example => example.IsObserved));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AReplayedOrResumedTitleCannotBecomeAnUnobservedAlternativeFromItsLastPlayDate(int starts)
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var replayed = Title(user, false);
        var unseen = Title(user, false);
        var later = Watch(replayed, Prediction.AddDays(-10), Prediction.AddDays(2));
        later.PlayCount = starts;
        var result = Result(user, Item(replayed, .9), Item(unseen, .8), Item(chosen, .2));
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)), later);
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var unobservedId = TrainingExample.CreateTitleObservationId(user, unseen, 0, false);
        Assert.All(examples.Where(example => example.IsObserved), example =>
            Assert.Equal(unobservedId, Assert.Single(example.Choice!.Alternatives).ObservationId));
        Assert.Equal(unobservedId, Assert.Single(examples, example => example.IsContrastSample).ObservationId);
        Assert.Equal(2, examples.Count(example => example.IsObserved));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidOpportunityTimesOrUserScopesReturnNoChoicePlan(int invalid)
    {
        var user = Guid.NewGuid();
        var result = Result(user, Item(Guid.NewGuid(), .9));
        var profile = Profile(user);
        if (invalid == 0) result.GeneratedAt = DateTime.UnixEpoch;
        else if (invalid == 1) result.GeneratedAt = DateTime.UtcNow.AddDays(1);
        else profile.UserId = Guid.NewGuid();
        var plan = RecommendationChoiceBuilder.Build(new(result, profile, new Dictionary<Guid, int>(), DateTime.UtcNow), (_, _) => throw new InvalidOperationException("Invalid scopes must not capture features."));
        Assert.Empty(plan.Choices);
        Assert.Empty(plan.FittingAlternatives);
    }

    private static Guid Title(Guid user, bool heldout)
    {
        while (true)
        {
            var id = Guid.NewGuid();
            var example = new TrainingExample { Features = new CandidateFeatures(), ObservationId = TrainingExample.CreateTitleObservationId(user, id, 0, false), HasStableObservationId = true };
            if (example.IsPermanentHoldout == heldout) return id;
        }
    }

    private static RecommendedItem Item(Guid id, double genre, CandidateFeatures? features = null)
        => new() { ItemId = id, ItemType = "Movie", Genres = ["Drama"], DateCreated = Prediction.AddDays(-10), ScoringFeatures = features ?? new CandidateFeatures { GenreSimilarity = genre, CombinedCriticScore = .8 } };

    private static RecommendationResult Result(Guid user, params RecommendedItem[] items)
        => new() { UserId = user, GeneratedAt = Prediction, Recommendations = new(items.ToList()) };

    private static WatchedItemInfo Watch(Guid id, DateTime added, DateTime played)
        => new() { ItemId = id, ItemType = "Movie", Played = true, PlayCount = 1, Genres = ["Drama"], Year = 2020, DateCreated = added, LastPlayedDate = played, RuntimeTicks = 100, PlaybackPositionTicks = 100 };

    private static UserWatchProfile Profile(Guid user, params WatchedItemInfo[] rows)
        => new() { UserId = user, WatchedItems = new(rows.ToList()) };
}
