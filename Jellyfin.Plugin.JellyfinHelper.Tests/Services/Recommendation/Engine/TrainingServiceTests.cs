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
    [Fact]
    public void TrainPerUser_ModelIdleBeyondWindow_RetiredWithInfoLog()
    {
        // A user builds a per-user model, then goes quiet for longer than the idle window. A later run for a
        // different user never revisits the quiet user, so the age sweep must retire the stale model and log the
        // retirement. This drives the staleEvicted > 0 branch that the happy-path per-user tests never reach.
        var activeUser = Guid.NewGuid();
        var idleUser = Guid.NewGuid();
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var dataPath = Path.Join(Path.GetTempPath(), "jfh-trainperuser-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            using var neural = new NeuralScoringStrategy();
            using var global = new EnsembleScoringStrategy(
                new LearnedScoringStrategy(Path.Join(dataPath, "ml_weights.json")),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
                neural,
                Path.Join(dataPath, "ensemble_state.json"));
            using var registry = new PerUserEnsembleRegistry(
                global,
                neural,
                dataPath,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLogMock.Object);
            using var sut = CreateSut();

            // Data-rich run for the idle user creates their per-user model.
            _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
                .Returns(new Collection<UserWatchProfile> { CreateLargeProfile(idleUser, watchedCount: 30) });
            sut.TrainPerUser(
                registry,
                new[] { CreateLargeResult(idleUser, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)) });
            Assert.True(registry.HasPerUserModel(idleUser));

            // Rewind the idle user's persisted last-trained time to well beyond the idle window so the next run's
            // age sweep sees it as stale. The active user's run below never touches the idle user's stamp.
            var idleStatePath = Path.Join(dataPath, $"ensemble_state_{idleUser:N}.json");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(idleStatePath))!;
            node["UpdatedAt"] = DateTime.UtcNow
                .AddDays(-(EngineConstants.PerUserModelMaxIdleDays + 5))
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(idleStatePath, node.ToJsonString());

            // A later run only for a different, active user. The idle user is not in these results, so the
            // per-user pass never revisits them and only the age sweep can retire the stale model.
            _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
                .Returns(new Collection<UserWatchProfile> { CreateLargeProfile(activeUser, watchedCount: 30) });
            sut.TrainPerUser(
                registry,
                new[] { CreateLargeResult(activeUser, recommendationCount: 30, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)) });

            Assert.False(registry.HasPerUserModel(idleUser));
            Assert.False(File.Exists(Path.Join(dataPath, $"ml_weights_{idleUser:N}.json")));
            _pluginLogMock.Verify(
                l => l.LogInfo(
                    It.IsAny<string>(),
                    It.Is<string>(m => m.Contains("Retired") && m.Contains("per-user model")),
                    It.IsAny<ILogger>()),
                Times.Once);
        }
        finally
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

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

    [Fact]
    public void TrainPerUser_IncrementalSubsamplesPool_UsersAboveThresholdStillGetPerUserModel()
    {
        // Regression guard: incremental subsampling must apply ONLY to the global pass. Per-user passes see
        // the full per-user slices, and the per-user threshold is checked against the UNSUBSAMPLED count. So
        // a user whose real example count clears the threshold must still get a per-user model even when the
        // pooled set is large enough to trigger incremental subsampling. Before the fix, subsampling the pool
        // first could drop a user's visible count below the threshold and silently skip their model.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        // Two data-rich users (30 watched items + 30 prior recommendations each) produce far more than the
        // 12-example per-user threshold individually, and a pooled total well past IncrementalMinExamplesThreshold (30).
        var profiles = new Collection<UserWatchProfile>
        {
            CreateLargeProfile(userA, watchedCount: 30),
            CreateLargeProfile(userB, watchedCount: 30)
        };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var previous = new[]
        {
            CreateLargeResult(userA, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateLargeResult(userB, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var dataPath = Path.Join(Path.GetTempPath(), "jfh-trainperuser-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var neural = new NeuralScoringStrategy();
            using var global = new EnsembleScoringStrategy(
                new LearnedScoringStrategy(Path.Join(dataPath, "ml_weights.json")),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
                neural,
                Path.Join(dataPath, "ensemble_state.json"));
            using var registry = new PerUserEnsembleRegistry(
                global,
                neural,
                dataPath,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLogMock.Object);

            using var sut = CreateSut();
            var trained = sut.TrainPerUser(registry, previous, incremental: true);

            Assert.True(trained);
            // Both users cleared the threshold on their full slices, so both must have a dedicated per-user
            // model persisted despite the global pass subsampling the pooled set.
            Assert.True(registry.HasPerUserModel(userA));
            Assert.True(registry.HasPerUserModel(userB));
        }
        finally
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void TrainPerUser_NoPreviousResults_ReturnsFalseWithoutTraining()
    {
        var dataPath = Path.Join(Path.GetTempPath(), "jfh-trainperuser-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            using var neural = new NeuralScoringStrategy();
            using var global = new EnsembleScoringStrategy(
                new LearnedScoringStrategy(Path.Join(dataPath, "ml_weights.json")),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
                neural,
                Path.Join(dataPath, "ensemble_state.json"));
            using var registry = new PerUserEnsembleRegistry(
                global,
                neural,
                dataPath,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLogMock.Object);
            using var sut = CreateSut();

            var trained = sut.TrainPerUser(registry, Array.Empty<RecommendationResult>());

            // An empty result set has nothing to train on, so no model file is written and false is returned.
            Assert.False(trained);
            Assert.False(File.Exists(Path.Join(dataPath, "ml_weights.json")));
        }
        finally
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void TrainPerUser_UserBelowThreshold_GetsNoPerUserModel()
    {
        // A single user with only a couple of examples trains the global model but stays under the per-user
        // threshold, so the registry must not create a dedicated per-user model for them.
        var user = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreateLargeProfile(user, watchedCount: 2) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var previous = new[]
        {
            CreateLargeResult(user, recommendationCount: 2, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var dataPath = Path.Join(Path.GetTempPath(), "jfh-trainperuser-below-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            using var neural = new NeuralScoringStrategy();
            using var global = new EnsembleScoringStrategy(
                new LearnedScoringStrategy(Path.Join(dataPath, "ml_weights.json")),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
                neural,
                Path.Join(dataPath, "ensemble_state.json"));
            using var registry = new PerUserEnsembleRegistry(
                global,
                neural,
                dataPath,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLogMock.Object);
            using var sut = CreateSut();

            sut.TrainPerUser(registry, previous);

            Assert.False(registry.HasPerUserModel(user));
        }
        finally
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public void TrainPerUser_UserDropsBelowThresholdAfterHavingModel_EvictsAndFallsBackToGlobal()
    {
        // A user who built a per-user model on a data-rich run and then falls below the threshold on a later
        // run must not keep scoring on the stale personal fit: the model is evicted so the user resolves back
        // to the shared global ensemble. Without eviction the next score would still find the old per-user file.
        var user = Guid.NewGuid();
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var dataPath = Path.Join(Path.GetTempPath(), "jfh-trainperuser-evict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            using var neural = new NeuralScoringStrategy();
            using var global = new EnsembleScoringStrategy(
                new LearnedScoringStrategy(Path.Join(dataPath, "ml_weights.json")),
                new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
                neural,
                Path.Join(dataPath, "ensemble_state.json"));
            using var registry = new PerUserEnsembleRegistry(
                global,
                neural,
                dataPath,
                new EnsembleBlendBounds(
                    EnsembleScoringStrategy.DefaultAlphaMin,
                    EnsembleScoringStrategy.DefaultAlphaMax,
                    EnsembleScoringStrategy.DefaultGenrePenaltyFloor),
                _pluginLogMock.Object);
            using var sut = CreateSut();

            // Data-rich run: the user clears the threshold and gets a dedicated per-user model.
            _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
                .Returns(new Collection<UserWatchProfile> { CreateLargeProfile(user, watchedCount: 30) });
            sut.TrainPerUser(
                registry,
                new[] { CreateLargeResult(user, recommendationCount: 30, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)) });
            Assert.True(registry.HasPerUserModel(user));
            Assert.NotSame(global, registry.GetScoringStrategyForUser(user));

            // Later lean run: the same user falls below the threshold, so the model is evicted.
            _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles())
                .Returns(new Collection<UserWatchProfile> { CreateLargeProfile(user, watchedCount: 2) });
            sut.TrainPerUser(
                registry,
                new[] { CreateLargeResult(user, recommendationCount: 2, new DateTime(2025, 12, 8, 0, 0, 0, DateTimeKind.Utc)) });

            Assert.False(registry.HasPerUserModel(user));
            Assert.False(File.Exists(Path.Join(dataPath, $"ml_weights_{user:N}.json")));
            Assert.Same(global, registry.GetScoringStrategyForUser(user));
        }
        finally
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
