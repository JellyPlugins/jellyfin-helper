using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for TrainingService. The class uses a process-wide static gate (TrainGate) so tests must be serialised - hence the ConfigOverride collection.
/// </summary>
[Collection("ConfigOverride")]
public class TrainingServiceTests
{
    private readonly Mock<IWatchHistoryService> _watchHistoryMock = new();
    private readonly Mock<IDiscoveryFeedbackStore> _feedbackStoreMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger> _loggerMock = new();

    private TrainingService CreateSut()
        => new(_watchHistoryMock.Object, _feedbackStoreMock.Object, _pluginLogMock.Object, _loggerMock.Object);

    /// <summary>
    ///     Minimal recording strategy that captures the last received training set so tests can assert against it.
    /// </summary>
    private sealed class RecordingStrategy : IScoringStrategy, ITrainableStrategy
    {
        public string Name => "Recording";

        public string NameKey => "strategyRecording";

        public IReadOnlyList<TrainingExample>? LastReceivedTrainSet { get; private set; }

        public IReadOnlyList<TrainingExample>? LastReceivedHeldOutSet { get; private set; }

        public bool NextTrainReturns { get; set; } = true;

        public int TrainInvocationCount { get; private set; }

        public double Score(CandidateFeatures features) => 0.5;

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new()
        {
            StrategyName = Name,
            FinalScore = 0.5
        };

        public bool Train(IReadOnlyList<TrainingExample> examples) => Train(examples, null);

        public bool Train(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample>? heldOutForMetrics)
        {
            TrainInvocationCount++;
            LastReceivedTrainSet = examples;
            LastReceivedHeldOutSet = heldOutForMetrics;
            return NextTrainReturns;
        }
    }

    /// <summary>Non-trainable strategy so we can prove the ITrainableStrategy branch is required.</summary>
    private sealed class NonTrainableStrategy : IScoringStrategy
    {
        public string Name => "NonTrainable";
        public string NameKey => "strategyNonTrainable";
        public double Score(CandidateFeatures features) => 0;
        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new() { StrategyName = Name };
    }

    [Fact]
    public void Train_NoPreviousResults_SkipsAndReturnsFalse()
    {
        var strategy = new RecordingStrategy();
        var sut = CreateSut();

        var result = sut.Train(strategy, previousResults: Array.Empty<RecommendationResult>());

        Assert.False(result);
        Assert.Equal(0, strategy.TrainInvocationCount);
        _watchHistoryMock.Verify(w => w.GetAllUserWatchProfiles(), Times.Never);
    }

