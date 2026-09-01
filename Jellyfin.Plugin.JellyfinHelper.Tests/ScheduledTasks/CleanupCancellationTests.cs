using System.Collections.Generic;
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
        // Cancellation received AFTER a directory enumeration has begun (mid-stream, not before) must
        // abort the walk instead of draining the enumeration first. The root's child enumeration yields
        // one entry, cancels, then offers a second; the per-entry check inside the child-push loop of
        // GetSubdirectoriesIterative must observe the cancellation before consuming that second entry.
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetDirectories("/tmp")).Returns(() => CancelMidEnumeration(cts));
        fs.Setup(f => f.GetDirectories(It.Is<string>(p => p != "/tmp"))).Returns([]);

        var task = new NonReparseTrickplayTask(fs.Object);

        Assert.Throws<OperationCanceledException>(() => task.ProcessLocationForTest("/tmp", false, cts.Token));
    }

    [Fact]
    public void CleanOrphanedSubtitlesTask_CancellationDuringEnumeration_StopsPromptly()
    {
        // Subtitle traversal has its own stack walk (PushChildDirectories). Same guarantee: a
        // cancellation mid-enumeration of the root's children must be observed by the per-entry check
        // inside PushChildDirectories before the second child is pushed.
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.GetDirectories("/tmp")).Returns(() => CancelMidEnumeration(cts));
        fs.Setup(f => f.GetDirectories(It.Is<string>(p => p != "/tmp"))).Returns([]);

        var task = new NonReparseSubtitleTask(fs.Object);

        Assert.Throws<OperationCanceledException>(() => task.ProcessLocationForTest("/tmp", false, cts.Token));
    }

    // Lazily yields a first child directory, cancels the token, then offers a second. A traversal that
    // only checks the token between whole enumerations would still consume the second entry; a
    // per-entry check aborts before it. Used to prove the mid-enumeration cancellation guard.
    private static IEnumerable<FileSystemMetadata> CancelMidEnumeration(CancellationTokenSource cts)
    {
        yield return new FileSystemMetadata { FullName = "/tmp/a", IsDirectory = true };
        cts.Cancel();
        yield return new FileSystemMetadata { FullName = "/tmp/b", IsDirectory = true };
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

    // Subtitle task with the reparse-point guard forced off so a mocked directory tree is actually
    // traversed. Used only to exercise mid-enumeration cancellation without touching a real filesystem.
    private sealed class NonReparseSubtitleTask : CleanOrphanedSubtitlesTask
    {
        public NonReparseSubtitleTask(IFileSystem fileSystem)
            : base(
                new Mock<ILibraryManager>().Object,
                fileSystem,
                Mock.Of<IPluginLogService>(),
                NullLogger<CleanOrphanedSubtitlesTask>.Instance,
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
