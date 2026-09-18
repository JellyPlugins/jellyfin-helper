using System.IO;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
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

public class MediaStatisticsServiceLanguageTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IFileSystem> _fileSystemMock;
    private readonly TestableMediaStatisticsService _service;

    public MediaStatisticsServiceLanguageTests()
    {
        _libraryManagerMock = TestMockFactory.CreateLibraryManager();
        _fileSystemMock = TestMockFactory.CreateFileSystem();
        var loggerMock = TestMockFactory.CreateLogger<MediaStatisticsService>();
        var configHelperMock = TestMockFactory.CreateCleanupConfigHelper();
        _service = new TestableMediaStatisticsService(_libraryManagerMock.Object, _fileSystemMock.Object, TestMockFactory.CreatePluginLogService(), loggerMock.Object, configHelperMock.Object);
    }

    private static string TestPath(params string[] segments) => Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);

    private void SetupLibraryWithVideo(string filePath, List<MediaStream> streams, long fileSize = 1_000_000_000)
    {
        var libraryPath = TestPath("media", "movies");
        var virtualFolder = new VirtualFolderInfo
        {
            Name = "Movies",
            CollectionType = CollectionTypeOptions.movies,
            Locations = [libraryPath]
        };
        _libraryManagerMock.Setup(m => m.GetVirtualFolders()).Returns([virtualFolder]);

        var file = new FileSystemMetadata
        {
            FullName = filePath,
            Name = Path.GetFileName(filePath),
            Length = fileSize,
            IsDirectory = false
        };
        _fileSystemMock.Setup(f => f.GetFiles(libraryPath)).Returns([file]);
        _fileSystemMock.Setup(f => f.GetDirectories(libraryPath)).Returns([]);

        var mockItem = new Mock<BaseItem>();
        mockItem.Object.Path = filePath;
        mockItem.Setup(i => i.GetMediaStreams()).Returns(streams);
        _service.SetItemLookup(filePath, mockItem.Object);
    }

    [Fact]
    public void AudioLanguage_SingleTrack_RecordsLanguage()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Equal(1_000_000_000, stats.AudioLanguageSizes["English"]);
        Assert.Contains(path, stats.AudioLanguagePaths["English"]);
    }

    [Fact]
    public void AudioLanguage_TwoTracksSameLanguage_CountsFileOnce()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "eng" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Single(stats.AudioLanguagePaths["English"]);
    }

    [Fact]
    public void AudioLanguage_TwoDistinctLanguages_RecordsBoth()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "eng" },
            new() { Type = MediaStreamType.Audio, Codec = "ac3", Language = "ger" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["English"]);
        Assert.Equal(1, stats.AudioLanguages["German"]);
    }

    [Fact]
    public void AudioLanguage_NullLanguageStream_IsSkipped()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = null }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Empty(stats.AudioLanguages);
    }

    [Fact]
    public void AudioLanguage_UndLanguage_IsSkipped()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "und" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Empty(stats.AudioLanguages);
    }

    [Fact]
    public void AudioLanguage_UnknownIso639Code_RecordedAsUpperCaseCode()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Audio, Codec = "aac", Language = "xyz" }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.AudioLanguages["XYZ"]);
    }

    [Fact]
    public void SubtitleLanguage_EmbeddedTrack_IsRecorded()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng", IsExternal = false }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Equal(1, stats.SubtitleLanguages["English"]);
    }

    [Fact]
    public void SubtitleLanguage_ExternalTrack_IsSkipped()
    {
        var path = TestPath("media", "movies", "Film.mkv");
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitRate = 5_000_000 },
            new() { Type = MediaStreamType.Subtitle, Codec = "srt", Language = "eng", IsExternal = true }
        };
        SetupLibraryWithVideo(path, streams);
        var result = _service.CalculateStatistics();
        var stats = result.Libraries[0];
        Assert.Empty(stats.SubtitleLanguages);
    }

    [Fact]
    public void NormalizeIso639Language_KnownCodes_ReturnDisplayName()
    {
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("deu"));
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("ger"));
        Assert.Equal("German", MediaStatisticsService.NormalizeIso639Language("de"));
        Assert.Equal("English", MediaStatisticsService.NormalizeIso639Language("eng"));
    }

    [Fact]
    public void NormalizeIso639Language_UnknownCode_ReturnsUpperCasedCode()
        => Assert.Equal("XYZ", MediaStatisticsService.NormalizeIso639Language("xyz"));

    [Fact]
    public void NormalizeIso639Language_NullInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(null));

    [Fact]
    public void NormalizeIso639Language_EmptyInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(""));

    [Fact]
    public void NormalizeIso639Language_WhitespaceInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language("   "));

    [Fact]
    public void NormalizeIso639Language_UndInput_ReturnsNull()
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language("und"));

    [Theory]
    [InlineData("mis")]
    [InlineData("mul")]
    [InlineData("zxx")]
    [InlineData("MIS")]
    public void NormalizeIso639Language_UncodedCodes_ReturnNull(string code)
        => Assert.Null(MediaStatisticsService.NormalizeIso639Language(code));

    [Theory]
    [InlineData("en-US", "English")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("de-DE", "German")]
    [InlineData("en_us", "English")]
    public void NormalizeIso639Language_RegionSubtag_StrippedToBase(string code, string expected)
        => Assert.Equal(expected, MediaStatisticsService.NormalizeIso639Language(code));

    private sealed class TestableMediaStatisticsService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        Jellyfin.Plugin.JellyfinHelper.Services.PluginLog.IPluginLogService pluginLog,
        ILogger<MediaStatisticsService> logger,
        ICleanupConfigHelper configHelper)
        : MediaStatisticsService(libraryManager, fileSystem, pluginLog, logger, configHelper)
    {
        private readonly Dictionary<string, BaseItem> _itemLookup = new(StringComparer.OrdinalIgnoreCase);
        public void SetItemLookup(string filePath, BaseItem item) => _itemLookup[filePath] = item;
        internal override Dictionary<string, BaseItem> BuildItemLookup() => new(_itemLookup, StringComparer.OrdinalIgnoreCase);
    }
}
