using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.ScheduledTasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.ScheduledTasks;

public sealed class CleanupCancellationTests
{
    [Fact]
    public void CleanEmptyMediaFoldersTask_Cancellation_Throws()
    {
        var task = CreateEmptyFolderTask();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => task.ProcessLocationForTest("/tmp", false, cts.Token));
    }

    [Fact]
    public void CleanTrickplayTask_Cancellation_Throws()
    {
        var task = CreateTrickplayTask();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => task.ProcessLocationForTest("/tmp", false, cts.Token));
    }

    [Fact]
    public void CleanOrphanedSubtitlesTask_Cancellation_Throws()
    {
        var task = CreateSubtitleTask();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => task.ProcessLocationForTest("/tmp", false, cts.Token));
    }

    private static CleanEmptyMediaFoldersTask CreateEmptyFolderTask()
    {
        var lib = new Mock<ILibraryManager>();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetDirectories(It.IsAny<string>())).Returns([]);
        return new CleanEmptyMediaFoldersTask(lib.Object, fs.Object, Mock.Of<IPluginLogService>(), NullLogger<CleanEmptyMediaFoldersTask>.Instance, Mock.Of<ICleanupConfigHelper>(c => c.GetConfig() == new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration() && c.GetTrashPath(It.IsAny<string>()) == "/trash"), Mock.Of<ICleanupTrackingService>(), Mock.Of<ITrashService>());
    }

    private static CleanTrickplayTask CreateTrickplayTask()
    {
        var lib = new Mock<ILibraryManager>();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetDirectories(It.IsAny<string>())).Returns([]);
        return new CleanTrickplayTask(lib.Object, fs.Object, Mock.Of<IPluginLogService>(), NullLogger<CleanTrickplayTask>.Instance, Mock.Of<ICleanupConfigHelper>(c => c.GetConfig() == new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration() && c.GetTrashPath(It.IsAny<string>()) == "/trash"), Mock.Of<ICleanupTrackingService>(), Mock.Of<ITrashService>());
    }

    private static CleanOrphanedSubtitlesTask CreateSubtitleTask()
    {
        var lib = new Mock<ILibraryManager>();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetDirectories(It.IsAny<string>())).Returns([]);
        return new CleanOrphanedSubtitlesTask(lib.Object, fs.Object, Mock.Of<IPluginLogService>(), NullLogger<CleanOrphanedSubtitlesTask>.Instance, Mock.Of<ICleanupConfigHelper>(c => c.GetConfig() == new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration() && c.GetTrashPath(It.IsAny<string>()) == "/trash"), Mock.Of<ICleanupTrackingService>(), Mock.Of<ITrashService>());
    }
}

internal static class CleanupTaskTestExtensions
{
    public static (int, long) ProcessLocationForTest(this BaseLibraryCleanupTask task, string path, bool dryRun, CancellationToken token)
    {
        var method = task.GetType().GetMethod("ProcessLocation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return ((int, long))method!.Invoke(task, [path, dryRun, token])!;
    }
}
