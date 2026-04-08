using Jellyfin.Plugin.FiletreeCleaner.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FiletreeCleaner.Tests;

public class CleanEmptyMediaFoldersTaskTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly Mock<ILogger<CleanEmptyMediaFoldersTask>> _loggerMock;
    private readonly CleanEmptyMediaFoldersTask _task;

    public CleanEmptyMediaFoldersTaskTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _fileSystemMock = new Mock<IFileSystem>();
        _loggerMock = new Mock<ILogger<CleanEmptyMediaFoldersTask>>();
        _task = new CleanEmptyMediaFoldersTask(_libraryManagerMock.Object, _fileSystemMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithOnlyMetadata_DeletesFolder()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));

        SetupFiles(movieDir, "movie.nfo", "poster.jpg");
        SetupSubDirs(movieDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TopLevelFolderWithVideoFile_IsKept()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Good Movie (2021)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Good Movie (2021)", movieDir));

        SetupFiles(movieDir, "movie.mkv", "movie.nfo", "poster.jpg");
        SetupSubDirs(movieDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CompletelyEmptyFolder_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Upcoming Movie (2025)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Upcoming Movie (2025)", movieDir));

        SetupFiles(movieDir);
        SetupSubDirs(movieDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_TrickplayFolder_IsSkipped()
    {
        const string libraryPath = "/media/movies";
        const string trickplayDir = "/media/movies/Movie.trickplay";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Movie.trickplay", trickplayDir));

        SetupFiles(trickplayDir, "index.json", "00001.jpg");
        SetupSubDirs(trickplayDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DryRun_LogsWouldDeleteWithoutDeleting()
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/Old Movie (2020)";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Old Movie (2020)", movieDir));

        SetupFiles(movieDir, "movie.nfo");
        SetupSubDirs(movieDir);

        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete empty media folder", LogLevel.Information);
        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithVideoInSeason_EntireFolderIsKept()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Quantum Donuts (2018)";
        const string season1Dir = "/media/tv/Quantum Donuts (2018)/Season 01";
        const string season2Dir = "/media/tv/Quantum Donuts (2018)/Season 02";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Quantum Donuts (2018)", showDir));

        SetupFiles(showDir, "tvshow.nfo");
        SetupSubDirs(showDir,
            ("Season 01", season1Dir),
            ("Season 02", season2Dir));

        SetupFiles(season1Dir, "S01E01.mkv", "season.nfo");
        SetupSubDirs(season1Dir);

        SetupFiles(season2Dir, "season.nfo");
        SetupSubDirs(season2Dir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithNoVideoAnywhere_IsDeleted()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Cancelled Show (2019)";
        const string season1Dir = "/media/tv/Cancelled Show (2019)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Cancelled Show (2019)", showDir));

        SetupFiles(showDir, "tvshow.nfo", "poster.jpg");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupSubDirs(season1Dir);

        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithDeeplyNestedVideo_IsKept()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Deep Show (2020)";
        const string season1Dir = "/media/tv/Deep Show (2020)/Season 01";
        const string extrasDir = "/media/tv/Deep Show (2020)/Season 01/Extras";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Deep Show (2020)", showDir));

        SetupFiles(showDir, "tvshow.nfo");
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir, "season.nfo");
        SetupSubDirs(season1Dir, ("Extras", extrasDir));

        SetupFiles(extrasDir, "behind-the-scenes.mkv");
        SetupSubDirs(extrasDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MultipleOrphanedFolders_DeletesAllAndReportsCount()
    {
        const string libraryPath = "/media/movies";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Old Movie 1 (2018)", "/media/movies/Old Movie 1 (2018)"),
            ("Old Movie 2 (2019)", "/media/movies/Old Movie 2 (2019)"));

        SetupFiles("/media/movies/Old Movie 1 (2018)", "movie.nfo");
        SetupSubDirs("/media/movies/Old Movie 1 (2018)");

        SetupFiles("/media/movies/Old Movie 2 (2019)", "movie.nfo", "poster.jpg");
        SetupSubDirs("/media/movies/Old Movie 2 (2019)");

        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Would have deleted 2 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_NoLibraryFolders_CompletesWithoutError()
    {
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([]);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Deleted 0 folders", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_CancellationRequested_StopsProcessing()
    {
        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath1, ("Movie", "/media/movies1/Movie"));
        SetupFiles("/media/movies1/Movie", "movie.nfo");
        SetupSubDirs("/media/movies1/Movie");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await _task.ExecuteInternalAsync(false, new Progress<double>(), cts.Token);

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath2, false), Times.Never);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DirectoryScanError_LogsErrorAndContinues()
    {
        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath1, false)).Throws(new IOException("Access denied"));

        SetupTopLevelDirs(libraryPath2, ("Old Movie", "/media/movies2/Old Movie"));
        SetupFiles("/media/movies2/Old Movie", "movie.nfo");
        SetupSubDirs("/media/movies2/Old Movie");

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("Error scanning directory", LogLevel.Error);
        VerifyLogContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ProgressIsReported()
    {
        const string libraryPath1 = "/media/movies1";
        const string libraryPath2 = "/media/movies2";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath1] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath2] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath1);
        SetupTopLevelDirs(libraryPath2);

        var reportedValues = new List<double>();
        var progress = new SynchronousProgress<double>(v => reportedValues.Add(v));

        await _task.ExecuteInternalAsync(false, progress, CancellationToken.None);

        Assert.Equal(2, reportedValues.Count);
        Assert.Equal(50, reportedValues[0]);
        Assert.Equal(100, reportedValues[1]);
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".avi")]
    [InlineData(".m4v")]
    [InlineData(".ts")]
    [InlineData(".iso")]
    [InlineData(".MKV")]
    [InlineData(".Mp4")]
    public async Task ExecuteInternalAsync_VariousVideoExtensions_FolderIsKept(string extension)
    {
        const string libraryPath = "/media/movies";
        const string movieDir = "/media/movies/SomeMovie";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("SomeMovie", movieDir));

        SetupFilesWithFullNames(movieDir, "/media/movies/SomeMovie/video" + extension);
        SetupSubDirs(movieDir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_DuplicateLibraryPaths_ScansOnlyOnce()
    {
        const string libraryPath = "/media/movies";

        var virtualFolder1 = new VirtualFolderInfo { Locations = [libraryPath] };
        var virtualFolder2 = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder1, virtualFolder2]);

        SetupTopLevelDirs(libraryPath);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        _fileSystemMock.Verify(f => f.GetDirectories(libraryPath, false), Times.Once);
    }

    [Fact]
    public async Task ExecuteInternalAsync_ShowWithEmptySubdirsOnly_IsSkipped()
    {
        const string libraryPath = "/media/tv";
        const string showDir = "/media/tv/Future Show (2026)";
        const string season1Dir = "/media/tv/Future Show (2026)/Season 01";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath, ("Future Show (2026)", showDir));

        SetupFiles(showDir);
        SetupSubDirs(showDir, ("Season 01", season1Dir));

        SetupFiles(season1Dir);
        SetupSubDirs(season1Dir);

        await _task.ExecuteInternalAsync(false, new Progress<double>(), CancellationToken.None);

        VerifyLogNeverContains("Deleting empty media folder", LogLevel.Information);
    }

    [Fact]
    public async Task ExecuteInternalAsync_MixedFolders_OnlyOrphanedOnesAreDeleted()
    {
        const string libraryPath = "/media/movies";

        SetupLibrary(libraryPath);
        SetupTopLevelDirs(libraryPath,
            ("Good Movie (2021)", "/media/movies/Good Movie (2021)"),
            ("Orphaned Movie (2019)", "/media/movies/Orphaned Movie (2019)"),
            ("Another Good (2020)", "/media/movies/Another Good (2020)"));

        SetupFiles("/media/movies/Good Movie (2021)", "movie.mkv", "movie.nfo");
        SetupSubDirs("/media/movies/Good Movie (2021)");

        SetupFiles("/media/movies/Orphaned Movie (2019)", "movie.nfo", "poster.jpg");
        SetupSubDirs("/media/movies/Orphaned Movie (2019)");

        SetupFiles("/media/movies/Another Good (2020)", "film.mp4");
        SetupSubDirs("/media/movies/Another Good (2020)");

        await _task.ExecuteInternalAsync(true, new Progress<double>(), CancellationToken.None);

        VerifyLogContains("[Dry Run] Would delete empty media folder: /media/movies/Orphaned Movie (2019)", LogLevel.Information);
        VerifyLogContains("Would have deleted 1 folders", LogLevel.Information);
    }

    // ========== Helper methods ==========

    private void SetupLibrary(string libraryPath)
    {
        var virtualFolder = new VirtualFolderInfo { Locations = [libraryPath] };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);
    }

    private void SetupTopLevelDirs(string parentPath, params (string Name, string FullName)[] dirs)
    {
        var dirMetadata = dirs.Select(d => new FileSystemMetadata
        {
            FullName = d.FullName,
            Name = d.Name,
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(parentPath, false)).Returns(dirMetadata);
    }

    private void SetupSubDirs(string parentPath, params (string Name, string FullName)[] dirs)
    {
        var dirMetadata = dirs.Select(d => new FileSystemMetadata
        {
            FullName = d.FullName,
            Name = d.Name,
            IsDirectory = true
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetDirectories(parentPath, false)).Returns(dirMetadata);
    }

    private void SetupFiles(string dirPath, params string[] fileNames)
    {
        var files = fileNames.Select(name => new FileSystemMetadata
        {
            FullName = dirPath + "/" + name,
            IsDirectory = false
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath, false)).Returns(files);
    }

    private void SetupFilesWithFullNames(string dirPath, params string[] fullNames)
    {
        var files = fullNames.Select(name => new FileSystemMetadata
        {
            FullName = name,
            IsDirectory = false
        }).ToArray();

        _fileSystemMock.Setup(f => f.GetFiles(dirPath, false)).Returns(files);
    }

    /// <summary>
    /// A synchronous implementation of IProgress that invokes the callback immediately.
    /// Unlike Progress&lt;T&gt;, this does not post to a SynchronizationContext.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    private void VerifyLogContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private void VerifyLogNeverContains(string messagePart, LogLevel level)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(messagePart)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}