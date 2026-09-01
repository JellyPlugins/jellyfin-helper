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

    [Fact]
    public void CleanTrickplayTask_CancellationDuringEnumeration_StopsPromptly()
    {
        // Cancellation received AFTER directory traversal has started (not before) must still abort the
        // walk instead of materialising the whole tree first. The token is cancelled when enumeration
        // descends into a child directory; the in-loop check in GetSubdirectoriesIterative must observe it.
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        var callCount = 0;
        fs.Setup(f => f.GetDirectories(It.IsAny<string>())).Returns(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                // Root enumeration seeds the stack with two children.
                return new[]
                {
                    new FileSystemMetadata { FullName = "/tmp/a", IsDirectory = true },
                    new FileSystemMetadata { FullName = "/tmp/b", IsDirectory = true }
                };
            }

            // Traversal has begun: cancel now and prove the loop aborts rather than descending further.
            cts.Cancel();
            return [];
        });

        var task = new NonReparseTrickplayTask(fs.Object);

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

    // Trickplay task with the reparse-point guard forced off so a mocked directory tree is actually
    // traversed. Used only to exercise mid-enumeration cancellation without touching a real filesystem.
    private sealed class NonReparseTrickplayTask : CleanTrickplayTask
    {
        public NonReparseTrickplayTask(IFileSystem fileSystem)
            : base(
                new Mock<ILibraryManager>().Object,
                fileSystem,
                Mock.Of<IPluginLogService>(),
                NullLogger<CleanTrickplayTask>.Instance,
                Mock.Of<ICleanupConfigHelper>(c => c.GetConfig() == new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration() && c.GetTrashPath(It.IsAny<string>()) == "/trash"),
                Mock.Of<ICleanupTrackingService>(),
                Mock.Of<ITrashService>())
        {
        }

        protected override bool IsReparsePoint(string path) => false;
    }
}

internal static class CleanupTaskTestExtensions
{
    public static (int, long) ProcessLocationForTest(this BaseLibraryCleanupTask task, string path, bool dryRun, CancellationToken token)
    {
        var method = task.GetType().GetMethod("ProcessLocation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        try
        {
            return ((int, long))method!.Invoke(task, [path, dryRun, token])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Reflection wraps the real exception; rethrow the inner one preserving its stack so
            // callers observe the production exception type (e.g. OperationCanceledException).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
