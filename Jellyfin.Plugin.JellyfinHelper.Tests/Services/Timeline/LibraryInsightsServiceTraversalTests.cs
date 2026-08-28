using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Exercises the recursive directory walk inside GetDirectorySizeAndNewestTime and the pre-1990 creation-time fallbacks.
/// </summary>
public sealed class LibraryInsightsServiceTraversalTests
{
    [Fact]
    public async Task ComputeInsightsAsync_TracksNewestFileWriteTime_AsModifiedDate()
    {
        // A single file whose LastWriteTimeUtc is deliberately newer than the directory's own
        // timestamp must become the entry's ModifiedUtc, proving newestTime is captured and preferred.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Movie");

        // Force the directory's own mtime to be older than the file so the newest FILE time must win.
        Directory.SetLastWriteTimeUtc(mediaDir, DateTime.UtcNow.AddDays(-10));

        var newestFileTime = DateTime.UtcNow.AddHours(-1);
        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "movie.mkv", length: 123_456, lastWriteUtc: newestFileTime);

        var service = CreateService(temp.Path, fs);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(newestFileTime, entry.ModifiedUtc);
        Assert.Equal(123_456, entry.Size);
    }

    [Fact]
    public async Task ComputeInsightsAsync_RecursesIntoNestedSubdirectories_SummingAllDescendantFiles()
    {
        // A file at the top level plus a file in a real nested subdirectory. If the nested dir were
        // ignored the size would only reflect the top-level file; the total proves it was pushed and walked.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Show");
        var seasonDir = temp.CreateSubDirectory("Show/Season 01");

        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "poster.mkv", length: 1_000, lastWriteUtc: DateTime.UtcNow.AddDays(-3));
        fs.AddFile(seasonDir, "episode.mkv", length: 9_000, lastWriteUtc: DateTime.UtcNow.AddDays(-3));

        var service = CreateService(temp.Path, fs);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(10_000, entry.Size);
    }

    [Fact]
    public async Task ComputeInsightsAsync_SkipsNestedTrickplayDirectory_DuringSizeWalk()
    {
        // A '.trickplay' folder nested under a media folder must have its bytes excluded from the size.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Movie");
        var trickplayDir = temp.CreateSubDirectory("Movie/backdrops.trickplay");

        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "movie.mkv", length: 5_000, lastWriteUtc: DateTime.UtcNow.AddDays(-2));
        fs.AddFile(trickplayDir, "tiles.bin", length: 4_000_000, lastWriteUtc: DateTime.UtcNow.AddDays(-2));

        var service = CreateService(temp.Path, fs);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(5_000, entry.Size);
    }

    [Fact]
    public async Task ComputeInsightsAsync_SkipsNestedTrashDirectory_DuringSizeWalk()
    {
        // A nested folder matching the configured trash name must be skipped during the recursive walk,
        // not only at the top level - so its bytes stay out of the parent folder's size.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Movie");
        var trashDir = temp.CreateSubDirectory("Movie/.jellyfin-helper-trash");

        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "movie.mkv", length: 6_000, lastWriteUtc: DateTime.UtcNow.AddDays(-2));
        fs.AddFile(trashDir, "deleted.mkv", length: 8_000_000, lastWriteUtc: DateTime.UtcNow.AddDays(-2));

        var service = CreateService(temp.Path, fs, trashFolderPath: ".jellyfin-helper-trash");

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(6_000, entry.Size);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComputeInsightsAsync_InaccessibleNestedDirectory_IsSkipped_WithoutFailing(bool useIoException)
    {
        // The walk pops a nested directory whose enumeration throws; the outer catch must swallow both
        // IOException and UnauthorizedAccessException and continue, still counting the accessible sibling.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Movie");
        var goodDir = temp.CreateSubDirectory("Movie/Extras");
        var badDir = temp.CreateSubDirectory("Movie/Locked");

        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "movie.mkv", length: 2_000, lastWriteUtc: DateTime.UtcNow.AddDays(-1));
        fs.AddFile(goodDir, "extra.mkv", length: 3_000, lastWriteUtc: DateTime.UtcNow.AddDays(-1));
        fs.ThrowOnGetFiles(
            badDir,
            useIoException ? new IOException("locked") : new UnauthorizedAccessException("denied"));

        var service = CreateService(temp.Path, fs);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(5_000, entry.Size);
    }

    [Fact]
    public async Task ComputeInsightsAsync_ChildDirectoryFailingAttributeProbe_IsSkipped_WithoutFailing()
    {
        // The size walk discovers a nested child that no longer exists on disk.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Movie");
        var phantomChild = Path.Join(mediaDir, "GhostSeason");

        var fs = new ScriptedFileSystem();
        fs.AddFile(mediaDir, "movie.mkv", length: 7_000, lastWriteUtc: DateTime.UtcNow.AddDays(-1));
        fs.AddPhantomDirectory(mediaDir, phantomChild);

        var service = CreateService(temp.Path, fs);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest);
        Assert.Equal(7_000, entry.Size);
    }

    [Fact]
    public async Task ComputeInsightsAsync_DirectoryWithPre1990CreationTime_FallsBackToLastWriteTime()
    {
        // A media folder with a stale pre-1990 creation time must fall back to its last-write time so the
        // entry is still emitted rather than dropped by the year<1990 guard.
        using var temp = new TempDirectory();
        var mediaDir = temp.CreateSubDirectory("Old Movie");
        temp.CreateFile("Old Movie/movie.mkv", 100_000);

        Directory.SetCreationTimeUtc(mediaDir, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(mediaDir, DateTime.UtcNow.AddDays(-1));

        var service = CreateRealIoService(temp.Path);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest, e => e.Name == "Old Movie");
        Assert.True(entry.CreatedUtc.Year >= 1990);
    }

    [Fact]
    public async Task ComputeInsightsAsync_LooseFileWithPre1990CreationTime_FallsBackToLastWriteTime()
    {
        // A loose media file with a stale pre-1990 creation time must fall back to its last-write time so
        // the file entry is still produced.
        using var temp = new TempDirectory();
        var filePath = temp.CreateFile("standalone.mkv", 100_000);

        File.SetCreationTimeUtc(filePath, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddDays(-1));

        var service = CreateRealIoService(temp.Path);

        var result = await service.ComputeInsightsAsync(CancellationToken.None);

        var entry = Assert.Single(result.Largest, e => e.Name == "standalone");
        Assert.True(entry.CreatedUtc.Year >= 1990);
    }

    // -- Service construction ----------------------------------------

    private static LibraryInsightsService CreateService(
        string locationPath,
        ScriptedFileSystem fileSystem,
        string? trashFolderPath = null)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo
            {
                Name = "Movies",
                Locations = [locationPath],
                CollectionType = CollectionTypeOptions.movies
            }
        });

        var config = new PluginConfiguration();
        if (trashFolderPath != null)
        {
            config.TrashFolderPath = trashFolderPath;
        }

        return new LibraryInsightsService(
            libraryManager.Object,
            fileSystem.Build().Object,
            TestMockFactory.CreateCleanupConfigHelper(config).Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LibraryInsightsService>().Object);
    }

    // Real-IO-delegating variant, used where the code path reads real directory/file timestamps.
    private static LibraryInsightsService CreateRealIoService(string locationPath)
    {
        var libraryManager = TestMockFactory.CreateLibraryManager();
        libraryManager.Setup(lm => lm.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
        {
            new VirtualFolderInfo
            {
                Name = "Movies",
                Locations = [locationPath],
                CollectionType = CollectionTypeOptions.movies
            }
        });

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(fs => fs.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((path, _) =>
            {
                if (!Directory.Exists(path)) return [];
                return Directory.GetDirectories(path)
                    .Select(d => new FileSystemMetadata { FullName = d, IsDirectory = true })
                    .ToArray();
            });
        fileSystem.Setup(fs => fs.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns<string, bool>((path, _) =>
            {
                if (!Directory.Exists(path)) return [];
                return Directory.GetFiles(path)
                    .Select(f => new FileSystemMetadata
                    {
                        FullName = f,
                        IsDirectory = false,
                        Length = new FileInfo(f).Length,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(f)
                    })
                    .ToArray();
            });

        return new LibraryInsightsService(
            libraryManager.Object,
            fileSystem.Object,
            TestMockFactory.CreateCleanupConfigHelper(new PluginConfiguration()).Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<LibraryInsightsService>().Object);
    }

    // -- Scripted IFileSystem -----------------------------------------

    /// <summary>
    ///     Builds a Mock{IFileSystem} over a real on-disk tree, returning real subdirectories (so the walk's DirectoryInfo.Attributes read succeeds) while letting each test inject exact file lengths and last-write times, or make a specific directory throw.
    /// </summary>
    private sealed class ScriptedFileSystem
    {
        private readonly Dictionary<string, List<FileSystemMetadata>> _filesByDir = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _getFilesThrows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _phantomChildDirs = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string dir, string name, long length, DateTime lastWriteUtc)
        {
            var fullName = Path.Join(dir, name);
            if (!_filesByDir.TryGetValue(dir, out var list))
            {
                list = new List<FileSystemMetadata>();
                _filesByDir[dir] = list;
            }

            list.Add(new FileSystemMetadata
            {
                FullName = fullName,
                IsDirectory = false,
                Length = length,
                LastWriteTimeUtc = lastWriteUtc
            });
        }

        public void ThrowOnGetFiles(string dir, Exception ex) => _getFilesThrows[dir] = ex;

        // Registers a non-existent child path that GetDirectories should report for `parentDir`. The
        // walk then probes DirectoryInfo.Attributes on a path that isn't on disk, forcing the throw.
        public void AddPhantomDirectory(string parentDir, string phantomFullName)
        {
            if (!_phantomChildDirs.TryGetValue(parentDir, out var list))
            {
                list = new List<string>();
                _phantomChildDirs[parentDir] = list;
            }

            list.Add(phantomFullName);
        }

        public Mock<IFileSystem> Build()
        {
            var mock = new Mock<IFileSystem>();

            mock.Setup(fs => fs.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns<string, bool>((path, _) =>
                {
                    var results = new List<FileSystemMetadata>();
                    if (Directory.Exists(path))
                    {
                        results.AddRange(Directory.GetDirectories(path)
                            .Select(d => new FileSystemMetadata { FullName = d, IsDirectory = true }));
                    }

                    if (_phantomChildDirs.TryGetValue(path, out var phantoms))
                    {
                        results.AddRange(phantoms
                            .Select(p => new FileSystemMetadata { FullName = p, IsDirectory = true }));
                    }

                    return results.ToArray();
                });

            mock.Setup(fs => fs.GetFiles(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns<string, bool>((path, _) =>
                {
                    if (_getFilesThrows.TryGetValue(path, out var ex))
                    {
                        throw ex;
                    }

                    return _filesByDir.TryGetValue(path, out var list)
                        ? list.ToArray()
                        : [];
                });

            return mock;
        }
    }

    // -- TempDirectory helper (self-contained; sibling helpers are private to their class) --

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "jfh-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubDirectory(string name)
        {
            var dir = SafeCombinePath(name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string CreateFile(string relativePath, long size)
        {
            var fullPath = SafeCombinePath(relativePath);
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = File.Create(fullPath);
            fs.SetLength(size);
            return fullPath;
        }

        private string SafeCombinePath(string relativePath)
        {
            var basePath = System.IO.Path.GetFullPath(Path);
            if (!basePath.EndsWith(System.IO.Path.DirectorySeparatorChar))
            {
                basePath += System.IO.Path.DirectorySeparatorChar;
            }

            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Join(basePath, relativePath));
            if (!candidate.StartsWith(basePath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Path must stay within the temp directory.", nameof(relativePath));
            }

            return candidate;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup
            }
        }
    }
}
