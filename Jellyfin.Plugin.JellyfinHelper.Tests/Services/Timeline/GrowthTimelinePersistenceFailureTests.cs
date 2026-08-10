using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Timeline;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Timeline;

/// <summary>
///     Persistence is best-effort: an I/O failure while saving the baseline or timeline must be
///     logged and swallowed so the computed result is still returned to the caller. The scan's
///     value comes from its in-memory computation, not from the write succeeding.
/// </summary>
public sealed class GrowthTimelinePersistenceFailureTests : IDisposable
{
    private readonly string _dataPath;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<GrowthTimelineService>> _loggerMock;
    private readonly Mock<ICleanupConfigHelper> _configHelperMock;
    private readonly GrowthTimelineService _sut;

    public GrowthTimelinePersistenceFailureTests()
    {
        _dataPath = Path.Join(Path.GetTempPath(), "jfh-timeline-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);

        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _loggerMock = TestMockFactory.CreateLogger<GrowthTimelineService>();

        var config = new PluginConfiguration { TrashFolderPath = ".trash" };
        _configHelperMock = new Mock<ICleanupConfigHelper>();
        _configHelperMock.Setup(c => c.GetConfig()).Returns(config);
        _configHelperMock.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns<string>(lib => Path.Join(lib, ".trash"));

        _sut = new GrowthTimelineService(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateAppPaths(_dataPath).Object,
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

    // Wires a single library with one movie directory containing one media file.
    private void SetupSingleMovieLibrary()
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
    }

    [Fact]
    public async Task ComputeTimelineAsync_BaselinePathUnwritable_LogsAndReturnsResultWithoutThrowing()
    {
        // The baseline JSON path already exists as a DIRECTORY, so AtomicFile's File.Move fails
        // with IOException after every retry. SaveBaselineAsync must swallow it and the scan must
        // still hand back its computed result.
        SetupSingleMovieLibrary();
        Directory.CreateDirectory(Path.Join(_dataPath, "jellyfin-helper-growth-baseline.json"));

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.DataPoints);
    }

    [Fact]
    public async Task ComputeTimelineAsync_TimelinePathUnwritable_LogsAndReturnsResultWithoutThrowing()
    {
        // The timeline JSON path already exists as a DIRECTORY, so the timeline save fails with
        // IOException. SaveTimelineAsync must swallow it and return the computed result.
        SetupSingleMovieLibrary();
        Directory.CreateDirectory(Path.Join(_dataPath, "jellyfin-helper-growth-timeline.json"));

        var result = await _sut.ComputeTimelineAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.DataPoints);
    }
}
