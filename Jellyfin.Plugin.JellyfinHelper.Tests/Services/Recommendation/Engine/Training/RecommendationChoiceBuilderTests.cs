using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
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
    public void AnOrphanEpisodeCannotShareAMovieProviderObservationOrWithdrawThatMoviesChoiceAlternative()
    {
        var user = Guid.NewGuid();
        var episode = Watch(Guid.NewGuid(), Prediction.AddDays(-10), Prediction.AddDays(1));
        episode.ItemType = "Episode";
        episode.TmdbId = 42;
        var movie = Watch(Guid.NewGuid(), Prediction.AddDays(-10), Prediction.AddDays(2));
        movie.TmdbId = 42;
        movie.UserRating = 1;
        var profile = Profile(user, episode, movie);
        var examples = TrainingDataBuilder.BuildExamples([], [profile], TestContext.Current.CancellationToken).Examples;
        var observed = examples.Where(row => row.IsObserved).ToArray();
        Assert.Equal(2, observed.Length);
        Assert.Equal(2, observed.Select(row => row.ObservationId).Distinct().Count());
        Assert.Equal(.1, Assert.Single(observed, row => row.ObservationId == TrainingExample.CreateProviderObservationId(user, 42, "movie")).Label);
        Assert.True(Assert.Single(observed, row => row.ObservationId == TrainingExample.CreateLibraryObservationId(user, episode.ItemId)).Label > .5);

        var episodeOnly = Profile(user, episode);
        var aliases = RecommendationChoiceBuilder.EngagedTitles(episodeOnly, new Dictionary<Guid, int> { [episode.ItemId] = 42, [movie.ItemId] = 42 });
        Assert.Contains(TrainingExample.CreateLibraryObservationId(user, episode.ItemId), aliases);
        Assert.DoesNotContain(TrainingExample.CreateProviderObservationId(user, 42, "movie"), aliases);
        Assert.DoesNotContain(TrainingExample.CreateProviderObservationId(user, 42, "tv"), aliases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrganicTargetsCannotBorrowProviderGenresAcquiredAfterTheirPrediction(bool series)
    {
        var user = Guid.NewGuid();
        var target = Watch(Guid.NewGuid(), Prediction.AddDays(-10), Prediction.AddDays(1));
        target.LocalGenres = [];
        target.Genres = ["Documentary"];
        target.TitleMetadata = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.TitleMetadataSnapshot
        {
            SourceGenres = ["Documentary"], FetchedAtUtc = Prediction.AddDays(2)
        };
        if (series)
        {
            target.ItemType = "Episode";
            target.SeriesId = Guid.NewGuid();
        }

        var examples = TrainingDataBuilder.BuildExamples([], [Profile(user, target)], TestContext.Current.CancellationToken).Examples;
        var observed = Assert.Single(examples, row => row.IsObserved);
        Assert.Equal(0, observed.Features.GenreCount);
        Assert.False(observed.Features.HasGenreEvidence);
        Assert.Equal(target.LastPlayedDate, observed.GeneratedAtUtc);
        Assert.True(observed.Label > .5);
        Assert.Equal(new[] { "Documentary" }, target.Genres);
    }

    [Fact]
    public void RepeatedSavedPredictionsKeepTheirOwnFeaturesTimestampsAndMatchingChoices()
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, false);
        var other = Title(user, false);
        var firstFeatures = new CandidateFeatures { GenreSimilarity = .2, CombinedCriticScore = .3 };
        var secondFeatures = new CandidateFeatures { GenreSimilarity = .8, CombinedCriticScore = .9 };
        var first = Result(user, Item(chosen, .2, firstFeatures), Item(other, .9));
        first.GeneratedAt = Prediction.AddDays(-1);
        var second = Result(user, Item(chosen, .8, secondFeatures), Item(other, .9));
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)));
        var examples = TrainingDataBuilder.BuildExamples([first, second], [profile], TestContext.Current.CancellationToken).Examples;
        var positives = examples.Where(row => row.IsObserved).OrderBy(row => row.GeneratedAtUtc).ToArray();
        Assert.Equal(2, positives.Length);
        Assert.Equal(first.GeneratedAt, positives[0].GeneratedAtUtc);
        Assert.Equal(second.GeneratedAt, positives[1].GeneratedAtUtc);
        Assert.Equal(firstFeatures.ToVector(), positives[0].Features.ToVector());
        Assert.Equal(secondFeatures.ToVector(), positives[1].Features.ToVector());
        Assert.All(positives, row =>
        {
            Assert.Equal(row.GeneratedAtUtc, row.Choice!.RecommendedAtUtc);
            Assert.Equal(row.Features.ToVector(), row.Choice.ChosenFeatures.ToVector());
        });
        Assert.Equal(.2, firstFeatures.GenreSimilarity);
        Assert.Equal(.8, secondFeatures.GenreSimilarity);
    }

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

    [Fact]
    public void ARealLegacyChoiceMayInspectOtherCandidatesWithoutTurningUnavailableTitlesIntoDislikes()
    {
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var chosen = Title(user, heldout: false);
        var alternative = Title(user, heldout: false);
        var unoffered = Guid.NewGuid();
        var result = Result(user, Item(chosen, .8), Item(alternative, .7));
        foreach (var item in result.Recommendations) item.ScoringFeatures = null;
        var earlierObservedChoice = Result(user, Item(chosen, .8), Item(alternative, .7));
        earlierObservedChoice.GeneratedAt = Prediction.AddDays(-1);
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(-10), Prediction.AddDays(1)));
        var credits = new CreditMetadataSnapshot
        {
            SourceItemId = unoffered, MediaType = "tv",
            Cast = new CreditField { Complete = true, AcquiredAtUtc = Prediction.AddDays(-1), People = [] },
            Crew = new CreditField { Complete = true, AcquiredAtUtc = Prediction.AddDays(-1), People = [] }
        };
        var unseen = new RecommendedItem { ItemId = unoffered, ItemType = "Series", Genres = ["Drama"], DateCreated = Prediction.AddDays(2), Credits = credits };
        var otherResult = Result(otherUser, unseen);
        var counts = new Dictionary<Guid, int> { [unoffered] = 5 };
        var built = TrainingDataBuilder.BuildExamples([earlierObservedChoice, result, otherResult], [profile, new UserWatchProfile { UserId = otherUser }], null, counts, null, null, null, TestContext.Current.CancellationToken);
        Assert.Contains(built.Examples, row => row.UserId == user && row.IsObserved && row.ObservationId == TrainingExample.CreateLibraryObservationId(user, chosen));
        Assert.DoesNotContain(built.Examples, row => row.UserId == user && row.ObservationId == TrainingExample.CreateLibraryObservationId(user, unoffered));
        Assert.Equal(1, built.RandomNegativeCount);
        Assert.Equal(TrainingExample.CreateLibraryObservationId(user, alternative), Assert.Single(built.Examples, row => row.UserId == user && row.IsContrastSample).ObservationId);
        Assert.Same(credits, unseen.Credits);
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
    [InlineData(-10, 2, true)]
    [InlineData(-10, 0, false)]
    [InlineData(-10, 1, false)]
    [InlineData(0.5, 2, false)]
    public void AnOrganicWatchComparesWithThePlaylistOnlyWhenItsMaterialAlreadyExisted(double addedDay, int alternativeVersion, bool comparison)
    {
        var user = Guid.NewGuid();
        var chosen = Title(user, heldout: false);
        var other = Title(user, heldout: false);
        // An organic reconstruction uses the current builder. A saved legacy alternative
        // must keep its original derivation, not be relabeled to manufacture a comparison.
        var alternative = new CandidateFeatures { FeatureDerivationVersion = alternativeVersion, GenreSimilarity = .95, CombinedCriticScore = .8 };
        var original = alternative.ToVector();
        var result = Result(user, Item(other, .95, alternative));
        var profile = Profile(user, Watch(chosen, Prediction.AddDays(addedDay), Prediction.AddDays(1)));
        var examples = TrainingDataBuilder.BuildExamples([result], [profile], TestContext.Current.CancellationToken).Examples;
        var positive = Assert.Single(examples, example => example.IsObserved && example.Choice is null);
        var choice = examples.SingleOrDefault(example => example.Choice is not null);
        var unpublished = Assert.Single(examples, example => !example.IsObserved);
        Assert.Equal(comparison, choice is not null);
        Assert.Equal(Prediction.AddDays(1), positive.GeneratedAtUtc);
        Assert.Equal(.7, examples.Where(example => example.IsObserved).Sum(example => example.SampleWeight), 12);
        if (choice is not null)
        {
            Assert.Equal(Prediction, choice.GeneratedAtUtc);
            Assert.NotSame(positive.Features, choice.Features);
        }
        Assert.Equal(comparison, unpublished.IsContrastSample);
        Assert.True(positive.Label > .5);
        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, positive.Features.FeatureDerivationVersion);
        Assert.Equal(alternativeVersion, alternative.FeatureDerivationVersion);
        Assert.Equal(original, alternative.ToVector());
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
