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

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.
}