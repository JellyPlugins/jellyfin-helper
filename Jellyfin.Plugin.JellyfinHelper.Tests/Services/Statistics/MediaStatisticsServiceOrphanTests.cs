using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class MediaStatisticsServiceOrphanTests
{
    private readonly Mock<MediaBrowser.Controller.Library.ILibraryManager> _libraryManager = TestMockFactory.CreateLibraryManager();
    private readonly Mock<IFileSystem> _fileSystem = TestMockFactory.CreateFileSystem();
    private readonly PluginConfiguration _config = new();
    private readonly Mock<IArrOrphanProtectionService> _arrProtection = new();
    private readonly MediaStatisticsService _service;
    private readonly string _root = Path.GetFullPath(Path.Join("media", "movies"));

    public MediaStatisticsServiceOrphanTests()
    {
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        var configHelper = TestMockFactory.CreateCleanupConfigHelper(_config);
        _arrProtection.Setup(a => a.GetSnapshotAsync(_config, It.IsAny<IReadOnlyList<VirtualFolderInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrphanProtectionSnapshot.Empty);
        _service = new MediaStatisticsService(
            _libraryManager.Object,
            _fileSystem.Object,
            TestMockFactory.CreatePluginLogService(),
            TestMockFactory.CreateLogger<MediaStatisticsService>().Object,
            configHelper.Object,
            arrOrphanProtection: _arrProtection.Object);
        SetLibrary(CollectionTypeOptions.movies);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.movies)]
    [InlineData(CollectionTypeOptions.tvshows)]
    [InlineData(CollectionTypeOptions.homevideos)]
    [InlineData(CollectionTypeOptions.musicvideos)]
    [InlineData(null)]
    public void NestedVideo_ProtectsEveryAncestorButNotOrphanSibling(CollectionTypeOptions? type)
    {
        SetLibrary(type);
        var group = Path.Join(_root, "Group");
        var nested = Path.Join(group, "Nested");
        var movie = Path.Join(nested, "Movie");
        var orphan = Path.Join(group, "Orphan");
        DefineDirectory(_root, [group], "poster.jpg");
        DefineDirectory(group, [nested, orphan], "group.nfo");
        DefineDirectory(nested, [movie], "poster.jpg");
        DefineDirectory(movie, [], "movie.MKV");
        DefineDirectory(orphan, [], "movie.nfo", "movie.srt");

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Equal([orphan], stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.OrphanedMetadataDirectories);
        Assert.Equal(1, stats.VideoFileCount);
        Assert.Equal(100, stats.VideoSize);
        Assert.Equal(2, stats.ImageFileCount);
        Assert.Equal(2, stats.NfoFileCount);
        Assert.Equal(1, stats.SubtitleFileCount);
        Assert.Equal(1, stats.VideosWithoutImages);
        Assert.Equal(1, stats.VideosWithoutNfo);
    }

    [Theory]
    [InlineData("track.FLAC", false)]
    [InlineData("track.mp3", true)]
    [InlineData("movie.strm", true)]
    public void AudioAndStreamLinks_ProtectMetadataDirectories(string mediaFile, bool nested)
    {
        var child = Path.Join(_root, "Child");
        DefineDirectory(_root, nested ? [child] : [], "poster.jpg", "movie.nfo", "movie.srt");
        if (nested)
        {
            DefineDirectory(child, [], mediaFile);
        }
        else
        {
            DefineDirectory(_root, [], "poster.jpg", "movie.nfo", "movie.srt", mediaFile);
        }

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(0, stats.OrphanedMetadataDirectories);
        Assert.Equal(1, stats.AudioFileCount + stats.VideoFileCount);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("movie.nfo", false)]
    [InlineData("poster.jpg", false)]
    [InlineData("movie.xml", true)]
    [InlineData("poster.webp", true)]
    [InlineData("photo.heic", true)]
    public void EmptyAndMetadataOnlyTrees_AreProtectedPlaceholders(string fileName, bool nested)
    {
        var child = Path.Join(_root, "Wanted");
        DefineDirectory(_root, nested ? [child] : [], fileName.Length == 0 ? [] : [fileName]);
        if (nested)
        {
            DefineDirectory(child, [], "season.nfo", "poster.jpg");
        }

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(0, stats.OrphanedMetadataDirectories);
    }

    [Fact]
    public void NestedSubtitle_PreventsIncorrectPlaceholderClassification()
    {
        var child = Path.Join(_root, "OldMovie");
        DefineDirectory(_root, [child], "movie.nfo");
        DefineDirectory(child, [], "movie.srt");

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Equal(2, stats.OrphanedMetadataDirectories);
        Assert.Contains(_root, stats.OrphanedMetadataDirectoriesPaths);
        Assert.Contains(child, stats.OrphanedMetadataDirectoriesPaths);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.music, "Movies", false)]
    [InlineData(CollectionTypeOptions.books, "Movies", false)]
    [InlineData(CollectionTypeOptions.boxsets, "Movies", false)]
    [InlineData(CollectionTypeOptions.movies, "My Collection", false)]
    [InlineData(CollectionTypeOptions.movies, "My Boxset", false)]
    [InlineData(CollectionTypeOptions.movies, "Movies", true)]
    public void ExcludedLibraries_KeepStatisticsWithoutOrphanVerdicts(CollectionTypeOptions type, string name, bool userExcluded)
    {
        SetLibrary(type, name);
        if (userExcluded)
        {
            _config.ExcludedLibraries = " movies , Other Library ";
        }

        DefineDirectory(_root, [], "movie.nfo", "movie.srt");

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.NfoFileCount);
        Assert.Equal(1, stats.SubtitleFileCount);
        Assert.Equal(200, stats.TotalSize);
    }

    [Theory]
    [InlineData("collections")]
    [InlineData("Movies [boxset]")]
    [InlineData("Movies [collection]")]
    public void InternalCollectionDirectories_AreNotOrphanCandidates(string directoryName)
    {
        var child = Path.Join(_root, directoryName);
        DefineDirectory(_root, [child]);
        DefineDirectory(child, [], "movie.nfo", "movie.srt");

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.SubtitleFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableChild_DoesNotProveParentIsOrphaned(bool directoryListing)
    {
        var child = Path.Join(_root, "Unreadable");
        DefineDirectory(_root, [child], "movie.nfo", "movie.srt");
        DefineDirectory(child, []);
        if (directoryListing)
        {
            _fileSystem.Setup(f => f.GetDirectories(child)).Throws(new UnauthorizedAccessException());
        }
        else
        {
            _fileSystem.Setup(f => f.GetFiles(child)).Throws(new IOException());
        }

        Assert.Empty(Assert.Single(_service.CalculateStatistics().Libraries).OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void RegisteredMediaWithUnknownExtension_ProtectsAncestorsWithoutChangingFileClassification()
    {
        var child = Path.Join(_root, "Movie");
        var path = Path.Join(child, "movie.custom");
        DefineDirectory(_root, [child], "movie.nfo", "movie.srt");
        DefineDirectory(child, [], "movie.custom", "poster.jpg");
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([new Video { Path = path }]);

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.OtherFileCount);
        Assert.Equal(0, stats.VideoFileCount);
    }

    [Theory]
    [InlineData(".jellyfin-helper-trash")]
    [InlineData("movie.trickplay")]
    public void MediaInSkippedSubtrees_DoesNotProtectUnrelatedOrphans(string childName)
    {
        var child = Path.Join(_root, childName);
        _config.TrashFolderPath = ".jellyfin-helper-trash";
        DefineDirectory(_root, [child], "movie.nfo", "movie.srt");
        DefineDirectory(child, [], "movie.mkv");
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([new Video { Path = Path.Join(child, "movie.mkv") }]);

        Assert.Equal([_root], Assert.Single(_service.CalculateStatistics().Libraries).OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public async Task AsyncBaseline_WithoutConfiguredArr_StillDetectsOnlyTheRealOrphan()
    {
        DefineDirectory(_root, [], "movie.nfo", "movie.srt");

        var stats = Assert.Single((await _service.CalculateStatisticsAsync(TestContext.Current.CancellationToken)).Libraries);

        Assert.Equal([_root], stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(200, stats.TotalSize);
    }

    [Fact]
    public async Task OptionalArrProtection_SupplementsButDoesNotReplaceTheBaseline()
    {
        var wanted = Path.Join(_root, "Wanted");
        var orphan = Path.Join(_root, "Orphan");
        DefineDirectory(_root, [wanted, orphan]);
        DefineDirectory(wanted, [], "movie.nfo", "movie.srt");
        DefineDirectory(orphan, [], "movie.nfo", "movie.srt");
        _arrProtection.Setup(a => a.GetSnapshotAsync(_config, It.IsAny<IReadOnlyList<VirtualFolderInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanProtectionSnapshot([wanted], []));

        var stats = Assert.Single((await _service.CalculateStatisticsAsync(TestContext.Current.CancellationToken)).Libraries);

        Assert.Equal([orphan], stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(400, stats.TotalSize);
        _arrProtection.Verify(a => a.GetSnapshotAsync(_config, It.IsAny<IReadOnlyList<VirtualFolderInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OptionalArrFailure_DoesNotSuppressFileStatistics()
    {
        DefineDirectory(_root, [], "movie.nfo", "movie.srt");
        _arrProtection.Setup(a => a.GetSnapshotAsync(_config, It.IsAny<IReadOnlyList<VirtualFolderInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanProtectionSnapshot([], [_root]));

        var stats = Assert.Single((await _service.CalculateStatisticsAsync(TestContext.Current.CancellationToken)).Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(200, stats.TotalSize);
    }

    [Fact]
    public async Task UncertainNestedRoot_ProtectsAncestorsButDoesNotHideAnUnrelatedSibling()
    {
        var uncertain = Path.Join(_root, "Uncertain");
        var orphan = Path.Join(_root, "Orphan");
        DefineDirectory(_root, [uncertain, orphan], "movie.nfo", "movie.srt");
        DefineDirectory(uncertain, [], "movie.nfo", "movie.srt");
        DefineDirectory(orphan, [], "movie.nfo", "movie.srt");
        _arrProtection.Setup(a => a.GetSnapshotAsync(_config, It.IsAny<IReadOnlyList<VirtualFolderInfo>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrphanProtectionSnapshot([], [uncertain]));

        Assert.Equal([orphan], Assert.Single((await _service.CalculateStatisticsAsync(TestContext.Current.CancellationToken)).Libraries).OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void UnavailableDatabase_DoesNotProveMediaAbsence()
    {
        DefineDirectory(_root, [], "movie.nfo", "movie.srt");
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException());

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(200, stats.TotalSize);
    }

    [Fact]
    public void RegisteredMediaNotVisibleOnDisk_StillProtectsTheContainingTree()
    {
        DefineDirectory(_root, [], "movie.nfo", "movie.srt");
        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([new Video { Path = Path.Join(_root, "Hidden", "movie.mkv") }]);

        Assert.Empty(Assert.Single(_service.CalculateStatistics().Libraries).OrphanedMetadataDirectoriesPaths);
    }

    [Fact]
    public void DuplicateChild_UsesItsAnalysisWithoutDoubleCounting()
    {
        var child = Path.Join(_root, "Movie");
        DefineDirectory(_root, [child, child], "movie.nfo", "movie.srt");
        DefineDirectory(child, [], "movie.mkv");

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(1, stats.VideoFileCount);
    }

    [Fact]
    public void DirectoryCycle_IsUnprovenAndDoesNotDoubleCount()
    {
        var child = Path.Join(_root, "Cycle");
        DefineDirectory(_root, [child], "movie.nfo", "movie.srt");
        DefineDirectory(child, [_root]);

        var stats = Assert.Single(_service.CalculateStatistics().Libraries);

        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(200, stats.TotalSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedLink_IsNotEvidenceOfAnOrphan(bool fileEntry)
    {
        var linked = Path.Join(_root, fileEntry ? "linked.txt" : "Linked");
        DefineDirectory(_root, fileEntry ? [] : [linked], "movie.nfo", "movie.srt");
        if (fileEntry)
        {
            DefineDirectory(_root, [], "movie.nfo", "movie.srt", "linked.txt");
        }
        else
        {
            DefineDirectory(linked, [], "movie.mkv");
        }

        var service = new LinkAwareService(_libraryManager.Object, _fileSystem.Object, TestMockFactory.CreateCleanupConfigHelper(_config).Object, linked);

        var stats = Assert.Single(service.CalculateStatistics().Libraries);
        Assert.Empty(stats.OrphanedMetadataDirectoriesPaths);
        Assert.Equal(fileEntry ? 0 : 1, stats.VideoFileCount);
    }

    [Fact]
    public async Task CancelledScan_DoesNotQueryOrScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CalculateStatisticsAsync(cancellation.Token));
        Assert.Empty(_arrProtection.Invocations);
        Assert.Empty(_fileSystem.Invocations);
    }

    private sealed class LinkAwareService(
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ICleanupConfigHelper configHelper,
        string linkedPath)
        : MediaStatisticsService(libraryManager, fileSystem, TestMockFactory.CreatePluginLogService(), TestMockFactory.CreateLogger<MediaStatisticsService>().Object, configHelper)
    {
        internal override bool IsReparsePoint(string path) => path == linkedPath;
    }

    private void SetLibrary(CollectionTypeOptions? type, string name = "Movies")
    {
        _libraryManager.Setup(m => m.GetVirtualFolders()).Returns([
            new VirtualFolderInfo { Name = name, CollectionType = type, Locations = [_root] }
        ]);
    }

    private void DefineDirectory(string path, string[] children, params string[] files)
    {
        _fileSystem.Setup(f => f.GetFiles(path)).Returns(files.Select(name => new FileSystemMetadata
        {
            FullName = Path.Join(path, name), Name = name, Length = 100, IsDirectory = false
        }).ToArray());
        _fileSystem.Setup(f => f.GetDirectories(path)).Returns(children.Select(child => new FileSystemMetadata
        {
            FullName = child, Name = Path.GetFileName(child), IsDirectory = true
        }).ToArray());
    }
}
