using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

/// <summary>
///     Focused tests for RepairLinksTask - every branch of its ExecuteAsync orchestration.
/// </summary>
public sealed class RepairLinksTaskTests
{
    private readonly Mock<ILibraryManager> _libraryManager;
    private readonly Mock<ILinkRepairService> _linkRepair;
    private readonly Mock<ICleanupConfigHelper> _configHelper;

    public RepairLinksTaskTests()
    {
        _libraryManager = TestMockFactory.CreateLibraryManager();
        _linkRepair = new Mock<ILinkRepairService>();
        _configHelper = TestMockFactory.CreateCleanupConfigHelper();
    }

    private RepairLinksTask CreateTask() =>
        new(
            TestMockFactory.CreateLogger<RepairLinksTask>().Object,
            _libraryManager.Object,
            TestMockFactory.CreatePluginLogService(),
            _linkRepair.Object,
            _configHelper.Object);

    private sealed class ProgressReporter : IProgress<double>
    {
        public List<double> Reports { get; } = [];
        public void Report(double value) => Reports.Add(value);
    }

    [Fact]
    public async Task ExecuteAsync_NoLibraryPaths_SkipsRepairAndReports100Percent()
    {
        // BUG GUARD: when no library paths are configured, the task must NOT invoke the repair service (would waste a full library scan on an empty path list) AND must still report progress=100 so the scheduler UI doesn't hang on 0%.
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(new List<string>());

        var task = CreateTask();
        var progress = new ProgressReporter();

        await task.ExecuteAsync(progress, CancellationToken.None);

        _linkRepair.Verify(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains(0.0, progress.Reports);
        Assert.Contains(100.0, progress.Reports);
    }

    [Fact]
    public async Task ExecuteAsync_LibraryPathsPresent_InvokesRepairAndReportsProgress()
    {
        var libraryPaths = new List<string> { "/media/movies", "/media/tv" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);
        _linkRepair.Setup(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new LinkRepairResult());

        var task = CreateTask();
        var progress = new ProgressReporter();

        await task.ExecuteAsync(progress, CancellationToken.None);

        // Verify exact paths were forwarded (verifies wiring, not just "any call"). Default CleanupConfigHelper mock returns DryRun mode, so the second argument is true.
        _linkRepair.Verify(s => s.RepairLinks(libraryPaths, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);

        // Progress must hit at least three milestones: 0, 10 (post-load), 90 (post-repair), 100 (done).
        Assert.Contains(0.0, progress.Reports);
        Assert.Contains(10.0, progress.Reports);
        Assert.Contains(90.0, progress.Reports);
        Assert.Contains(100.0, progress.Reports);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunMode_ForwardsDryRunFlagToService()
    {
        // BUG GUARD: dry-run must propagate to the service so no filesystem mutations occur.
        // We assert on the exact bool flag captured from the invocation.
        var libraryPaths = new List<string> { "/media" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);
        _configHelper.Setup(c => c.IsDryRunLinkRepair()).Returns(true);

        bool? capturedDryRun = null;
        _linkRepair.Setup(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((_, dr, _) => capturedDryRun = dr)
            .Returns(new LinkRepairResult());

        var task = CreateTask();
        await task.ExecuteAsync(new ProgressReporter(), CancellationToken.None);

        Assert.True(capturedDryRun);
    }

    [Fact]
    public async Task ExecuteAsync_ActivateMode_ForwardsFalseDryRun()
    {
        var libraryPaths = new List<string> { "/media" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);
        _configHelper.Setup(c => c.IsDryRunLinkRepair()).Returns(false);

        bool? capturedDryRun = null;
        _linkRepair.Setup(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((_, dr, _) => capturedDryRun = dr)
            .Returns(new LinkRepairResult());

        var task = CreateTask();
        await task.ExecuteAsync(new ProgressReporter(), CancellationToken.None);

        Assert.False(capturedDryRun);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationBeforeRepair_Throws()
    {
        // The task calls ThrowIfCancellationRequested BEFORE handing over to the service.
        var libraryPaths = new List<string> { "/media" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = CreateTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.ExecuteAsync(new ProgressReporter(), cts.Token));

        _linkRepair.Verify(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationTokenIsForwardedToService()
    {
        // The CancellationToken must reach RepairLinks so the service
        // can react to cancellation during its own long-running work.
        var libraryPaths = new List<string> { "/media" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);

        CancellationToken capturedToken = default;
        _linkRepair.Setup(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, bool, CancellationToken>((_, _, ct) => capturedToken = ct)
            .Returns(new LinkRepairResult());

        using var cts = new CancellationTokenSource();
        var task = CreateTask();

        await task.ExecuteAsync(new ProgressReporter(), cts.Token);

        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task ExecuteAsync_ProgressReporter_ReceivesMonotonicallyNonDecreasingValues()
    {
        // Locks the invariant that progress values never go backwards - a subtle but important UX property. If a future refactor reorders progress.Report() calls (e.g.
        var libraryPaths = new List<string> { "/media" };
        _configHelper.Setup(c => c.GetFilteredLibraryLocations(It.IsAny<ILibraryManager>()))
            .Returns(libraryPaths);
        _linkRepair.Setup(s => s.RepairLinks(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new LinkRepairResult());

        var task = CreateTask();
        var progress = new ProgressReporter();

        await task.ExecuteAsync(progress, CancellationToken.None);

        var last = double.MinValue;
        foreach (var report in progress.Reports)
        {
            Assert.True(report >= last, $"Progress decreased: {last} -> {report}");
            last = report;
        }
    }
}