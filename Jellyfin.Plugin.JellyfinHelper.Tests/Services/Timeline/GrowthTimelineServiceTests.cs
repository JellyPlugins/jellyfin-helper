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
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie (2020)", IsDirectory = true }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([
                new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 5000 }
            ]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.DataPoints);
        Assert.Equal(1, result.TotalFilesScanned);
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

        Assert.Equal(0, result.TotalFilesScanned);
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

        Assert.Equal(0, result.TotalFilesScanned);
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
                new FileSystemMetadata { FullName = mkv, Name = "movie.mkv", IsDirectory = false, Length = 5 },
                new FileSystemMetadata { FullName = mp3, Name = "song.mp3", IsDirectory = false, Length = 5 },
                new FileSystemMetadata { FullName = txt, Name = "readme.txt", IsDirectory = false, Length = 6 }
            ]);

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        // mkv + mp3 counted, txt (non-media) ignored.
        Assert.Equal(2, result.TotalFilesScanned);
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
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true }]);
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
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true }]);
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
        // We only assert the second call completes without throwing and returns a valid
        // result object; the exact data-point shape depends on TimelineAggregator's
        // TrimLeadingZeros / Merge logic which is covered by TimelineAggregatorTests.
        var libRoot = Path.Join(_dataPath, "library");
        Directory.CreateDirectory(libRoot);
        var movieDir = Path.Join(libRoot, "Movie");
        Directory.CreateDirectory(movieDir);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Locations = [libRoot] }]);
        _fileSystemMock.Setup(f => f.GetDirectories(libRoot))
            .Returns([new FileSystemMetadata { FullName = movieDir, Name = "Movie", IsDirectory = true }]);
        _fileSystemMock.Setup(f => f.GetFiles(libRoot)).Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(f => f.GetFiles(movieDir))
            .Returns([new FileSystemMetadata { FullName = Path.Join(movieDir, "movie.mkv"), Name = "movie.mkv", IsDirectory = false, Length = 500 }]);
        _fileSystemMock.Setup(f => f.GetDirectories(movieDir)).Returns(Array.Empty<FileSystemMetadata>());

        await _sut.ComputeTimelineAsync(CancellationToken.None);

        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        var second = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(0, second.TotalFilesScanned);
    }

    // ANCHOR: TESTS_END - do not remove, used by replace_in_file to append new tests.
}