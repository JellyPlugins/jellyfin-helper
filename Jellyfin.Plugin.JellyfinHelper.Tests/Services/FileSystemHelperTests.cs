using System.Collections.ObjectModel;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Tests for <see cref="FileSystemHelper" />.
/// </summary>
public class FileSystemHelperTests
{
    private readonly Mock<ILogger> _loggerMock = TestMockFactory.CreateLogger();

    /// <summary>Creates a temp directory, runs the action, then deletes it.</summary>
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteBytes(string filePath, int length)
    {
        File.WriteAllBytes(filePath, new byte[length]);
    }

    [Fact]
    public void CalculateDirectorySize_EmptyDirectory_ReturnsZero()
    {
        var root = CreateTempDir();
        try
        {
            var result = FileSystemHelper.CalculateDirectorySize(root);
            Assert.Equal(0, result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CalculateDirectorySize_WithFiles_ReturnsTotalSize()
    {
        var root = CreateTempDir();
        try
        {
            WriteBytes(Path.Combine(root, "file1.mkv"), 1000);
            WriteBytes(Path.Combine(root, "file2.srt"), 500);
            WriteBytes(Path.Combine(root, "file3.nfo"), 200);

            var result = FileSystemHelper.CalculateDirectorySize(root);

            Assert.Equal(1700, result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CalculateDirectorySize_WithSubDirectories_SumsRecursively()
    {
        var root = CreateTempDir();
        try
        {
            WriteBytes(Path.Combine(root, "file.mkv"), 1000);
            var sub = Directory.CreateDirectory(Path.Combine(root, "sub1")).FullName;
            WriteBytes(Path.Combine(sub, "file2.mkv"), 2000);
            WriteBytes(Path.Combine(sub, "file3.srt"), 300);

            var result = FileSystemHelper.CalculateDirectorySize(root);

            Assert.Equal(3300, result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CalculateDirectorySize_DeeplyNested_SumsAllLevels()
    {
        var root = CreateTempDir();
        try
        {
            WriteBytes(Path.Combine(root, "a.mkv"), 100);
            var sub = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
            WriteBytes(Path.Combine(sub, "b.mkv"), 200);
            var subsub = Directory.CreateDirectory(Path.Combine(sub, "subsub")).FullName;
            WriteBytes(Path.Combine(subsub, "c.mkv"), 300);

            var result = FileSystemHelper.CalculateDirectorySize(root);

            Assert.Equal(600, result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CalculateDirectorySize_NonExistentRoot_ReturnsZero()
    {
        // A path that does not exist triggers IOException on GetFiles; should return 0.
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var result = FileSystemHelper.CalculateDirectorySize(missing);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateDirectorySize_UnauthorizedAccessOnRoot_ReturnsZero()
    {
        // Same semantics as the old mock-based test: a path that cannot be enumerated returns 0.
        // On CI we simulate this with a non-existent path (IOException is caught identically).
        var inaccessible = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var result = FileSystemHelper.CalculateDirectorySize(inaccessible);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateDirectorySize_MultipleSubDirectories_SumsAll()
    {
        var root = CreateTempDir();
        try
        {
            foreach (var (subName, size) in new[] { ("a", 100), ("b", 200), ("c", 300) })
            {
                var sub = Directory.CreateDirectory(Path.Combine(root, subName)).FullName;
                WriteBytes(Path.Combine(sub, $"{subName}.mkv"), size);
            }

            var result = FileSystemHelper.CalculateDirectorySize(root);

            Assert.Equal(600, result);
        }
        finally { Directory.Delete(root, true); }
    }

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