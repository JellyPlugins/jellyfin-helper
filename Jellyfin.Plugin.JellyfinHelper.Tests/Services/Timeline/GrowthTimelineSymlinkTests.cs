using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.IO;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Tests that <see cref="GrowthTimelineService.GetDirectorySize" /> skips child-directory
///     symlinks and junction points during recursion, preventing StackOverflowException
///     from circular directory structures (A → B → A). The caller-supplied root is followed.
/// </summary>
public class GrowthTimelineSymlinkTests : IDisposable
{
    private readonly string _testRoot = Path.Join(Path.GetTempPath(), $"GTS-Symlink-{Guid.NewGuid():N}");
    private readonly GrowthTimelineService _service;

    public GrowthTimelineSymlinkTests()
    {
        Directory.CreateDirectory(_testRoot);

        _service = new GrowthTimelineService(
            TestMockFactory.CreateLibraryManager().Object,
            new RealFileSystemAdapter(),
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateAppPaths(_testRoot).Object,
            TestMockFactory.CreateLogger<GrowthTimelineService>().Object,
            TestMockFactory.CreateCleanupConfigHelper().Object);
    }

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
            // Ignore cleanup failures in CI
            _ = ex;
        }
    }

    // ── Symlink is not followed ───────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Symlink")]
    public void GetDirectorySize_DirectoryWithSymlink_DoesNotFollowSymlink()
    {
        var realDir = Path.Join(_testRoot, "real");
        var targetDir = Path.Join(_testRoot, "target");
        var linkDir = Path.Join(realDir, "link_to_target");
        Directory.CreateDirectory(realDir);
        Directory.CreateDirectory(targetDir);
        File.WriteAllBytes(Path.Join(realDir, "movie.mkv"), new byte[1024]);
        File.WriteAllBytes(Path.Join(targetDir, "extra.mkv"), new byte[2048]);

        try
        {
            Directory.CreateSymbolicLink(linkDir, targetDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Symlink creation requires elevated privileges on some Windows configurations.
            // xUnit 2.x has no Assert.Skip — early return keeps the test green but un-asserted,
            // which is acceptable: the test runs and asserts on runners that support symlinks.
            _ = ex;
            return;
        }

        // Only the 1 024-byte file in realDir should be counted; the symlink is skipped.
        var size = _service.GetDirectorySize(realDir, string.Empty, string.Empty, CancellationToken.None);

        Assert.Equal(1024, size);
    }

    // ── Circular symlink (A → B → A) does not cause StackOverflow ────────────

    [Fact]
    [Trait("Category", "Symlink")]
    public void GetDirectorySize_CircularSymlink_DoesNotCauseInfiniteRecursion()
    {
        var dirA = Path.Join(_testRoot, "A");
        var dirB = Path.Join(_testRoot, "B");
        var linkAToB = Path.Join(dirA, "link_to_b");
        var linkBToA = Path.Join(dirB, "link_to_a");

        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        File.WriteAllBytes(Path.Join(dirA, "a.mkv"), new byte[512]);
        File.WriteAllBytes(Path.Join(dirB, "b.mkv"), new byte[256]);

        try
        {
            Directory.CreateSymbolicLink(linkAToB, dirB);
            Directory.CreateSymbolicLink(linkBToA, dirA);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _ = ex;
            return;
        }

        // Should terminate without StackOverflowException.
        // Only a.mkv (512 bytes) should be counted; link_to_b is a ReparsePoint and is skipped.
        var size = _service.GetDirectorySize(dirA, string.Empty, string.Empty, CancellationToken.None);

        Assert.Equal(512, size);
    }

    // ── Root path itself is a symlink — IS followed (library roots can be symlinks) ───

    [Fact]
    [Trait("Category", "Symlink")]
    public void GetDirectorySize_RootIsSymlink_CountsFilesInsideTarget()
    {
        // Library roots can be symlinks (e.g. network mounts, bind mounts).
        // GetDirectorySize must traverse them so that timeline statistics are correct.
        // The ReparsePoint guard only applies to *sub*directories discovered during recursion
        // to prevent cycles — it intentionally does not skip the caller-supplied root.
        var realDir = Path.Join(_testRoot, "real_root_target");
        var linkDir = Path.Join(_testRoot, "symlink_root");
        Directory.CreateDirectory(realDir);
        File.WriteAllBytes(Path.Join(realDir, "movie.mkv"), new byte[4096]);

        try
        {
            Directory.CreateSymbolicLink(linkDir, realDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _ = ex;
            return;
        }

        // The root is a symlink — files inside the target are counted normally.
        var size = _service.GetDirectorySize(linkDir, string.Empty, string.Empty, CancellationToken.None);

        Assert.Equal(4096, size);
    }

    // ── Regular subdirectories are still traversed ───────────────────────────

    [Fact]
    public void GetDirectorySize_NestedRealDirectories_CountsAllFiles()
    {
        var a = Path.Join(_testRoot, "nested_a");
        var b = Path.Join(a, "b");
        var c = Path.Join(b, "c");
        Directory.CreateDirectory(c);
        File.WriteAllBytes(Path.Join(a, "f1.mkv"), new byte[100]);
        File.WriteAllBytes(Path.Join(b, "f2.mkv"), new byte[200]);
        File.WriteAllBytes(Path.Join(c, "f3.mkv"), new byte[300]);

        var size = _service.GetDirectorySize(a, string.Empty, string.Empty, CancellationToken.None);

        Assert.Equal(600, size);
    }

    // ── Cancellation is respected ─────────────────────────────────────────────

    [Fact]
    public void GetDirectorySize_CancellationRequested_ThrowsOperationCanceled()
    {
        var dir = Path.Join(_testRoot, "cancel_test");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Join(dir, "f.mkv"), new byte[64]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _service.GetDirectorySize(dir, string.Empty, string.Empty, cts.Token));
    }

    // ── Minimal IFileSystem adapter that reads from the real filesystem ───────

    /// <summary>
    ///     Thin adapter that delegates <c>GetFiles</c> and <c>GetDirectories</c> to the
    ///     real filesystem so the symlink tests can create actual reparse points on disk.
    ///     All other members throw <see cref="NotImplementedException" /> — they are never
    ///     called by <see cref="GrowthTimelineService.GetDirectorySize" />.
    /// </summary>
    private sealed class RealFileSystemAdapter : IFileSystem
    {
        public IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false)
            => Directory.Exists(path)
                ? Directory.GetFiles(path)
                    .Select(f => new FileSystemMetadata { FullName = f, Length = new FileInfo(f).Length })
                : [];

        public IEnumerable<FileSystemMetadata> GetFiles(
            string path,
            IReadOnlyList<string>? extensions,
            bool enableCaseSensitiveExtensions,
            bool recursive)
            => GetFiles(path);

        public IEnumerable<FileSystemMetadata> GetFiles(string path, string pattern, bool recursive)
            => throw new NotImplementedException();

        public IEnumerable<FileSystemMetadata> GetFiles(
            string path,
            string pattern,
            IReadOnlyList<string>? extensions,
            bool enableCaseSensitiveExtensions,
            bool recursive)
            => throw new NotImplementedException();

        public IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false)
            => Directory.Exists(path)
                ? Directory.GetDirectories(path)
                    .Select(d => new FileSystemMetadata { FullName = d, IsDirectory = true })
                : [];

        public FileSystemMetadata GetFileSystemInfo(string path) => throw new NotImplementedException();
        public FileSystemMetadata GetFileInfo(string path) => throw new NotImplementedException();
        public FileSystemMetadata GetDirectoryInfo(string path) => throw new NotImplementedException();
        public string MakeAbsolutePath(string folderPath, string filePath) => throw new NotImplementedException();

        public IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false)
            => throw new NotImplementedException();

        public bool IsShortcut(string filename) => throw new NotImplementedException();
        public string? ResolveShortcut(string filename) => throw new NotImplementedException();
        public void CreateShortcut(string shortcutPath, string target) => throw new NotImplementedException();
        public void MoveDirectory(string source, string destination) => throw new NotImplementedException();
        public string GetValidFilename(string filename) => throw new NotImplementedException();
        public DateTime GetCreationTimeUtc(FileSystemMetadata info) => throw new NotImplementedException();
        public DateTime GetCreationTimeUtc(string path) => throw new NotImplementedException();
        public DateTime GetLastWriteTimeUtc(FileSystemMetadata info) => throw new NotImplementedException();
        public DateTime GetLastWriteTimeUtc(string path) => throw new NotImplementedException();
        public void SwapFiles(string file1, string file2) => throw new NotImplementedException();
        public bool AreEqual(string path1, string path2) => throw new NotImplementedException();
        public bool ContainsSubPath(string parentPath, string path) => throw new NotImplementedException();
        public bool IsPathFile(string path) => throw new NotImplementedException();
        public void DeleteFile(string path) => throw new NotImplementedException();
        public IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false) => throw new NotImplementedException();
        public IEnumerable<string> GetFilePaths(string path, bool recursive = false) => throw new NotImplementedException();
        public IEnumerable<string> GetFilePaths(string path, string[]? extensions, bool enableCaseSensitiveExtensions, bool recursive) => throw new NotImplementedException();
        public IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false) => throw new NotImplementedException();
        public void SetHidden(string path, bool isHidden) => throw new NotImplementedException();
        public void SetAttributes(string path, bool isHidden, bool readOnly) => throw new NotImplementedException();
        public IEnumerable<FileSystemMetadata> GetDrives() => throw new NotImplementedException();
        public bool DirectoryExists(string path) => throw new NotImplementedException();
        public bool FileExists(string path) => throw new NotImplementedException();
        public string GetFileNameWithoutExtension(FileSystemMetadata info) => throw new NotImplementedException();
    }
}