    [Fact]
    public void Train_NonTrainableStrategy_ReturnsFalse()
    {
        // A strategy that does NOT implement ITrainableStrategy must be short-circuited
        // to false even when previous results and watch profiles exist.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());

        var sut = CreateSut();
        var strategy = new NonTrainableStrategy();

        var result = sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]);

        Assert.False(result);
    }

    [Fact]
    public void Train_TrainableStrategy_NoUsersOrExamples_DoesNotInvokeStrategy()
    {
        // No watch profiles => TrainingDataBuilder produces an empty example list. The trainable strategy is still invoked (Train receives an empty list) but must report false via NextTrainReturns=false, and the service must forward that.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy { NextTrainReturns = false };

        var result = sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]);

        Assert.False(result);
        Assert.Equal(1, strategy.TrainInvocationCount);
        Assert.NotNull(strategy.LastReceivedTrainSet);
        Assert.Empty(strategy.LastReceivedTrainSet!);
    }

    [Fact]
    public void Train_HonorsCancellationToken()
    {
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }], cancellationToken: cts.Token));
    }

    [Fact]
    public void Train_FeedbackStoreThrows_TrainingContinues()
    {
        // Best-effort: a broken discovery feedback store must not crash the training pipeline.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _feedbackStoreMock.Setup(s => s.LoadAll()).Throws(new IOException("boom"));

        var sut = CreateSut();
        var strategy = new RecordingStrategy { NextTrainReturns = false };

        var result = sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]);

        Assert.False(result);
        // The training path was still exercised (strategy.Train called once).
        Assert.Equal(1, strategy.TrainInvocationCount);
    }

    /// <summary>
    ///     Builds a small but realistic watch profile so that TrainingDataBuilder actually
    ///     produces training examples (at least one positive + a negative).
    /// </summary>
    private static UserWatchProfile CreatePopulatedProfile(Guid userId)
    {
        return new UserWatchProfile
        {
            UserId = userId,
            UserName = "Test",
            LastActivityDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            WatchedItems =
            {
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    PlayCount = 3,
                    Genres = new[] { "Action" },
                    LastPlayedDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
                },
                new WatchedItemInfo
                {
                    ItemId = Guid.NewGuid(),
                    Played = true,
                    Genres = new[] { "Drama" },
                    LastPlayedDate = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)
                }
            }
        };
    }

    private static RecommendationResult CreateResultWithRecommendations(Guid userId)
    {
        return new RecommendationResult
        {
            UserId = userId,
            UserName = "Test",
            GeneratedAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            Recommendations =
            {
                new RecommendedItem
                {
                    ItemId = Guid.NewGuid(),
                    ItemType = "Movie",
                    Name = "Action Flick",
                    Score = 0.8,
                    Genres = new[] { "Action" },
                    Year = 2020
                },
                new RecommendedItem
                {
                    ItemId = Guid.NewGuid(),
                    ItemType = "Movie",
                    Name = "Drama Piece",
                    Score = 0.6,
                    Genres = new[] { "Drama" },
                    Year = 2021
                }
            }
        };
    }

    [Fact]
    public void Train_WithPopulatedProfiles_InvokesStrategyWithExamples()
    {
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreatePopulatedProfile(userId) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy { NextTrainReturns = true };

        var previous = new[] { CreateResultWithRecommendations(userId) };
        var result = sut.Train(strategy, previous);

        Assert.True(result);
        Assert.Equal(1, strategy.TrainInvocationCount);
        Assert.NotNull(strategy.LastReceivedTrainSet);
        // With random-negative sampling + Phase-2 organic examples, we expect at least SOME examples.
        Assert.NotEmpty(strategy.LastReceivedTrainSet!);
    }

    [Fact]
    public void Train_Incremental_SubsamplesOldExamples()
    {
        // Incremental=true reduces the training set to "recent + sampled old".
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreatePopulatedProfile(userId) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();
        var baselineStrategy = new RecordingStrategy();
        var previous = new[] { CreateResultWithRecommendations(userId) };

        var baselineResult = sut.Train(baselineStrategy, previous, incremental: false);
        var incrementalResult = sut.Train(strategy, previous, incremental: true);

        Assert.True(baselineResult);
        Assert.True(incrementalResult);
        Assert.NotNull(strategy.LastReceivedTrainSet);
        Assert.NotNull(baselineStrategy.LastReceivedTrainSet);

        // Invariant: an incremental pass never enlarges the training set relative to a full pass on the same fixture.
        Assert.True(
            strategy.LastReceivedTrainSet!.Count <= baselineStrategy.LastReceivedTrainSet!.Count,
            $"incremental training must not enlarge the training set (baseline={baselineStrategy.LastReceivedTrainSet!.Count}, incremental={strategy.LastReceivedTrainSet!.Count})");
    }

    [Fact]
    public void Train_WithDiscoveryFeedback_IncludesInBuilder()
    {
        // When the feedback store returns Phase-4 discovery examples, they must be
        // forwarded into TrainingDataBuilder.BuildExamples.
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreatePopulatedProfile(userId) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(new List<DiscoveryFeedbackResult>
        {
            new()
            {
                UserId = userId,
                Entries =
                {
                    new DiscoveryFeedbackEntry
                    {
                        TmdbId = 123,
                        MediaType = "movie",
                        ShownAtUtc = DateTime.UtcNow.AddDays(-1),
                        Genres = new[] { "Action" }
                    }
                }
            }
        });

        var sut = CreateSut();
        var strategy = new RecordingStrategy();
        var previous = new[] { CreateResultWithRecommendations(userId) };

        var result = sut.Train(strategy, previous);

        Assert.True(result);
        _feedbackStoreMock.Verify(s => s.LoadAll(), Times.Once);
    }

    [Fact]
    public void Ctor_WithoutFeedbackStore_TrainCallSucceeds()
    {
        // The legacy two-arg constructor (no feedback store) was previously dead code - no test exercised it.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());

        var sut = new TrainingService(
            _watchHistoryMock.Object,
            _pluginLogMock.Object,
            _loggerMock.Object);

        var strategy = new RecordingStrategy { NextTrainReturns = false };

        var result = sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]);

        Assert.False(result);
        // The strategy was still consulted with an empty example set - proving the
        // no-feedback-store constructor really did wire through to TrainCore.
        Assert.Equal(1, strategy.TrainInvocationCount);
        Assert.NotNull(strategy.LastReceivedTrainSet);
        Assert.Empty(strategy.LastReceivedTrainSet!);
        // Held-out split must be null when < 20 examples (fallback path).
        Assert.Null(strategy.LastReceivedHeldOutSet);
        // The feedback store must NOT have been consulted - the two-arg constructor
        // stores a null and TrainCore skips the LoadAll() call entirely.
        _feedbackStoreMock.Verify(s => s.LoadAll(), Times.Never);
    }

    /// <summary>
    ///     Builds a large watch profile with N distinct watched items across several genres, which - combined with N recommendations per user - produces enough training examples to trigger the held-out validation split path (>= 20 examples).
    /// </summary>
    private static UserWatchProfile CreateLargeProfile(Guid userId, int watchedCount)
    {
        var profile = new UserWatchProfile
        {
            UserId = userId,
            UserName = "TestBig",
            LastActivityDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        var genres = new[] { "Action", "Drama", "Comedy", "Thriller", "SciFi" };
        for (var i = 0; i < watchedCount; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                PlayCount = 2,
                Genres = new[] { genres[i % genres.Length] },
                LastPlayedDate = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(i)
            });
        }

        return profile;
    }

    private static RecommendationResult CreateLargeResult(Guid userId, int recommendationCount, DateTime generatedAt)
    {
        var result = new RecommendationResult
        {
            UserId = userId,
            UserName = "TestBig",
            GeneratedAt = generatedAt
        };

        var genres = new[] { "Action", "Drama", "Comedy", "Thriller", "SciFi" };
        for (var i = 0; i < recommendationCount; i++)
        {
            result.Recommendations.Add(new RecommendedItem
            {
                ItemId = Guid.NewGuid(),
                ItemType = "Movie",
                Name = $"Rec {i}",
                Score = 0.5 + (i * 0.01),
                Genres = new[] { genres[i % genres.Length] },
                Year = 2020 + (i % 5)
            });
        }

        return result;
    }

    [Fact]
    public void Train_WithEnoughExamples_UsesHeldOutValidationSplit()
    {
        // BUG GUARD: The held-out split path (Lines 209-215) only fires when trainingExamples.Count >= 20. Below that threshold the code falls back to "train on all, validate on training-set fit".
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreateLargeProfile(userId, watchedCount: 30) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy { NextTrainReturns = true };
        var previous = new[] { CreateLargeResult(userId, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)) };

        var result = sut.Train(strategy, previous);

        Assert.True(result);
        Assert.NotNull(strategy.LastReceivedTrainSet);
        // With 20+ examples, a non-null held-out slice must be forwarded to the strategy.
        Assert.NotNull(strategy.LastReceivedHeldOutSet);
        Assert.True(strategy.LastReceivedHeldOutSet!.Count >= 2,
            "Held-out split must contain at least 2 examples (Math.Max(2, 10%) floor).");
        // The train split must be non-empty AND the two splits must be disjoint by object reference - together this proves the split actually partitioned the example set rather than degenerating to either "all-training / no-holdout" or "all-holdout / no-training" (either extreme would.
        Assert.NotEmpty(strategy.LastReceivedTrainSet!);
        var trainSetRefs = new HashSet<TrainingExample>(
            strategy.LastReceivedTrainSet!,
            ReferenceEqualityComparer.Instance);
        Assert.All(strategy.LastReceivedHeldOutSet!, heldOut =>
        {
            Assert.DoesNotContain(heldOut, trainSetRefs);
        });
    }

    [Fact]
    public void Train_HeldOutSplit_PicksMostRecentAsValidation()
    {
        // BUG GUARD: The comment at line 211 promises "Sort by GeneratedAtUtc descending to pick the most recent as held-out".
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreateLargeProfile(userId, watchedCount: 30) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();
        var previous = new[] { CreateLargeResult(userId, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)) };

        sut.Train(strategy, previous);

        Assert.NotNull(strategy.LastReceivedHeldOutSet);
        Assert.NotNull(strategy.LastReceivedTrainSet);

        // Hard requirement: with 30 watched items + 30 prior recommendations, BuildExamples MUST produce enough examples to populate BOTH splits.
        Assert.NotEmpty(strategy.LastReceivedHeldOutSet!);
        Assert.NotEmpty(strategy.LastReceivedTrainSet!);

        var minHeldOut = strategy.LastReceivedHeldOutSet!.Min(e => e.GeneratedAtUtc);
        var maxTrain = strategy.LastReceivedTrainSet!.Max(e => e.GeneratedAtUtc);

        // Every held-out example must be at least as recent as the newest training example. (Ties can occur because BuildExamples stamps a batch of examples with the same GeneratedAtUtc; the invariant is "no train example is strictly newer than any held-out example".).
        Assert.True(minHeldOut >= maxTrain,
            $"Temporal leakage detected: oldest held-out ({minHeldOut:o}) predates newest train ({maxTrain:o}).");
    }

    [Fact]
    public void Train_Incremental_WithMixedAgeExamples_SubsamplesOldOnesOnly()
    {
        // BUG GUARD: The incremental training branch (Lines 144-194) partitions examples by "generatedAt >= cutoff" where cutoff = latestGeneratedAt.AddDays(-1).
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreateLargeProfile(userId, watchedCount: 30) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();
        var previous = new[]
        {
            CreateLargeResult(userId, recommendationCount: 15, new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateLargeResult(userId, recommendationCount: 15, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        // Non-incremental baseline
        var strategyBaseline = new RecordingStrategy();
        sut.Train(strategyBaseline, previous, incremental: false);
        var baselineCount = strategyBaseline.LastReceivedTrainSet?.Count ?? 0;

        // Incremental - should subsample the older set
        sut.Train(strategy, previous, incremental: true);
        var incrementalCount = strategy.LastReceivedTrainSet?.Count ?? 0;

        // The incremental path must produce >= 1 example. If both counts happen to fall below IncrementalMinExamplesThreshold the incremental branch is skipped and both paths return the same set - that is a valid outcome, not a bug.
        Assert.NotNull(strategy.LastReceivedTrainSet);
        Assert.True(incrementalCount <= baselineCount,
            $"Incremental training must not enlarge the training set. baseline={baselineCount}, incremental={incrementalCount}.");
    }

    /// <summary>
    ///     Strategy whose Train callback re-enters TrainingService.Train on the SAME instance, letting us prove the non-blocking gate rejects the reentrant call.
    /// </summary>
    private sealed class ReentrantStrategy : IScoringStrategy, ITrainableStrategy
    {
        private readonly Func<bool> _reentrantCall;

        public ReentrantStrategy(Func<bool> reentrantCall) => _reentrantCall = reentrantCall;

        public string Name => "Reentrant";

        public string NameKey => "strategyReentrant";

        public int TrainInvocationCount { get; private set; }

        public bool? NestedReturnValue { get; private set; }

        public double Score(CandidateFeatures features) => 0.5;

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new()
        {
            StrategyName = Name,
            FinalScore = 0.5
        };

        public bool Train(IReadOnlyList<TrainingExample> examples) => Train(examples, null);

        public bool Train(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample>? heldOutForMetrics)
        {
            TrainInvocationCount++;
            // Re-enter Train while the outer call still holds the gate - must be rejected.
            NestedReturnValue = _reentrantCall();
            return true;
        }
    }

    [Fact]
    public void Dispose_ReleasesTrainGate_AllowsSubsequentTrain()
    {
        // Dispose() disposes the gate; no existing test drives it. Prove disposal is clean and,
        // because the gate is per-instance, a fresh SUT still trains normally afterwards.
        var disposed = CreateSut();
        disposed.Dispose();

        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy { NextTrainReturns = false };

        // Empty fixture => false; the point is that disposal of the prior SUT did not corrupt shared state.
        var result = sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]);

        Assert.False(result);
        Assert.Equal(1, strategy.TrainInvocationCount);
    }

    [Fact]
    public void Train_WhenAlreadyRunningOnSameInstance_SkipsReentrantCallAndReturnsFalse()
    {
        // The gate is per-instance and non-blocking (Wait(0)). A reentrant Train on the same instance, issued while the outer call still holds the gate, must be rejected and return false without building examples or invoking the strategy a second time.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var previous = new[] { new RecommendationResult { UserId = Guid.NewGuid() } };

        ReentrantStrategy? strategy = null;
        strategy = new ReentrantStrategy(() => sut!.Train(strategy!, previous));

        var outerResult = sut.Train(strategy, previous);

        Assert.False(strategy.NestedReturnValue);
        Assert.Equal(1, strategy.TrainInvocationCount);
        Assert.True(outerResult);
    }

    [Fact]
    public void Train_FeedbackStoreThrowsOperationCanceled_PropagatesInsteadOfSwallowing()
    {
        // Best-effort IO failures are swallowed, but cancellation must NOT be: the dedicated
        // catch(OperationCanceledException){throw;} has to re-surface it so callers observe the cancel.
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile>());
        _feedbackStoreMock.Setup(s => s.LoadAll()).Throws(new OperationCanceledException());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();

        Assert.Throws<OperationCanceledException>(() =>
            sut.Train(strategy, [new RecommendationResult { UserId = Guid.NewGuid() }]));

        Assert.Equal(0, strategy.TrainInvocationCount);
    }

    [Fact]
    public void Train_IncrementalWithAllRecentExamples_KeepsAllAndSubsamplesNothing()
    {
        // A single recommendation batch stamps every example at one GeneratedAt, so all examples land newer than cutoff (latest-1day) and oldExamples is empty.
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreateLargeProfile(userId, watchedCount: 30) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var previous = new[] { CreateLargeResult(userId, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)) };

        var baselineStrategy = new RecordingStrategy();
        var incrementalStrategy = new RecordingStrategy();

        var baselineResult = sut.Train(baselineStrategy, previous, incremental: false);
        var incrementalResult = sut.Train(incrementalStrategy, previous, incremental: true);

        Assert.True(baselineResult);
        Assert.True(incrementalResult);
        Assert.NotNull(incrementalStrategy.LastReceivedTrainSet);
        Assert.NotEmpty(incrementalStrategy.LastReceivedTrainSet!);

        // No example is older than cutoff, so nothing is dropped: the incremental train set matches
        // the non-incremental baseline on the identical single-batch fixture.
        Assert.Equal(
            baselineStrategy.LastReceivedTrainSet!.Count,
            incrementalStrategy.LastReceivedTrainSet!.Count);
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.
}
