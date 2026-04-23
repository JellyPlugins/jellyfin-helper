using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Activity;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Unit tests for <see cref="UserActivityUpdateTask" />.
/// </summary>
public class UserActivityUpdateTaskTests
{
    private readonly Mock<IUserActivityInsightsService> _insightsMock = new();
    private readonly Mock<IUserActivityCacheService> _cacheMock = new();
    private readonly Mock<IPluginLogService> _pluginLogMock = new();
    private readonly Mock<ILogger> _loggerMock = new();

    private UserActivityUpdateTask CreateSut() =>
        new(_insightsMock.Object, _cacheMock.Object, _pluginLogMock.Object, _loggerMock.Object);

    [Fact]
    public async Task Execute_BuildsReportAndSaves()
    {
        // Arrange
        var result = new UserActivityResult
        {
            TotalItemsWithActivity = 42,
            TotalPlayCount = 100,
            TotalUsersAnalyzed = 5
        };
        _insightsMock.Setup(x => x.BuildActivityReport()).Returns(result);
        var progress = new Mock<IProgress<double>>();

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(progress.Object, CancellationToken.None);

        // Assert
        _insightsMock.Verify(x => x.BuildActivityReport(), Times.Once);
        _cacheMock.Verify(x => x.SaveResult(result), Times.Once);
    }

    [Fact]
    public async Task Execute_ReportsProgressCorrectly()
    {
        // Arrange
        var result = new UserActivityResult();
        _insightsMock.Setup(x => x.BuildActivityReport()).Returns(result);

        var reportedValues = new System.Collections.Generic.List<double>();
        var progress = new Mock<IProgress<double>>();
        progress.Setup(p => p.Report(It.IsAny<double>()))
            .Callback<double>(v => reportedValues.Add(v));

        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(progress.Object, CancellationToken.None);

        // Assert — progress should include 10, 80, 100
        Assert.Contains(10.0, reportedValues);
        Assert.Contains(80.0, reportedValues);
        Assert.Contains(100.0, reportedValues);
    }

    [Fact]
    public async Task Execute_CancellationRequested_Throws()
    {
        // Arrange
        var progress = new Mock<IProgress<double>>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = CreateSut();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync(progress.Object, cts.Token));
    }

    [Fact]
    public async Task Execute_DryRun_DoesNotSaveCache()
    {
        // Arrange
        var result = new UserActivityResult();
        _insightsMock.Setup(x => x.BuildActivityReport()).Returns(result);
        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(new Progress<double>(), CancellationToken.None, Configuration.TaskMode.DryRun);

        // Assert — dry-run still builds the report but does NOT persist to cache
        _insightsMock.Verify(x => x.BuildActivityReport(), Times.Once);
        _cacheMock.Verify(x => x.SaveResult(It.IsAny<UserActivityResult>()), Times.Never);
    }

    [Fact]
    public async Task Execute_Deactivate_DoesNotBuildReportOrSaveCache()
    {
        // Arrange — no mock setup needed: BuildActivityReport should never be called
        var sut = CreateSut();

        // Act
        await sut.ExecuteAsync(new Progress<double>(), CancellationToken.None, Configuration.TaskMode.Deactivate);

        // Assert — Deactivate is a true no-op: no report building, no cache save
        _insightsMock.Verify(x => x.BuildActivityReport(), Times.Never);
        _cacheMock.Verify(x => x.SaveResult(It.IsAny<UserActivityResult>()), Times.Never);
    }
}