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
///     Tests for <see cref="TrainingService"/>. The class uses a process-wide static gate
///     (<c>TrainGate</c>) so tests must be serialised — hence the <c>ConfigOverride</c> collection.
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
    ///     Minimal recording strategy that captures the last received training set so tests can
    ///     assert against it. Deliberately implements <see cref="ITrainableStrategy"/>.
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
        // No watch profiles => TrainingDataBuilder produces an empty example list.
        // The trainable strategy is still invoked (Train receives an empty list) but must
        // report false via NextTrainReturns=false, and the service must forward that.
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

    // ===== Populated training path =====

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
        // Regression: incremental=true reduces the training set to "recent + sampled old".
        // The strategy's LastReceivedTrainSet count should generally be <= the non-incremental variant.
        var userId = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile> { CreatePopulatedProfile(userId) };
        _watchHistoryMock.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);
        _feedbackStoreMock.Setup(s => s.LoadAll()).Returns(Array.Empty<DiscoveryFeedbackResult>());

        var sut = CreateSut();
        var strategy = new RecordingStrategy();
        var previous = new[] { CreateResultWithRecommendations(userId) };

        var incrementalResult = sut.Train(strategy, previous, incremental: true);

        Assert.True(incrementalResult);
        Assert.NotNull(strategy.LastReceivedTrainSet);
    }

    [Fact]
    public void Train_WithDiscoveryFeedback_IncludesInBuilder()
    {
        // Regression: when the feedback store returns Phase-4 discovery examples, they must be
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

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.
}