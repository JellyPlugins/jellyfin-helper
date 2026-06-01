using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Unit tests for <see cref="TrashService.RelocateTrashContents"/>.
/// </summary>
public class TrashServiceRelocateTests : IDisposable
{
    private readonly ILogger _logger = TestMockFactory.CreateLogger().Object;
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("TrashRelocate");
    private readonly TrashService _sut = new(TestMockFactory.CreatePluginLogService());

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public void RelocateTrashContents_MovesFilesAndDirectories()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        var newTrash = Path.Combine(_testRoot, "new-trash");
        Directory.CreateDirectory(oldTrash);

        // Create a directory entry
        var dir = Path.Combine(oldTrash, "20260101-120000_MyMovie");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "movie.mkv"), "content");

        // Create a file entry
        File.WriteAllText(Path.Combine(oldTrash, "20260102-100000_subtitle.srt"), "sub");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(2, moved);
        Assert.Equal(0, failed);
        Assert.True(Directory.Exists(Path.Combine(newTrash, "20260101-120000_MyMovie")));
        Assert.True(File.Exists(Path.Combine(newTrash, "20260102-100000_subtitle.srt")));
        // Old trash should be removed (empty after move)
        Assert.False(Directory.Exists(oldTrash));
    }

    [Fact]
    public void RelocateTrashContents_OldPathDoesNotExist_ReturnsZero()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "nonexistent");
        var newTrash = Path.Combine(_testRoot, "new-trash");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void RelocateTrashContents_SamePath_ReturnsZero()
    {
        // Arrange
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);
        File.WriteAllText(Path.Combine(trashPath, "20260101-120000_file.txt"), "data");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(trashPath, trashPath, _logger);

        // Assert
        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
        // Original file should still be there
        Assert.True(File.Exists(Path.Combine(trashPath, "20260101-120000_file.txt")));
    }

    [Fact]
    public void RelocateTrashContents_NewInsideOld_AbortsWithZero()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "trash");
        var newTrash = Path.Combine(oldTrash, "subdir");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "data");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void RelocateTrashContents_OldInsideNew_AbortsWithZero()
    {
        // Arrange
        var newTrash = Path.Combine(_testRoot, "trash");
        var oldTrash = Path.Combine(newTrash, "subdir");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "data");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void RelocateTrashContents_HandlesCollisions()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        var newTrash = Path.Combine(_testRoot, "new-trash");
        Directory.CreateDirectory(oldTrash);
        Directory.CreateDirectory(newTrash);

        // Create same-named file in both old and new
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "from-old");
        File.WriteAllText(Path.Combine(newTrash, "20260101-120000_file.txt"), "already-there");

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(1, moved);
        Assert.Equal(0, failed);
        // Original file in new should still exist
        Assert.True(File.Exists(Path.Combine(newTrash, "20260101-120000_file.txt")));
        // Collision should create a suffixed version
        Assert.True(File.Exists(Path.Combine(newTrash, "20260101-120000_file.txt_2")));
    }

    [Fact]
    public void RelocateTrashContents_EmptyOldTrash_ReturnsZeroAndRemovesFolder()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        var newTrash = Path.Combine(_testRoot, "new-trash");
        Directory.CreateDirectory(oldTrash);

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
        // Empty old folder should be removed
        Assert.False(Directory.Exists(oldTrash));
    }

    [Fact]
    public void RelocateTrashContents_CreatesNewTrashDirectory()
    {
        // Arrange
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        var newTrash = Path.Combine(_testRoot, "brand-new-trash");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "data");

        Assert.False(Directory.Exists(newTrash));

        // Act
        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        // Assert
        Assert.Equal(1, moved);
        Assert.Equal(0, failed);
        Assert.True(Directory.Exists(newTrash));
        Assert.True(File.Exists(Path.Combine(newTrash, "20260101-120000_file.txt")));
    }
}