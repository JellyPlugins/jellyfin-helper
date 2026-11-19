using System.Collections.Immutable;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Actual choice evidence survives publication and restarts without becoming fresh or leaking held-out outcomes.</summary>
public sealed class RecommendationChoiceHistoryTests
{
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
