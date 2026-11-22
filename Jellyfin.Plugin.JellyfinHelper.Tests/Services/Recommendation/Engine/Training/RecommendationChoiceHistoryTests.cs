using System.Collections.Immutable;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Actual choice evidence survives publication and restarts without becoming fresh or leaking held-out outcomes.</summary>
public sealed class RecommendationChoiceHistoryTests
{
    [Fact]
    public void RemovedOwnersArePrunedBeforeAnyActiveProfilePermissionLookup()
    {
        var now = DateTime.UtcNow;
        var removed = Guid.NewGuid();
        var active = Guid.NewGuid();
        var original = Observation(removed, Identity(false), now, true);
        original.Choice = original.Choice! with
        {
            ChosenItemId = Guid.NewGuid(),
            Alternatives = original.Choice.Alternatives.Select(alternative => alternative with { LibraryItemId = Guid.NewGuid() }).ToImmutableArray()
        };
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("removed-owner-choices.json");
        var history = new RecommendationChoiceHistory(fs, path);
        history.Merge([original], new HashSet<Guid> { removed }, now);
        Assert.True(history.Save());
        var permissions = new Dictionary<Guid, HashSet<Guid>> { [active] = new HashSet<Guid>() };
        Assert.Empty(history.Merge(
            [],
            new HashSet<Guid> { active },
            now,
            canUseLibraryItem: (user, item) => permissions[user].Contains(item),
            canUseCapturedItem: (user, item, _) => permissions[user].Contains(item)));
        Assert.True(history.Save());
        var restarted = new RecommendationChoiceHistory(fs, path);
        var current = original.Copy();
        current.Choice = null;
        Assert.DoesNotContain(restarted.Merge([current], new HashSet<Guid> { removed }, now), row => row.Choice is not null || row.IsContrastSample);
    }

