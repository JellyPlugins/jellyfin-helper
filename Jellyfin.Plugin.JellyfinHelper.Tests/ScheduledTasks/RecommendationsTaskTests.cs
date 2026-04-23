using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Unit tests for <see cref="RecommendationsTask" />.
/// </summary>
public class RecommendationsTaskTests
{
    private readonly Mock<IRecommendationEngine> _recsEngineMock = new();
    private readonly Mock<IRecommendationCacheService> _recsCacheMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger> _loggerMock = new();

    private RecommendationsTask CreateSut() =>
        new(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, _loggerMock.Object);

    [Fact]
    public async Task Execute_ActiveMode_TrainsAndSavesResults()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate };
        var progress = new Mock<IProgress<double>>();
        var cached = new List<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), Recommendations = new Collection<RecommendedItem> { new() { ItemId = Guid.NewGuid(), Score = 0.8 } } }
        };
        _recsCacheMock.Setup(x => x.LoadResults()).Returns(cached);
        _recsEngineMock.Setup(x => x.TrainStrategy(cached, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(true);

        var results = new List<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), Recommendations = new Collection<RecommendedItem> { new() { ItemId = Guid.NewGuid(), Score = 0.9 } } }
        };
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(results);

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        // Assert
        _recsEngineMock.Verify(x => x.TrainStrategy(cached, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _recsEngineMock.Verify(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>()), Times.Once);
        _recsCacheMock.Verify(x => x.SaveResults(results), Times.Once);
    }

    [Fact]
    public async Task Execute_DeactivateMode_DoesNotTrainOrGenerateOrSave()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate };
        var progress = new Mock<IProgress<double>>();

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        // Assert — Deactivate is a true no-op: no training, no generation, no cache save
        _recsEngineMock.Verify(
            x => x.TrainStrategy(It.IsAny<IReadOnlyList<RecommendationResult>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recsEngineMock.Verify(
            x => x.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _recsCacheMock.Verify(
            x => x.SaveResults(It.IsAny<IReadOnlyList<RecommendationResult>>()), Times.Never);
    }

    [Fact]
    public async Task Execute_DryRunMode_DoesNotSaveResults()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.DryRun };
        var progress = new Mock<IProgress<double>>();

        _recsCacheMock.Setup(x => x.LoadResults()).Returns(new List<RecommendationResult>());
        var results = new List<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), Recommendations = new Collection<RecommendedItem> { new() { ItemId = Guid.NewGuid(), Score = 0.5 } } }
        };
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(results);

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        // Assert
        _recsCacheMock.Verify(x => x.SaveResults(It.IsAny<IReadOnlyList<RecommendationResult>>()), Times.Never);
    }

    [Fact]
    public async Task Execute_TrainingFails_ContinuesWithGeneration()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate };
        var progress = new Mock<IProgress<double>>();

        var cached = new List<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), Recommendations = new Collection<RecommendedItem> { new() { ItemId = Guid.NewGuid(), Score = 0.7 } } }
        };
        _recsCacheMock.Setup(x => x.LoadResults()).Returns(cached);
        _recsEngineMock.Setup(x => x.TrainStrategy(It.IsAny<IReadOnlyList<RecommendationResult>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("Training failed"));

        var results = new List<RecommendationResult>();
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(results);

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        // Assert — generation still happens
        _recsEngineMock.Verify(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_NoCachedResults_SkipsTraining()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate };
        var progress = new Mock<IProgress<double>>();

        _recsCacheMock.Setup(x => x.LoadResults()).Returns(new List<RecommendationResult>());
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>()))
            .Returns(new List<RecommendationResult>());

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        // Assert
        _recsEngineMock.Verify(x => x.TrainStrategy(It.IsAny<IReadOnlyList<RecommendationResult>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_CancellationRequested_Throws()
    {
        // Arrange
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate };
        var progress = new Mock<IProgress<double>>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = CreateSut();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync(config, progress.Object, cts.Token));
    }
}