using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Error-handling behavior for RecommendationsTask: cancellation must propagate out of every phase, while non-fatal failures in the best-effort playlist phases are logged and swallowed so the task still completes.
/// </summary>
public class RecommendationsTaskErrorHandlingTests
{
    private readonly Mock<IRecommendationEngine> _recsEngineMock = new();
    private readonly Mock<IRecommendationCacheService> _recsCacheMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger> _loggerMock = new();

    private RecommendationsTask CreateSut() =>
        new(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, _loggerMock.Object);

    [Fact]
    public async Task Execute_TrainingCancelled_PropagatesOperationCanceled()
    {
        // Cancellation is a control-flow signal, not a training failure: it must surface
        // rather than be swallowed by the non-fatal catch that ordinary training errors hit.
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate };
        var progress = new Mock<IProgress<double>>();

        var cached = new List<RecommendationResult>
        {
            new() { UserId = Guid.NewGuid(), Recommendations = new Collection<RecommendedItem> { new() { ItemId = Guid.NewGuid(), Score = 0.7 } } }
        };
        _recsCacheMock.Setup(x => x.LoadResults()).Returns(cached);
        _recsEngineMock.Setup(x => x.TrainStrategy(It.IsAny<IReadOnlyList<RecommendationResult>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException());

        var sut = CreateSut();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync(config, progress.Object, CancellationToken.None));

        // Generation lives after the training block; the propagating cancellation skips it entirely.
        _recsEngineMock.Verify(x => x.GetAllRecommendations(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_PlaylistSyncCancelled_PropagatesOperationCanceled()
    {
        // Save precedes sync, so results persist; but a cancelled sync must not be masked as a swallowed failure.
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate, SyncRecommendationsToPlaylist = true };
        var progress = new Mock<IProgress<double>>();
        var playlistMock = new Mock<IRecommendationPlaylistService>();

        _recsCacheMock.Setup(x => x.LoadResults()).Returns(new List<RecommendationResult>());
        var results = new List<RecommendationResult>();
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(results);
        playlistMock.Setup(x => x.UpdatePlaylistsForAllUsersAsync(results, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new RecommendationsTask(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, playlistMock.Object, _loggerMock.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync(config, progress.Object, CancellationToken.None));

        _recsCacheMock.Verify(x => x.SaveResults(results), Times.Once);
    }

    [Fact]
    public async Task Execute_PlaylistSyncThrowsNonFatal_LogsWarningAndDoesNotThrow()
    {
        // Playlist sync is best-effort: a non-fatal failure is logged, and the already-saved recommendations stand.
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Activate, SyncRecommendationsToPlaylist = true };
        var progress = new Mock<IProgress<double>>();
        var playlistMock = new Mock<IRecommendationPlaylistService>();

        _recsCacheMock.Setup(x => x.LoadResults()).Returns(new List<RecommendationResult>());
        var results = new List<RecommendationResult>();
        _recsEngineMock.Setup(x => x.GetAllRecommendations(20, It.IsAny<CancellationToken>())).Returns(results);
        playlistMock.Setup(x => x.UpdatePlaylistsForAllUsersAsync(results, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sync boom"));

        var sut = new RecommendationsTask(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, playlistMock.Object, _loggerMock.Object);

        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        _recsCacheMock.Verify(x => x.SaveResults(results), Times.Once);
        _pluginLogMock.Verify(
            x => x.LogWarning("Recommendations", It.Is<string>(s => s.Contains("Playlist sync failed", StringComparison.OrdinalIgnoreCase)), It.IsAny<Exception>(), It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public async Task Cleanup_RemovalCancelled_PropagatesOperationCanceled()
    {
        // Deactivate cleanup is best-effort for ordinary errors, but a cancellation still has to escape.
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate };
        var progress = new Mock<IProgress<double>>();
        var playlistMock = new Mock<IRecommendationPlaylistService>();
        playlistMock.Setup(x => x.RemoveAllRecommendationPlaylistsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new RecommendationsTask(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, playlistMock.Object, _loggerMock.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync(config, progress.Object, CancellationToken.None));
    }

    [Fact]
    public async Task Cleanup_RemovalThrowsNonFatal_LogsWarningAndDoesNotThrow()
    {
        // Per the XML-doc contract, cleanup errors are logged but do not fail the task.
        var config = new PluginConfiguration { RecommendationsTaskMode = TaskMode.Deactivate };
        var progress = new Mock<IProgress<double>>();
        var playlistMock = new Mock<IRecommendationPlaylistService>();
        playlistMock.Setup(x => x.RemoveAllRecommendationPlaylistsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cleanup boom"));

        var sut = new RecommendationsTask(_recsEngineMock.Object, _recsCacheMock.Object, _pluginLogMock.Object, playlistMock.Object, _loggerMock.Object);

        await sut.ExecuteAsync(config, progress.Object, CancellationToken.None);

        _pluginLogMock.Verify(
            x => x.LogWarning("Recommendations", It.Is<string>(s => s.Contains("clean up old recommendation playlists", StringComparison.OrdinalIgnoreCase)), It.IsAny<Exception>(), It.IsAny<ILogger>()),
            Times.Once);
    }
}