    [Fact]
    public void MissingLegacyPhysicalIdsStayDormantAcrossSavingRatherThanUsingAnAccessibleDuplicateOrErasingEvidence()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var original = Observation(user, Identity(false), now, true);
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("legacy-physical-choice.json");
        var history = new RecommendationChoiceHistory(fs, path);
        history.Merge([original], new HashSet<Guid> { user }, now);
        Assert.True(history.Save());
        var before = fs.File.ReadAllText(path);
        var current = original.Copy();
        current.Choice = null;
        var eligible = new HashSet<(Guid User, Guid Title)> { (user, original.ObservationId), (user, original.Choice!.Alternatives[0].ObservationId) };
        var fitting = history.Merge([current], new HashSet<Guid> { user }, now, eligibleTitles: eligible, canUseLibraryItem: (_, _) => true);
        Assert.DoesNotContain(fitting, row => row.Choice is not null || row.IsContrastSample);
        Assert.True(history.Save());
        Assert.Equal(before, fs.File.ReadAllText(path));
        var restarted = new RecommendationChoiceHistory(fs, path);
        Assert.DoesNotContain(restarted.Merge([current], new HashSet<Guid> { user }, now, eligibleTitles: eligible, canUseLibraryItem: (_, _) => true), row => row.Choice is not null || row.IsContrastSample);
    }
    [Fact]
    public void UnchosenPlaylistAlternativeKeepsItsRelativeSignalAcrossLaterRunsWithoutBecomingAPermanentBan()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var original = Observation(user, Identity(false), now, true);
        var alternative = Assert.Single(original.Choice!.Alternatives);
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now);
        foreach (var run in Enumerable.Range(1, 3))
        {
            var current = Observation(user, original.ObservationId, now.AddDays(run), false);
            var merged = history.Merge([current], new HashSet<Guid> { user }, now.AddDays(run));
            var retained = Assert.Single(merged, row => row.IsContrastSample && row.ObservationId == alternative.ObservationId);
            Assert.False(retained.IsObserved);
            Assert.Equal(original.GeneratedAtUtc, retained.GeneratedAtUtc);
            Assert.Equal(alternative.Features.ToVector(), retained.Features.ToVector());
            Assert.Equal(.25, retained.SampleWeight);
            Assert.True(ScoringHelper.IsContentEligible(retained.Features));
        }

        var engaged = new HashSet<(Guid User, Guid Title)> { (user, alternative.ObservationId) };
        var changed = history.Merge([Observation(user, original.ObservationId, now.AddDays(4), false)], new HashSet<Guid> { user }, now.AddDays(4), engaged);
        Assert.DoesNotContain(changed, row => row.IsContrastSample && row.ObservationId == alternative.ObservationId);
        Assert.True(ScoringHelper.IsContentEligible(alternative.Features));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalChoicePersistenceBoundsItsWaitKeepsOneWriterAndDoesNotLoseNewerGenerations(bool cancel)
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var firstRow = Observation(user, Identity(false), now, true);
        var secondRow = Observation(user, Identity(false), now, true);
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<IDirectory>().Object);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        file.Setup(value => value.WriteAllBytes(It.IsAny<string>(), It.IsAny<byte[]>())).Callback(() =>
        {
            started.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        file.Setup(value => value.Move(It.IsAny<string>(), It.IsAny<string>(), true)).Callback(() => persisted.TrySetResult());
        var waitClock = new Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.ControlledWaitTimeProvider();
        var history = new RecommendationChoiceHistory(fs.Object, Path.GetFullPath("bounded-choice-writer.json"))
        {
            WaitTimeProvider = waitClock
        };
        history.Merge([firstRow], new HashSet<Guid> { user }, now);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = history.SaveAsync(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            else
            {
                waitClock.ExpireDeadlines();
                Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }

            history.Merge([firstRow, secondRow], new HashSet<Guid> { user }, now);
            var pendingWait = history.SaveAsync(TestContext.Current.CancellationToken);
            waitClock.ExpireDeadlines();
            Assert.False(await pendingWait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            file.Verify(value => value.WriteAllBytes(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Once);
            var joining = history.SaveAsync(TestContext.Current.CancellationToken);
            release.Set();
            Assert.False(await joining);
            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(await history.SaveAsync(TestContext.Current.CancellationToken));
            file.Verify(value => value.WriteAllBytes(It.IsAny<string>(), It.Is<byte[]>(bytes => System.Text.Encoding.UTF8.GetString(bytes).Contains(secondRow.ObservationId.ToString(), StringComparison.OrdinalIgnoreCase))), Times.Once);
        }
        finally
        {
            release.Set();
            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OptionalChoiceAsyncPersistenceHonorsPreCancellationAndAbsentStorage()
    {
        var history = new RecommendationChoiceHistory();
        Assert.False(await history.SaveAsync(TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => history.SaveAsync(cancellation.Token));
    }
    [Fact]
    public void SplittingAnAgedChoiceCannotRemoveAnOtherwiseEligibleOrganicObservation()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(false);
        var original = Observation(user, id, now.AddDays(-538), true);
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now.AddDays(-537));
        var current = Observation(user, id, now.AddDays(-537), false);
        current.SampleWeight = .7;
        Assert.True(original.ComputeEffectiveWeight(now) >= LearnedScoringStrategy.MinSampleWeight);
        Assert.True(current.ComputeEffectiveWeight(now) >= LearnedScoringStrategy.MinSampleWeight);
        var result = history.Merge([current], new HashSet<Guid> { user }, now);
        Assert.Same(current, Assert.Single(result));
        Assert.Equal(.7, current.SampleWeight);
        Assert.Single(TrainingMath.FilterEffectiveExamples(result, now, LearnedScoringStrategy.MinSampleWeight));
    }

    [Theory]
    [InlineData(0, false, 8)]
    [InlineData(0, true, 8)]
    [InlineData(180, true, 6)]
    public void ReplayedChoicesShareOneOpportunityContrastLimitAndBudgetEvenWithFreshOverlap(int age, bool fresh, int limit)
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var alternatives = Enumerable.Range(0, 20).Select(_ => new RecommendationChoiceAlternative(Identity(false), new CandidateFeatures { GenreSimilarity = .9 })).ToImmutableArray();
        var originals = Enumerable.Range(0, 7).Select(_ => Observation(user, Identity(false), now, true)).ToArray();
        foreach (var row in originals) row.Choice = row.Choice! with { Alternatives = alternatives };
        var history = new RecommendationChoiceHistory();
        history.Merge(originals, new HashSet<Guid> { user }, now);
        var current = originals.Select(row => Observation(user, row.ObservationId, now.AddDays(age), false)).ToList();
        if (fresh)
        {
            current[0] = originals[0].Copy();
            current.AddRange(alternatives.Take(8).Select(alternative => new TrainingExample
            {
                UserId = user, ObservationId = alternative.ObservationId, HasStableObservationId = true, IsObserved = false, IsContrastSample = true,
                GeneratedAtUtc = originals[0].GeneratedAtUtc, SampleWeight = .25 / 8, Label = .05, Features = alternative.Features.Copy()
            }));
        }

        var reference = now.AddDays(age);
        var result = history.Merge(current, new HashSet<Guid> { user }, reference);
        var contrasts = result.Where(row => row.IsContrastSample).ToArray();
        Assert.Equal(limit, contrasts.Length);
        Assert.Equal(limit, contrasts.Select(row => row.ObservationId).Distinct().Count());
        Assert.Equal(.25, contrasts.Sum(row => row.SampleWeight), 12);
        Assert.Equal(7, result.Count(row => row.Choice is not null));
        Assert.All(contrasts, row => Assert.Equal(originals[0].GeneratedAtUtc, row.GeneratedAtUtc));
        Assert.Equal(limit, TrainingMath.FilterEffectiveExamples(contrasts, reference, LearnedScoringStrategy.MinSampleWeight).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EngagedLibraryAliasesAreWithdrawnAfterProviderIdentityAcquisitionWithoutConflatingMovieAndTv(bool series)
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var library = Guid.NewGuid();
        var libraryAlias = TrainingExample.CreateLibraryObservationId(user, library);
        var opposite = TrainingExample.CreateTitleObservationId(user, library, 123, !series);
        var original = Observation(user, Identity(false), now, true);
        original.Choice = original.Choice! with { Alternatives = [new(libraryAlias, new CandidateFeatures()), new(opposite, new CandidateFeatures())] };
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now);
        var row = new WatchedItemInfo { ItemId = series ? Guid.NewGuid() : library, SeriesId = series ? library : null,
            ItemType = series ? "Episode" : "Movie", TmdbId = series ? 999 : 123, SeriesTmdbId = series ? 123 : 0, PlaybackPositionTicks = 10 };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [row] };
        var aliases = RecommendationChoiceBuilder.EngagedTitles(profile, new Dictionary<Guid, int> { [library] = 123 });
        Assert.Contains(libraryAlias, aliases);
        Assert.DoesNotContain(opposite, aliases);
        var result = history.Merge([Observation(user, original.ObservationId, now, false)], new HashSet<Guid> { user }, now,
            aliases.Select(alias => (user, alias)).ToHashSet());
        var choice = Assert.Single(result, item => item.Choice is not null).Choice!;
        Assert.Equal(opposite, Assert.Single(choice.Alternatives).ObservationId);
        Assert.DoesNotContain(result, item => item.IsContrastSample && item.ObservationId == libraryAlias);
    }

    [Fact]
    public void ReplayKeepsHistoricalFeaturesAndAgeButUsesTheLatestPositiveOutcome()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var original = Observation(user, Identity(false), now, true);
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now);
        var current = Observation(user, original.ObservationId, now.AddDays(1), false);
        current.Label = .6;
        current.Features.GenreSimilarity = .9;
        var result = history.Merge([current], new HashSet<Guid> { user }, now.AddDays(1));
        var replay = Assert.Single(result, row => row.Choice is not null);
        Assert.Equal(.6, replay.Label);
        Assert.Equal(original.Features.ToVector(), replay.Features.ToVector());
        Assert.Equal(original.GeneratedAtUtc, replay.GeneratedAtUtc);
        Assert.Equal(.85, original.Label);
        Assert.Equal(.6, current.Label);
    }

    [Fact]
    public void ChoiceSurvivesTheNextPlaylistAndRestartWithoutChangingCurrentPredictionsOrItsAge()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(heldout: false);
        var original = Observation(user, id, now, choice: true);
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("choice-history.json");
        var history = new RecommendationChoiceHistory(fs, path);
        Assert.Single(history.Merge([original], new HashSet<Guid> { user }, now));
        Assert.True(history.Save());

        var current = Observation(user, id, now, choice: false);
        current.Features.GenreSimilarity = .9;
        var merged = history.Merge([current], new HashSet<Guid> { user }, now.AddDays(1));
        Assert.NotSame(current, merged[0]);
        Assert.Equal(current.Features.ToVector(), merged[0].Features.ToVector());
        Assert.Equal(1, current.SampleWeight);
        Assert.Equal(1, merged.Where(row => row.IsObserved).Sum(row => row.SampleWeight), 12);
        Assert.Equal(.9, current.Features.GenreSimilarity);
        var replay = Assert.Single(merged, row => row.Choice is not null);
        Assert.Equal(original.GeneratedAtUtc, replay.GeneratedAtUtc);
        Assert.Equal(original.Features.ToVector(), replay.Features.ToVector());
        Assert.True(Assert.Single(merged, row => !row.IsObserved).IsContrastSample);
        Assert.Equal(.25, Assert.Single(merged, row => !row.IsObserved).SampleWeight);
        replay.Features.GenreSimilarity = 0;
        Assert.True(history.Save());

        var restarted = new RecommendationChoiceHistory(fs, path);
        var afterRestart = restarted.Merge([current], new HashSet<Guid> { user }, now.AddDays(2));
        var preserved = Assert.Single(afterRestart, row => row.Choice is not null);
        Assert.Equal(.2, preserved.Features.GenreSimilarity);
        Assert.Equal(original.GeneratedAtUtc, preserved.GeneratedAtUtc);
        Assert.Equal(original.ComputeTemporalWeight(now.AddDays(2)), preserved.ComputeTemporalWeight(now.AddDays(2)), 12);
        Assert.Equal(41, preserved.Features.ToVector().Length);
    }

    [Fact]
    public void HeldOutChoiceNeverCreatesFittingComparisonsEvenWhenAlternativesBelongToTheFittingFold()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(heldout: true);
        var history = new RecommendationChoiceHistory();
        history.Merge([Observation(user, id, now, choice: true)], new HashSet<Guid> { user }, now);
        var replay = history.Merge([Observation(user, id, now, choice: false)], new HashSet<Guid> { user }, now.AddDays(1));
        Assert.Equal(2, replay.Count);
        Assert.All(replay, row => Assert.True(row.IsObserved));
        Assert.All(replay, row => Assert.True(row.IsPermanentHoldout));
    }

    [Theory]
    [InlineData(.85)]
    [InlineData(.1)]
    public void ACurrentlyObservedAlternativeIsWithdrawnFromReplayFittingAndAdmissionWithoutChangingActualOutcomes(double laterLabel)
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var chosen = Identity(false);
        var later = Identity(false);
        var unseen = Identity(false);
        var original = Observation(user, chosen, now, true);
        original.Choice = original.Choice! with { Alternatives = [new(later, new CandidateFeatures { GenreSimilarity = .8 }), new(unseen, new CandidateFeatures { GenreSimilarity = .7 })] };
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("withdraw-engaged-choice.json");
        var history = new RecommendationChoiceHistory(fs, path);
        history.Merge([original], new HashSet<Guid> { user }, now);
        var current = Observation(user, chosen, now, false);
        var watchedLater = Observation(user, later, now, false);
        watchedLater.Label = laterLabel;
        var projected = history.Merge([current, watchedLater], new HashSet<Guid> { user }, now.AddDays(1));
        var replay = Assert.Single(projected, row => row.Choice is not null);
        Assert.Equal(unseen, Assert.Single(replay.Choice!.Alternatives).ObservationId);
        var comparison = Assert.Single(projected, row => row.IsContrastSample);
        Assert.Equal(unseen, comparison.ObservationId);
        Assert.Equal(.25, comparison.SampleWeight);
        Assert.Equal(laterLabel, Assert.Single(projected, row => row.ObservationId == later).Label);
        Assert.Equal(1, current.SampleWeight);
        Assert.Equal(1, watchedLater.SampleWeight);
        Assert.Equal(2, original.Choice.Alternatives.Length);
        Assert.Equal(original.GeneratedAtUtc, replay.GeneratedAtUtc);
        Assert.Equal(original.Features.ToVector(), replay.Features.ToVector());
        Assert.True(history.Save());
        var restarted = new RecommendationChoiceHistory(fs, path).Merge([current], new HashSet<Guid> { user }, now.AddDays(2));
        Assert.Equal(unseen, Assert.Single(Assert.Single(restarted, row => row.Choice is not null).Choice!.Alternatives).ObservationId);
        Assert.DoesNotContain(restarted, row => row.ObservationId == later);
    }

    [Fact]
    public void AChoiceWithNoRemainingUnobservedAlternativesIsWithdrawnWithoutRelabelingItsWatch()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var chosen = Identity(false);
        var original = Observation(user, chosen, now, true);
        var alternative = original.Choice!.Alternatives[0].ObservationId;
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now);
        var current = new[] { Observation(user, chosen, now, false), Observation(user, alternative, now, false) };
        var result = history.Merge(current, new HashSet<Guid> { user }, now);
        Assert.Equal(2, result.Count);
        Assert.All(result, row => { Assert.True(row.IsObserved); Assert.Null(row.Choice); Assert.Equal(.85, row.Label); });
        Assert.All(current, row => Assert.Equal(1, row.SampleWeight));
    }

    [Fact]
    public void AnUnfinishedKnownEngagementAlsoCannotReceiveAReplayContrast()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var original = Observation(user, Identity(false), now, true);
        var alternative = original.Choice!.Alternatives[0].ObservationId;
        var history = new RecommendationChoiceHistory();
        history.Merge([original], new HashSet<Guid> { user }, now);
        var result = history.Merge([Observation(user, original.ObservationId, now, false)], new HashSet<Guid> { user }, now,
            new HashSet<(Guid, Guid)> { (user, alternative) });
        var observation = Assert.Single(result);
        Assert.Null(observation.Choice);
        Assert.False(observation.IsContrastSample);
    }

    [Fact]
    public void IndependentChosenTitlesAccumulateAcrossDifferentPlaylistGenerations()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var first = Identity(heldout: true);
        var second = Identity(heldout: true);
        var history = new RecommendationChoiceHistory();
        history.Merge([Observation(user, first, now, choice: true)], new HashSet<Guid> { user }, now);
        history.Merge([Observation(user, first, now, choice: false), Observation(user, second, now, choice: true)], new HashSet<Guid> { user }, now);
        var result = history.Merge([Observation(user, first, now, choice: false), Observation(user, second, now, choice: false)], new HashSet<Guid> { user }, now.AddDays(1));
        Assert.Equal(2, result.Count(row => row.Choice is not null));
        Assert.Equal(2, result.Select(row => row.ObservationId).Distinct().Count());
    }

    [Fact]
    public void PersistedChoicesProvideIndependentAdmissionSupportAfterTheOriginalPlaylistsAreGone()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var observed = Enumerable.Range(0, 40).Select(_ => Observation(user, Identity(heldout: true), now, choice: true)).ToArray();
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("choice-admission-history.json");
        var history = new RecommendationChoiceHistory(fs, path);
        history.Merge(observed, new HashSet<Guid> { user }, now);
        Assert.True(history.Save());
        var current = observed.Select(row => Observation(user, row.ObservationId, now, choice: false)).ToArray();
        var replayed = new RecommendationChoiceHistory(fs, path).Merge(current, new HashSet<Guid> { user }, now);
        var choices = replayed.Where(row => row.Choice is not null).ToArray();
        var heuristic = new Mock<IScoringStrategy>();
        heuristic.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns(.4);
        var learned = new Mock<IScoringStrategy>();
        learned.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) => features.GenreSimilarity < .5 ? .9 : .1);
        var decision = EnsembleAdmissionEvaluator.Evaluate(choices.Take(20).ToArray(), choices.Skip(20).ToArray(), new(heuristic.Object, learned.Object), now, .75, 0);
        Assert.True(decision.Accepted);
        Assert.Equal("AcceptedChoiceRanking", decision.Reason);
        Assert.Equal(20, decision.Evidence.AcceptanceTitles);
        Assert.All(choices, row => Assert.Equal(now.AddDays(-3), row.GeneratedAtUtc));
    }

    [Fact]
    public void NewPublicationCannotCreateEvidenceAndCurrentDislikesOrRemovedUsersWithdrawOldChoices()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(heldout: false);
        var history = new RecommendationChoiceHistory();
        var noChoice = Observation(user, id, now, choice: false);
        Assert.Single(history.Merge([noChoice], new HashSet<Guid> { user }, now));
        history.Merge([Observation(user, id, now, choice: true)], new HashSet<Guid> { user }, now);
        var dislike = Observation(user, id, now, choice: false);
        dislike.Label = .1;
        Assert.Single(history.Merge([dislike], new HashSet<Guid> { user }, now));
        Assert.Single(history.Merge([noChoice], new HashSet<Guid> { user }, now));
        history.Merge([Observation(user, id, now, choice: true)], new HashSet<Guid> { user }, now);
        Assert.Empty(history.Merge([], new HashSet<Guid>(), now));
        Assert.Single(history.Merge([noChoice], new HashSet<Guid> { user }, now));
    }

    [Fact]
    public void RepeatedPublicationDoesNotRenewAnOldChoiceOrKeepIneffectiveEvidenceForever()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(heldout: false);
        var history = new RecommendationChoiceHistory();
        var choice = Observation(user, id, now, choice: true);
        history.Merge([choice], new HashSet<Guid> { user }, now);
        history.Merge([choice], new HashSet<Guid> { user }, now.AddDays(10));
        Assert.Single(history.Merge([Observation(user, id, now, choice: false)], new HashSet<Guid> { user }, now.AddYears(3)));
    }

    [Theory]
    [InlineData(60, 8)]
    [InlineData(180, 6)]
    [InlineData(400, 1)]
    [InlineData(450, 0)]
    public void ReplayedChoiceComparisonsRemainEligibleWithoutInflatingTheirBudgetOrRenewingTheirAge(int age, int expectedCount)
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(false);
        var choice = Observation(user, id, now, true);
        choice.Choice = choice.Choice! with
        {
            Alternatives = Enumerable.Range(0, 20).Select(_ => new RecommendationChoiceAlternative(Identity(false), new CandidateFeatures { GenreSimilarity = .9 })).ToImmutableArray()
        };
        var history = new RecommendationChoiceHistory();
        history.Merge([choice], new HashSet<Guid> { user }, now);
        var reference = now.AddDays(age);
        var contrasts = history.Merge([Observation(user, id, now, false)], new HashSet<Guid> { user }, reference).Where(row => row.IsContrastSample).ToArray();
        Assert.Equal(expectedCount, contrasts.Length);
        Assert.Equal(expectedCount > 0 ? .25 : 0, contrasts.Sum(row => row.SampleWeight), 12);
        Assert.All(contrasts, row => Assert.Equal(choice.GeneratedAtUtc, row.GeneratedAtUtc));
        Assert.Equal(contrasts.Length, TrainingMath.FilterEffectiveExamples(contrasts, reference, LearnedScoringStrategy.MinSampleWeight).Count);
    }

    [Fact]
    public void PersistenceFailuresKeepRealInMemoryChoicesAndCorruptOrWrongSchemaFilesAreOptional()
    {
        var now = DateTime.UtcNow;
        var user = Guid.NewGuid();
        var id = Identity(heldout: false);
        var path = Path.GetFullPath("choice-history-failure.json");
        var fs = new Mock<IFileSystem>();
        var file = new Mock<IFile>();
        fs.SetupGet(value => value.File).Returns(file.Object);
        fs.SetupGet(value => value.Directory).Returns(new Mock<IDirectory>().Object);
        file.Setup(value => value.WriteAllBytes(It.IsAny<string>(), It.IsAny<byte[]>())).Throws(new IOException("read-only"));
        var history = new RecommendationChoiceHistory(fs.Object, path);
        history.Merge([Observation(user, id, now, choice: true)], new HashSet<Guid> { user }, now);
        Assert.False(history.Save());
        Assert.Contains(history.Merge([Observation(user, id, now, choice: false)], new HashSet<Guid> { user }, now), row => row.Choice is not null);

        var memory = new MockFileSystem();
        foreach (var data in new[] { "bad json", "null", "{\"featureCount\":42,\"examples\":[]}" })
        {
            memory.AddFile(path, data);
            var restored = new RecommendationChoiceHistory(memory, path);
            Assert.Single(restored.Merge([Observation(user, id, now, choice: false)], new HashSet<Guid> { user }, now));
        }

        Assert.False(new RecommendationChoiceHistory(memory, "relative.json").Save());
        Assert.False(new RecommendationChoiceHistory().Save());
    }

    private static TrainingExample Observation(Guid user, Guid id, DateTime now, bool choice)
    {
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .2, TagSimilarity = .8 };
        return new TrainingExample
        {
            UserId = user, ObservationId = id, HasStableObservationId = true, IsObserved = true,
            Label = .85, GeneratedAtUtc = now.AddDays(-3), Features = features,
            Choice = choice
                ? new RecommendationChoiceEvidence
                {
                    ChosenObservationId = id, RecommendedAtUtc = now.AddDays(-3), ChosenAtUtc = now.AddDays(-2), AvailableAtUtc = now.AddDays(-10),
                    ChosenFeatures = features.Copy(), Alternatives = [new(Identity(heldout: false), new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .9 })]
                }
                : null
        };
    }

    private static Guid Identity(bool heldout)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (new TrainingExample { Features = new CandidateFeatures(), ObservationId = id, HasStableObservationId = true }.IsPermanentHoldout != heldout);
        return id;
    }
}
