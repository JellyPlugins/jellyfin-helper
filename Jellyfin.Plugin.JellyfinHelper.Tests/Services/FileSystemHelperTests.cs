using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
/// Tests for <see cref="FileSystemHelper"/>.
/// </summary>
public class FileSystemHelperTests
{
    private readonly Mock<IFileSystem> _fileSystemMock = TestMockFactory.CreateFileSystem();
    private readonly Mock<ILogger> _loggerMock = TestMockFactory.CreateLogger();

    // ===== CalculateDirectorySize Tests =====

    [Fact]
    public void CalculateDirectorySize_EmptyDirectory_ReturnsZero()
    {
        _fileSystemMock
            .Setup(fs => fs.GetFiles(It.IsAny<string>(), false))
            .Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock
            .Setup(fs => fs.GetDirectories(It.IsAny<string>(), false))
            .Returns(Array.Empty<FileSystemMetadata>());

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/test/empty", _loggerMock.Object);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateDirectorySize_WithFiles_ReturnsTotalSize()
    {
        var files = new[]
        {
            TestDataGenerator.CreateFile("/test/dir/file1.mkv", 1000),
            TestDataGenerator.CreateFile("/test/dir/file2.srt", 500),
            TestDataGenerator.CreateFile("/test/dir/file3.nfo", 200),
        };

        _fileSystemMock
            .Setup(fs => fs.GetFiles("/test/dir", false))
            .Returns(files);
        _fileSystemMock
            .Setup(fs => fs.GetDirectories("/test/dir", false))
            .Returns(Array.Empty<FileSystemMetadata>());

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/test/dir", _loggerMock.Object);

        Assert.Equal(1700, result);
    }

    [Fact]
    public void CalculateDirectorySize_WithSubDirectories_SumsRecursively()
    {
        var rootFiles = new[] { TestDataGenerator.CreateFile("/test/root/file.mkv", 1000) };
        var subDirs = new[] { TestDataGenerator.CreateDirectory("/test/root/sub1") };
        var subFiles = new[]
        {
            TestDataGenerator.CreateFile("/test/root/sub1/file2.mkv", 2000),
            TestDataGenerator.CreateFile("/test/root/sub1/file3.srt", 300),
        };

        _fileSystemMock.Setup(fs => fs.GetFiles("/test/root", false)).Returns(rootFiles);
        _fileSystemMock.Setup(fs => fs.GetDirectories("/test/root", false)).Returns(subDirs);
        _fileSystemMock.Setup(fs => fs.GetFiles("/test/root/sub1", false)).Returns(subFiles);
        _fileSystemMock.Setup(fs => fs.GetDirectories("/test/root/sub1", false)).Returns(Array.Empty<FileSystemMetadata>());

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/test/root", _loggerMock.Object);

        Assert.Equal(3300, result);
    }

    [Fact]
    public void CalculateDirectorySize_DeeplyNested_SumsAllLevels()
    {
        _fileSystemMock.Setup(fs => fs.GetFiles("/root", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/a.mkv", 100) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root", false))
            .Returns(new[] { TestDataGenerator.CreateDirectory("/root/sub") });

        _fileSystemMock.Setup(fs => fs.GetFiles("/root/sub", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/sub/b.mkv", 200) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root/sub", false))
            .Returns(new[] { TestDataGenerator.CreateDirectory("/root/sub/subsub") });

        _fileSystemMock.Setup(fs => fs.GetFiles("/root/sub/subsub", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/sub/subsub/c.mkv", 300) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root/sub/subsub", false))
            .Returns(Array.Empty<FileSystemMetadata>());

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/root", _loggerMock.Object);

        Assert.Equal(600, result);
    }

    [Fact]
    public void CalculateDirectorySize_IoExceptionOnSubDir_SkipsAndContinues()
    {
        var rootFiles = new[] { TestDataGenerator.CreateFile("/test/root/file.mkv", 500) };
        var subDirs = new[] { TestDataGenerator.CreateDirectory("/test/root/inaccessible") };

        _fileSystemMock.Setup(fs => fs.GetFiles("/test/root", false)).Returns(rootFiles);
        _fileSystemMock.Setup(fs => fs.GetDirectories("/test/root", false)).Returns(subDirs);

        _fileSystemMock.Setup(fs => fs.GetFiles("/test/root/inaccessible", false))
            .Throws(new IOException("Access denied"));

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/test/root", _loggerMock.Object);

        Assert.Equal(500, result);
    }

    [Fact]
    public void CalculateDirectorySize_UnauthorizedAccessOnRoot_ReturnsZero()
    {
        _fileSystemMock.Setup(fs => fs.GetFiles("/test/locked", false))
            .Throws(new UnauthorizedAccessException("No permission"));

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/test/locked", _loggerMock.Object);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateDirectorySize_MultipleSubDirectories_SumsAll()
    {
        _fileSystemMock.Setup(fs => fs.GetFiles("/root", false))
            .Returns(Array.Empty<FileSystemMetadata>());
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root", false))
            .Returns(new[]
            {
                TestDataGenerator.CreateDirectory("/root/a"),
                TestDataGenerator.CreateDirectory("/root/b"),
                TestDataGenerator.CreateDirectory("/root/c"),
            });

        _fileSystemMock.Setup(fs => fs.GetFiles("/root/a", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/a/1.mkv", 100) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root/a", false))
            .Returns(Array.Empty<FileSystemMetadata>());

        _fileSystemMock.Setup(fs => fs.GetFiles("/root/b", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/b/2.mkv", 200) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root/b", false))
            .Returns(Array.Empty<FileSystemMetadata>());

        _fileSystemMock.Setup(fs => fs.GetFiles("/root/c", false))
            .Returns(new[] { TestDataGenerator.CreateFile("/root/c/3.mkv", 300) });
        _fileSystemMock.Setup(fs => fs.GetDirectories("/root/c", false))
            .Returns(Array.Empty<FileSystemMetadata>());

        var result = FileSystemHelper.CalculateDirectorySize(_fileSystemMock.Object, "/root", _loggerMock.Object);

        Assert.Equal(600, result);
    }

    // ===== IncrementCount Tests =====

    [Fact]
    public void IncrementCount_NewKey_SetsToOne()
    {
        var dict = new Dictionary<string, int>();
        FileSystemHelper.IncrementCount(dict, "HEVC");
        Assert.Equal(1, dict["HEVC"]);
    }

    [Fact]
    public void IncrementCount_ExistingKey_IncrementsValue()
    {
        var dict = new Dictionary<string, int> { { "HEVC", 5 } };
        FileSystemHelper.IncrementCount(dict, "HEVC");
        Assert.Equal(6, dict["HEVC"]);
    }

    [Fact]
    public void IncrementCount_MultipleCalls_AccumulatesCorrectly()
    {
        var dict = new Dictionary<string, int>();
        FileSystemHelper.IncrementCount(dict, "H264");
        FileSystemHelper.IncrementCount(dict, "H264");
        FileSystemHelper.IncrementCount(dict, "H264");
        Assert.Equal(3, dict["H264"]);
    }

    // ===== AccumulateValue Tests =====

    [Fact]
    public void AccumulateValue_NewKey_SetsInitialValue()
    {
        var dict = new Dictionary<string, long>();
        FileSystemHelper.AccumulateValue(dict, "MKV", 1024);
        Assert.Equal(1024, dict["MKV"]);
    }

    [Fact]
    public void AccumulateValue_ExistingKey_AddsToExisting()
    {
        var dict = new Dictionary<string, long> { { "MKV", 1000 } };
        FileSystemHelper.AccumulateValue(dict, "MKV", 500);
        Assert.Equal(1500, dict["MKV"]);
    }

    // ===== AddPath Tests =====

    [Fact]
    public void AddPath_NewKey_CreatesCollectionWithPath()
    {
        var dict = new Dictionary<string, Collection<string>>();
        FileSystemHelper.AddPath(dict, "HEVC", "/media/movie.mkv");

        Assert.True(dict.TryGetValue("HEVC", out var paths));
        Assert.Single(paths);
        Assert.Equal("/media/movie.mkv", paths[0]);
    }

    [Fact]
    public void AddPath_ExistingKey_AppendsToCollection()
    {
        var dict = new Dictionary<string, Collection<string>>();
        FileSystemHelper.AddPath(dict, "HEVC", "/media/movie1.mkv");
        FileSystemHelper.AddPath(dict, "HEVC", "/media/movie2.mkv");

        Assert.Equal(2, dict["HEVC"].Count);
        Assert.Contains("/media/movie1.mkv", dict["HEVC"]);
        Assert.Contains("/media/movie2.mkv", dict["HEVC"]);
    }

    [Fact]
    public void AddPath_MultipleDifferentKeys_CreatesSeparateCollections()
    {
        var dict = new Dictionary<string, Collection<string>>();
        FileSystemHelper.AddPath(dict, "HEVC", "/media/hevc.mkv");
        FileSystemHelper.AddPath(dict, "H264", "/media/h264.mp4");

        Assert.Equal(2, dict.Count);
        Assert.Single(dict["HEVC"]);
        Assert.Single(dict["H264"]);
    }
}
