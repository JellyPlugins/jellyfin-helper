using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Tests for <see cref="GrowthTimelineService"/>.
/// </summary>
public sealed class GrowthTimelineServiceTests : IDisposable
{
    private readonly string _dataPath;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<GrowthTimelineService>> _loggerMock;
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly PluginConfiguration _config;
    private readonly GrowthTimelineService _sut;

    public GrowthTimelineServiceTests()
    {
        _dataPath = Path.Join(Path.GetTempPath(), "jfh-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);

        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<GrowthTimelineService>();

        _config = new PluginConfiguration { TrashFolderPath = ".trash" };
        _configHelperMock = new Mock<ICleanupConfigHelper>();
        _configHelperMock.Setup(c => c.GetConfig()).Returns(_config);
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns<string>(lib => Path.Join(lib, ".trash"));

        var appPaths = TestMockFactory.CreateAppPaths(_dataPath);

        _sut = new GrowthTimelineService(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            appPaths.Object,
            _loggerMock.Object,
            _configHelperMock.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
        try
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task LoadTimelineAsync_NoFileOnDisk_ReturnsNull()
    {
        var result = await _sut.LoadTimelineAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ComputeTimelineAsync_NoLibraries_ReturnsEmptyMonthlyResult()
    {
        // No libraries configured → the service must short-circuit with an empty result
        // and default monthly granularity. Must also not touch the persistence layer.
        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.DataPoints);
        Assert.Equal("monthly", result.Granularity);
        Assert.False(File.Exists(Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json")));
    }

    [Fact]
    public async Task ComputeTimelineAsync_CanBeCancelled_BeforeAnyWork()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.ComputeTimelineAsync(cts.Token));
    }

    [Fact]
    public async Task ComputeTimelineAsync_FirstScan_CreatesBaselineAndTimelineFiles()
    {
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie (2020)");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);

        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie (2020)", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([
                new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 5000 }
            ]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.DataPoints);
        Assert.Equal(1, result.TotalDirectoriesScanned);
        Assert.True(File.Exists(Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json")));
        Assert.True(File.Exists(Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json")));
    }

    [Fact]
    public async Task ComputeTimelineAsync_SkipsTrickplayFolders()
    {
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var trickplay = Path.Join(libRoot, "Movie.trickplay");
        Directory.CreateDirectory(trickplay);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = trickplay, Name = "Movie.trickplay", IsDirectory = true }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
        // GetFiles on the trickplay dir must never happen (loop `continue`s before that).
        _fileSystemMock.Verify(f => f.GetFiles(trickplay), Times.Never);
    }

    [Fact]
    public async Task ComputeTimelineAsync_SkipsTrashFolder_ByLeafName()
    {
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var trash = Path.Join(libRoot, ".trash");
        Directory.CreateDirectory(trash);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = trash, Name = ".trash", IsDirectory = true }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
        _fileSystemMock.Verify(f => f.GetFiles(trash), Times.Never);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LooseAudioAndVideoFilesInLibraryRoot_AreCounted()
    {
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);

        // Real files so File.GetCreationTimeUtc returns something valid.
        var mkv = Path.Join(libRoot, "movie.mkv");
        var mp3 = Path.Join(libRoot, "song.mp3");
        var txt = Path.Join(libRoot, "readme.txt");
        File.WriteAllText(mkv, "video");
        File.WriteAllText(mp3, "audio");
        File.WriteAllText(txt, "readme");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(libRoot))
            .Returns([
                new FileSystemMetadata { FullName = mkv, Name = "movie.mkv", IsDirectory = false, Length = 5, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new FileSystemMetadata { FullName = mp3, Name = "song.mp3", IsDirectory = false, Length = 5, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new FileSystemMetadata { FullName = txt, Name = "readme.txt", IsDirectory = false, Length = 6 }
            ]);

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        // mkv + mp3 counted, txt (non-media) ignored.
        Assert.Equal(2, result.TotalDirectoriesScanned);
    }

    [Fact]
    public async Task ComputeTimelineAsync_SecondScan_PreservesBaselineHistory()
    {
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var first = await _sut.ComputeTimelineAsync(CancellationToken.None);
        Assert.NotEmpty(first.DataPoints);
        var firstScanTs = first.FirstScanTimestamp;

        var second = await _sut.ComputeTimelineAsync(CancellationToken.None);

        // FirstScanTimestamp must be preserved across scans.
        Assert.Equal(firstScanTs, second.FirstScanTimestamp);
        Assert.NotEmpty(second.DataPoints);
    }

    [Fact]
    public async Task LoadTimelineAsync_ReturnsPersistedTimeline_AfterComputeRoundtrip()
    {
        // Guards the disk-read path of LoadTimelineAsync (file exists + valid JSON).
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 100 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        await _sut.ComputeTimelineAsync(CancellationToken.None);

        var loaded = await _sut.LoadTimelineAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.NotEmpty(loaded!.DataPoints);
    }

    [Fact]
    public async Task LoadTimelineAsync_CorruptedJson_ReturnsNull()
    {
        // A corrupted or truncated timeline file must not blow up the caller.
        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        await File.WriteAllTextAsync(timelinePath, "{ not valid json");

        var loaded = await _sut.LoadTimelineAsync(CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task ComputeTimelineAsync_EmptyAfterHavingData_ReturnsWithoutThrowing()
    {
        // Sequence: first scan with data → second scan with no libraries.
        // The second call must (a) return an empty transient result, AND (b) actually
        // PERSIST that empty state to disk so LoadTimelineAsync reflects reality.
        // A test that only inspects the transient return value could pass even if
        // SaveTimelineAsync was silently skipped - leaving stale non-zero data on disk.
        // We therefore reload from a fresh instance below to prove the persisted point
        // is the zero snapshot.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 500 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var first = await _sut.ComputeTimelineAsync(CancellationToken.None);
        Assert.NotEmpty(first.DataPoints);
        Assert.Equal(1, first.TotalDirectoriesScanned);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var second = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(0, second.TotalDirectoriesScanned);

        // Persistence proof: reload from disk (bypassing any in-memory cache) and verify
        // the persisted timeline reports the zero snapshot too, not the stale first scan.
        var reloaded = await _sut.LoadTimelineAsync(CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded!.TotalDirectoriesScanned);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LegacyGroupedBaselineOnDisk_IsDiscardedAndRebuiltPerDirectory()
    {
        // Legacy baselines keyed with a '|' separator (grouped by library+letter) are
        // incompatible with the per-directory format and would produce wrong diffs, so the
        // service must throw them away and rebuild a fresh per-directory baseline.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");
        await File.WriteAllTextAsync(
            baselinePath,
            "{\"firstScanTimestamp\":\"2020-01-01T00:00:00Z\",\"directories\":{\"lib|M\":{\"createdUtc\":\"2020-01-01T00:00:00Z\",\"size\":10,\"count\":3}}}");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotEmpty(result.DataPoints);
        Assert.Equal(1, result.TotalDirectoriesScanned);

        // The persisted baseline must no longer contain any grouped '|' key - it was rebuilt per-directory.
        var rewritten = await File.ReadAllTextAsync(baselinePath);
        using var doc = System.Text.Json.JsonDocument.Parse(rewritten);
        var dirs = doc.RootElement.GetProperty("directories");
        foreach (var entry in dirs.EnumerateObject())
        {
            Assert.DoesNotContain('|', entry.Name);
        }

        Assert.Contains(dirs.EnumerateObject(), e => e.Name == movieDir);
    }

    [Fact]
    public async Task ComputeTimelineAsync_BaselineExistsButNoTimeline_ReconstructsFromBaseline()
    {
        // A valid baseline with no timeline file (post-migration / data loss) must take the
        // historical-reconstruction branch instead of a fresh first scan, preserving the
        // originally recorded FirstScanTimestamp.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        var seededTs = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");
        await File.WriteAllTextAsync(
            baselinePath,
            "{\"firstScanTimestamp\":\"2021-06-01T00:00:00Z\",\"directories\":{" +
            "\"" + movieDir.Replace("\\", "\\\\") + "\":{\"createdUtc\":\"2021-01-01T00:00:00Z\",\"size\":500,\"count\":1}}}");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1200 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotEmpty(result.DataPoints);
        Assert.Equal(1, result.TotalDirectoriesScanned);
        Assert.Equal(seededTs, result.FirstScanTimestamp);
        Assert.True(File.Exists(Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json")));
    }

    [Fact]
    public async Task ComputeTimelineAsync_NoDirectoriesButPriorHistoryOnDisk_PersistsZeroSnapshotPreservingHistory()
    {
        // A prior non-zero point on disk plus zero libraries now: the empty-library branch must
        // merge a zero snapshot onto the surviving historical point and persist that, so the
        // chart shows a drop-to-zero rather than losing the history or keeping stale data.
        var oldDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        await File.WriteAllTextAsync(
            timelinePath,
            "{\"granularity\":\"monthly\",\"firstScanTimestamp\":\"2020-01-01T00:00:00Z\"," +
            "\"dataPoints\":[{\"date\":\"2020-01-01T00:00:00Z\",\"cumulativeSize\":9999,\"cumulativeFileCount\":5}]}");

        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");
        await File.WriteAllTextAsync(
            baselinePath,
            "{\"firstScanTimestamp\":\"2020-01-01T00:00:00Z\",\"directories\":{}}");

        // No libraries configured (default mock returns empty).

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        // History survives (the 2020 non-zero point) and the latest point drops to zero.
        Assert.NotEmpty(result.DataPoints);
        Assert.Contains(result.DataPoints, p => p.Date == oldDate && p.CumulativeSize == 9999);
        var latest = result.DataPoints[^1];
        Assert.Equal(0, latest.CumulativeSize);
        Assert.Equal(0, latest.CumulativeFileCount);

        // Prove it was persisted, not just returned transiently.
        var reloaded = await _sut.LoadTimelineAsync(CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Contains(reloaded!.DataPoints, p => p.Date == oldDate && p.CumulativeSize == 9999);
        Assert.Equal(0, reloaded.DataPoints[^1].CumulativeSize);
    }

    [Fact]
    public async Task LoadTimelineAsync_CancelledDuringRead_PropagatesOperationCanceled()
    {
        // A cancelled read must surface as OperationCanceledException, NOT be swallowed to null
        // the way a corrupt-file read is - callers need to distinguish "aborted" from "no data".
        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        await File.WriteAllTextAsync(
            timelinePath,
            "{\"granularity\":\"monthly\",\"dataPoints\":[]}");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.LoadTimelineAsync(cts.Token));
    }

    [Fact]
    public async Task ComputeTimelineAsync_DirectoryCreationTimePre1990_FallsBackToLastWriteTime()
    {
        // Filesystems that don't track creation time report a pre-1990 sentinel; the scan must
        // fall back to last-write time so the directory is still counted with a sane date.
        // Timestamps come from the FileSystemMetadata the injected filesystem returns (mockable and
        // platform-independent) - the real dir is kept only so the reparse .Attributes read sees a
        // real, non-reparse directory.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        var pre1990 = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var post1990 = new DateTime(2015, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata
            {
                FullName = movieDir, Name = "Movie", IsDirectory = true,
                CreationTimeUtc = pre1990, LastWriteTimeUtc = post1990
            }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(1, result.TotalDirectoriesScanned);
        // Earliest date reflects the post-1990 last-write year, proving the pre-1990 creation date was discarded.
        Assert.Equal(post1990.Year, result.EarliestFileDate.Year);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LooseFileCreationTimePre1990_FallsBackToLastWriteTime()
    {
        // Same creation-time fallback as directories, but for a loose media file in the library root.
        // Timestamps come from the mock FileSystemMetadata; only the real library root dir is kept so
        // the reparse .Attributes read on the root sees a real, non-reparse directory.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var mkv = Path.Join(libRoot, "movie.mkv");

        var pre1990 = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var post1990 = new DateTime(2015, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(libRoot))
            .Returns([new FileSystemMetadata
            {
                FullName = mkv, Name = "movie.mkv", IsDirectory = false, Length = 5,
                CreationTimeUtc = pre1990, LastWriteTimeUtc = post1990
            }]);

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(1, result.TotalDirectoriesScanned);
        Assert.Equal(post1990.Year, result.EarliestFileDate.Year);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LibraryScanThrowsIoException_IsSwallowedAndScanContinues()
    {
        // A failing library (I/O error while enumerating) must be logged and skipped, not
        // propagated - one broken mount should not abort the whole timeline computation.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot)).Throws(new IOException("disk gone"));

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.TotalDirectoriesScanned);
    }

    [Fact]
    public void GetDirectorySize_EnumerationThrowsIoException_ReturnsAccumulatedTotalWithoutThrowing()
    {
        // A directory that fails mid-enumeration must be logged-and-skipped so the traversal
        // returns the bytes counted so far rather than aborting the whole size computation.
        var fsMock = new Mock<IFileSystem>();
        var root = Path.Join(_dataPath, "unreadable");
        fsMock.Setup(f => f.GetFiles(root)).Throws(new IOException("permission denied"));

        var sut = new GrowthTimelineService(
            _libraryManagerMock.Object,
            fsMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateAppPaths(_dataPath).Object,
            _loggerMock.Object,
            _configHelperMock.Object);

        var size = sut.GetDirectorySize(root, string.Empty, string.Empty, CancellationToken.None);

        Assert.Equal(0, size);
    }

    [Fact]
    public async Task ComputeTimelineAsync_CorruptBaselineOnDisk_IsTreatedAsFirstScan()
    {
        // A corrupt baseline must not throw: LoadBaselineAsync returns null on JsonException,
        // the run is treated as a first scan, and a fresh valid baseline is written.
        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");
        await File.WriteAllTextAsync(baselinePath, "{ truncated baseline");

        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotEmpty(result.DataPoints);
        Assert.Equal(1, result.TotalDirectoriesScanned);

        // Baseline was rewritten to valid JSON, proving corruption was recovered rather than surfaced.
        var rewritten = await File.ReadAllTextAsync(baselinePath);
        using var doc = System.Text.Json.JsonDocument.Parse(rewritten);
        Assert.NotNull(doc);
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.

    [Fact]
    public async Task ComputeTimelineAsync_DirectoryBothCreationAndWriteTimePre1990_IsSkipped()
    {
        // When BOTH the creation and last-write timestamps are pre-1990 sentinels (a filesystem
        // that tracks neither), there is no sane date to attribute the directory to, so it must be
        // skipped entirely rather than plotted at a bogus year.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        var pre1990 = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata
            {
                FullName = movieDir, Name = "Movie", IsDirectory = true,
                CreationTimeUtc = pre1990, LastWriteTimeUtc = pre1990
            }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LooseFileBothCreationAndWriteTimePre1990_IsSkipped()
    {
        // Same both-timestamps-pre-1990 skip as directories, but for a loose media file in the root.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var mkv = Path.Join(libRoot, "movie.mkv");

        var pre1990 = new DateTime(1980, 5, 5, 0, 0, 0, DateTimeKind.Utc);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(libRoot))
            .Returns([new FileSystemMetadata
            {
                FullName = mkv, Name = "movie.mkv", IsDirectory = false, Length = 5,
                CreationTimeUtc = pre1990, LastWriteTimeUtc = pre1990
            }]);

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
    }

    [Fact]
    public void GetDirectorySize_ChildTrickplaySubdirectory_IsExcludedFromRecursiveTotal()
    {
        // The recursive traversal must skip .trickplay (and trash) subdirectories at every depth,
        // not only at the top level, so their bytes never inflate a library's reported size.
        var fsMock = new Mock<IFileSystem>();
        var root = Path.Join(_dataPath, "Show");
        var trickplayChild = Path.Join(root, "Season 1.trickplay");

        fsMock.Setup(f => f.GetFiles(root))
            .Returns([new FileSystemMetadata { FullName = Path.Join(root, "ep.mkv"), Name = "ep.mkv", IsDirectory = false, Length = 100 }]);
        fsMock.Setup(f => f.GetDirectories(root))
            .Returns([new FileSystemMetadata { FullName = trickplayChild, Name = "Season 1.trickplay", IsDirectory = true }]);
        // The trickplay child WOULD contribute 500 bytes if it were (wrongly) traversed.
        fsMock.Setup(f => f.GetFiles(trickplayChild))
            .Returns([new FileSystemMetadata { FullName = Path.Join(trickplayChild, "thumbs.bif"), Name = "thumbs.bif", IsDirectory = false, Length = 500 }]);
        fsMock.Setup(f => f.GetDirectories(trickplayChild)).Returns(Array.Empty<FileSystemMetadata>());

        var sut = new GrowthTimelineService(
            _libraryManagerMock.Object,
            fsMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateAppPaths(_dataPath).Object,
            _loggerMock.Object,
            _configHelperMock.Object);

        var size = sut.GetDirectorySize(root, ".trash", Path.Join(root, ".trash"), CancellationToken.None);

        // Only the real episode file is counted; the trickplay child was skipped.
        Assert.Equal(100, size);
    }

    // === Atomic save: crash-safe writes use temp-then-move ===

    [Fact]
    public async Task SaveBaselineAsync_WritesToDisk_FileIsValidJson()
    {
        // Trigger a real compute with a non-empty library so SaveBaselineAsync is called.
        var libRoot = Path.Join(_dataPath, "lib");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie (2021)");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie (2021)", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 1000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        await _sut.ComputeTimelineAsync(CancellationToken.None);

        // AtomicFile writes fully or not at all - the file must exist and be valid JSON.
        var baselinePath = Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json");
        Assert.True(File.Exists(baselinePath), "Baseline file was not written to disk.");

        var json = await File.ReadAllTextAsync(baselinePath);
        Assert.False(string.IsNullOrWhiteSpace(json));
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task SaveTimelineAsync_WritesToDisk_FileIsValidJson()
    {
        var libRoot = Path.Join(_dataPath, "lib2");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie (2022)");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie (2022)", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 2000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        await _sut.ComputeTimelineAsync(CancellationToken.None);

        var timelinePath = Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json");
        Assert.True(File.Exists(timelinePath), "Timeline file was not written to disk.");

        var json = await File.ReadAllTextAsync(timelinePath);
        Assert.False(string.IsNullOrWhiteSpace(json));
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task SaveBaselineAsync_NoTempFilesLeftOnDisk_AfterSuccessfulWrite()
    {
        var libRoot = Path.Join(_dataPath, "lib3");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie (2023)");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie (2023)", IsDirectory = true, CreationTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 3000 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        await _sut.ComputeTimelineAsync(CancellationToken.None);

        // AtomicFile must clean up its .tmp file on success - no orphans.
        var tmpFiles = Directory.GetFiles(_dataPath, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task ComputeTimelineAsync_LibraryRootNotStattable_IsSkippedAndNotScanned()
    {
        // The sole library root cannot be stat'd: new DirectoryInfo(location).Attributes returns
        // all-bits (-1) on a non-existent path, so the ReparsePoint guard fires and the root is
        // skipped before any enumeration - a symlink/junction root must never be scanned.
        var location = Path.Join(_dataPath, "ghost-library");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [location] }]);

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
        _fileSystemMock.Verify(f => f.GetDirectories(location), Times.Never);
    }

    [Fact]
    public async Task ComputeTimelineAsync_TopLevelSubdirNotStattable_IsSkipped()
    {
        // A real library root yields one top-level subdir whose path cannot be stat'd; its
        // Attributes return all-bits (-1) so the top-level ReparsePoint guard skips it before
        // any size measurement - its files must never be enumerated.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var subDir = Path.Join(libRoot, "ghost-movie");

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = subDir, Name = "ghost-movie", IsDirectory = true }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.Equal(0, result.TotalDirectoriesScanned);
        _fileSystemMock.Verify(f => f.GetFiles(subDir), Times.Never);
    }
}