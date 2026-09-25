using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

/// <summary>
///     Verifies that a per-library-root failure to resolve the absolute trash path is contained: the scan logs a warning for that root and still walks the library instead of aborting.
/// </summary>
public sealed class MediaStatisticsServiceTrashPathResolutionTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<IPluginLogService> _pluginLogMock;

    public MediaStatisticsServiceTrashPathResolutionTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        _pluginLogMock = new Mock<IPluginLogService>();
    }

    private static string TestPath(params string[] segments)
        => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    private MediaStatisticsService CreateService(ICleanupConfigHelper configHelper)
        => new(
            _libraryManagerMock.Object,
            _fileSystemMock.Object,
            _pluginLogMock.Object,
            TestMockFactory.CreateLogger<MediaStatisticsService>().Object,
            configHelper);

    [Fact]
    public void CalculateStatistics_TrashPathResolutionThrowsArgumentException_LogsWarningAndCompletesScan()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 1_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        // Embedded null byte makes Path.GetFullPath throw ArgumentException inside the when-filter.
        var configHelper = new Mock<ICleanupConfigHelper>();
        configHelper.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        configHelper.Setup(c => c.GetTrashPath(It.IsAny<string>())).Returns("/trash\0bad");

        var service = CreateService(configHelper.Object);

        var result = service.CalculateStatistics();

        // The failed trash resolution is swallowed: the library is still scanned end-to-end.
        Assert.Single(result.Libraries);
        Assert.Equal(1, result.Libraries[0].VideoFileCount);

        // Exactly one warning is emitted for the offending root, carrying the resolution exception.
        _pluginLogMock.Verify(
            p => p.LogWarning(
                "MediaStatistics",
                It.IsAny<string>(),
                It.IsNotNull<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    [Fact]
    public void CalculateStatistics_TrashPathResolutionThrowsPathTooLong_ScanStillProceeds()
    {
        var libraryPath = TestPath("media", "movies");

        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var videoFile = new FileSystemMetadata
        {
            FullName = TestPath("media", "movies", "Film.mkv"),
            Name = "Film.mkv",
            Length = 1_000_000,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([videoFile]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        // An absurdly long path drives Path.GetFullPath to PathTooLongException, exercising the
        // remaining alternative of the same catch filter so resolvedFullTrashPath is left null.
        var configHelper = new Mock<ICleanupConfigHelper>();
        configHelper.Setup(c => c.GetConfig()).Returns(new PluginConfiguration());
        configHelper.Setup(c => c.GetTrashPath(It.IsAny<string>()))
            .Returns("/" + new string('a', 40_000));

        var service = CreateService(configHelper.Object);

        var result = service.CalculateStatistics();

        Assert.Single(result.Libraries);
        Assert.Equal(1, result.Libraries[0].VideoFileCount);
    }
}
