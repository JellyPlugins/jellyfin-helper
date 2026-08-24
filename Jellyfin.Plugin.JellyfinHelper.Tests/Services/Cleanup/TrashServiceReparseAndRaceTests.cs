using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Covers the defensive reparse-point (symlink/junction) guards and the TOCTOU move-retry race
///     in <see cref="TrashService"/> that cannot be reproduced against a real filesystem: creating
///     symlinks requires elevated privileges (unavailable in CI) and a genuine move race is
///     non-deterministic. The tests override the filesystem seams to drive those branches
///     deterministically, then separately assert the default seam implementations behave correctly
///     against a real temp directory so the production wrappers are covered too.
/// </summary>
public sealed class TrashServiceReparseAndRaceTests : IDisposable
{
    private readonly Mock<IPluginLogService> _mockPluginLog = new();
    private readonly ILogger _logger = TestMockFactory.CreateLogger().Object;
    private readonly string _testRoot = Path.Join(Path.GetTempPath(), $"TrashReparse-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient locks must not fail the suite.
            _ = ex;
        }
    }

    // ── Reparse-point guards ──────────────────────────────────────────────────

    [Fact]
    public void PurgeExpiredTrash_TrashRootIsReparsePoint_SkipsPurgeAndLogsError()
    {
        Directory.CreateDirectory(_testRoot);
        // An expired entry that WOULD be purged if the root guard did not short-circuit first.
        var expiredChild = Path.Join(_testRoot, "20200101-000000_Old");
        Directory.CreateDirectory(expiredChild);

        var service = new ReparseStubTrashService(_mockPluginLog.Object);
        service.ReparsePaths.Add(_testRoot);

        var (bytesFreed, itemsPurged) = service.PurgeExpiredTrash(_testRoot, 30, _logger);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
        // The expired child survives because the root guard aborted the purge.
        Assert.True(Directory.Exists(expiredChild));
        _mockPluginLog.Verify(
            l => l.LogError(
                "Trash",
                It.Is<string>(m => m.Contains("reparse point") && m.Contains("skipping purge")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredEntryIsReparsePoint_RemovesLinkNodeOnlyAndFreesNoBytes()
    {
        Directory.CreateDirectory(_testRoot);
        var expiredLink = Path.Join(_testRoot, "20200101-000000_Linked");
        Directory.CreateDirectory(expiredLink);

        // Only the child is treated as a reparse point - the root is a normal directory.
        var service = new ReparseStubTrashService(_mockPluginLog.Object);
        service.ReparsePaths.Add(expiredLink);

        var (bytesFreed, itemsPurged) = service.PurgeExpiredTrash(_testRoot, 1, _logger);

        Assert.Equal(0, bytesFreed); // link-node removal frees no counted bytes
        Assert.Equal(1, itemsPurged);
        Assert.Single(service.DeletedLinkNodes);
        _mockPluginLog.Verify(
            l => l.LogInfo(
                "Trash",
                It.Is<string>(m => m.Contains("reparse point")),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void PurgeExpiredTrash_ReparseNodeChangesTypeBeforePurge_SkipsAndWarns()
    {
        // Concurrent-replacement race: the entry was a reparse point when the purge decided to
        // remove its link node, but between that check and the delete it changed type. The guard
        // in DeleteReparsePointLinkNode throws InvalidOperationException; the purge must catch it,
        // log a warning, leave the entry untouched, and count nothing as purged.
        Directory.CreateDirectory(_testRoot);
        var expiredLink = Path.Join(_testRoot, "20200101-000000_Racy");
        Directory.CreateDirectory(expiredLink);

        var service = new TypeChangeRaceTrashService(_mockPluginLog.Object);
        service.ReparsePaths.Add(expiredLink);

        var (bytesFreed, itemsPurged) = service.PurgeExpiredTrash(_testRoot, 1, _logger);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
        Assert.True(Directory.Exists(expiredLink)); // fail closed: entry left unchanged
        _mockPluginLog.Verify(
            l => l.LogWarning(
                "Trash",
                It.Is<string>(m => m.Contains("changed type before purge")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    // ── TOCTOU move-retry race ────────────────────────────────────────────────
    [Fact]
    public void MoveToTrash_MoveRacesOnceThenSucceeds_RetriesWithFreshNameAndSucceeds()
    {
        var library = Path.Join(_testRoot, "library");
        var trashBase = Path.Join(library, ".jellyfin-trash");
        var source = Path.Join(library, "Orphan");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "data.bin"), "payload");

        var service = new RacyMoveTrashService(_mockPluginLog.Object, failCount: 1);

        var size = service.MoveToTrash(source, trashBase, _logger);

        Assert.True(size > 0);
        Assert.Equal(2, service.MoveCalls); // one simulated race + one successful retry
        Assert.False(Directory.Exists(source));

        var trashed = Directory.GetDirectories(trashBase);
        Assert.Single(trashed);
        Assert.Contains("Orphan", Path.GetFileName(trashed[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToTrash_MoveRaceExhaustsRetries_ReturnsZeroAndLogsError()
    {
        var library = Path.Join(_testRoot, "library2");
        var trashBase = Path.Join(library, ".jellyfin-trash");
        var source = Path.Join(library, "Orphan2");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Join(source, "data.bin"), "payload");

        // Fails every attempt: the retry guard gives up after MoveRetries (3) retries.
        var service = new RacyMoveTrashService(_mockPluginLog.Object, failCount: 10);

        var size = service.MoveToTrash(source, trashBase, _logger);

        Assert.Equal(0, size);
        Assert.Equal(4, service.MoveCalls); // attempts 0..3; the 4th fails the `moveAttempt < MoveRetries` guard
        Assert.True(Directory.Exists(source)); // nothing was moved
        _mockPluginLog.Verify(
            l => l.LogError(
                "Trash",
                It.Is<string>(m => m.Contains("Failed to move directory to trash")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    // ── Default seam implementations (production wrappers) ─────────────────────

    [Fact]
    public void DefaultSeams_OperateOnTheRealFilesystem()
    {
        Directory.CreateDirectory(_testRoot);
        var service = new TrashService(_mockPluginLog.Object);

        var realDir = Path.Join(_testRoot, "real");
        Directory.CreateDirectory(realDir);

        // IsReparsePoint: a real directory is not a reparse point; a missing path is not either.
        Assert.False(service.IsReparsePoint(realDir));
        Assert.False(service.IsReparsePoint(Path.Join(_testRoot, "missing")));

        // DestinationExists: true for an existing directory, false for a missing path.
        Assert.True(service.DestinationExists(realDir));
        Assert.False(service.DestinationExists(Path.Join(_testRoot, "missing")));

        // MoveDirectory: moves a real directory.
        var moveDest = Path.Join(_testRoot, "moved");
        service.MoveDirectory(realDir, moveDest);
        Assert.False(Directory.Exists(realDir));
        Assert.True(Directory.Exists(moveDest));

        // DeleteReparsePointLinkNode: must reject a real (non-reparse-point) path, fail closed.
        // The method guards against the TOCTOU race where a symlink is replaced by a real directory
        // between the caller's IsReparsePoint check and the deletion. Passing a real directory
        // must throw InvalidOperationException and leave the entry unchanged.
        Assert.Throws<InvalidOperationException>(() => service.DeleteReparsePointLinkNode(moveDest));
        Assert.True(Directory.Exists(moveDest)); // entry must be left unchanged
        Directory.Delete(moveDest);              // manual cleanup
    }

    // ── Test seams ────────────────────────────────────────────────────────────

    /// <summary>
    ///     A <see cref="TrashService"/> whose reparse-point detection is driven by an explicit set
    ///     of paths, so the symlink/junction guards can be exercised without real reparse points.
    /// </summary>
    private sealed class ReparseStubTrashService : TrashService
    {
        public ReparseStubTrashService(IPluginLogService pluginLog)
            : base(pluginLog)
        {
        }

        public HashSet<string> ReparsePaths { get; } = new(StringComparer.Ordinal);

        public List<string> DeletedLinkNodes { get; } = new();

        internal override bool IsReparsePoint(string path) =>
            ReparsePaths.Contains(path)
            || ReparsePaths.Any(p => string.Equals(
                Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

        internal override void DeleteReparsePointLinkNode(string path)
        {
            DeletedLinkNodes.Add(path);

            // Actually remove the empty directory so the surrounding purge ends with a clean tree.
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    /// <summary>
    ///     A <see cref="TrashService"/> that reports a scripted set of paths as reparse points but
    ///     whose <see cref="TrashService.DeleteReparsePointLinkNode"/> always throws
    ///     <see cref="InvalidOperationException"/>, simulating the concurrent-replacement race the
    ///     production guard detects.
    /// </summary>
    private sealed class TypeChangeRaceTrashService : TrashService
    {
        public TypeChangeRaceTrashService(IPluginLogService pluginLog)
            : base(pluginLog)
        {
        }

        public HashSet<string> ReparsePaths { get; } = new(StringComparer.Ordinal);

        internal override bool IsReparsePoint(string path) =>
            ReparsePaths.Contains(path)
            || ReparsePaths.Any(p => string.Equals(
                Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

        internal override void DeleteReparsePointLinkNode(string path) =>
            throw new InvalidOperationException(
                $"'{path}' is no longer a reparse point at deletion time (concurrent replacement).");
    }

    /// <summary>
    ///     A <see cref="TrashService"/> whose <see cref="TrashService.MoveDirectory"/> throws an
    ///     <see cref="IOException"/> for the first <c>failCount</c> attempts (simulating another
    ///     process winning the destination between the collision check and the move), reporting the
    ///     destination as existing so the TOCTOU retry path runs. Once the failure budget is spent it
    ///     delegates to the real move.
    /// </summary>
    private sealed class RacyMoveTrashService : TrashService
    {
        private readonly int _failCount;

        public RacyMoveTrashService(IPluginLogService pluginLog, int failCount)
            : base(pluginLog)
        {
            _failCount = failCount;
        }

        public int MoveCalls { get; private set; }

        internal override void MoveDirectory(string source, string destination)
        {
            MoveCalls++;
            if (MoveCalls <= _failCount)
            {
                throw new IOException("Simulated TOCTOU race: destination already exists.");
            }

            base.MoveDirectory(source, destination);
        }

        internal override bool DestinationExists(string path) => MoveCalls <= _failCount;
    }
}

