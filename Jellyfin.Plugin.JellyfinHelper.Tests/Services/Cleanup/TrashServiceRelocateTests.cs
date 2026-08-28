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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(_testRoot))
                {
                    return;
                }

                Directory.Delete(_testRoot, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2 || !Directory.Exists(_testRoot))
                {
                    return;
                }

                Thread.Sleep(50);
            }
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

    [Fact]
    public void RelocateTrashContents_MalformedPath_ReturnsZeroWithoutThrowing()
    {
        // oldTrash exists so the Directory.Exists guard passes and control reaches the normalize block; an embedded null char makes Path.GetFullPath(newTrash) throw ArgumentException, which must be caught and reported as a no-op relocation.
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "data");

        var malformedNew = Path.Combine(_testRoot, "bad\0path");

        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, malformedNew, _logger);

        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
        // Old contents must remain intact after the failed normalize.
        Assert.True(File.Exists(Path.Combine(oldTrash, "20260101-120000_file.txt")));
    }

    [Fact]
    public void RelocateTrashContents_NewPathBlockedByExistingFile_ReturnsZero()
    {
        // A file occupying the exact newTrashPath location makes Directory.CreateDirectory
        // throw IOException; the method must abort cleanly without moving anything.
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        Directory.CreateDirectory(oldTrash);
        File.WriteAllText(Path.Combine(oldTrash, "20260101-120000_file.txt"), "data");

        var newTrash = Path.Combine(_testRoot, "blocked-trash");
        File.WriteAllText(newTrash, "i-am-a-file-not-a-dir");

        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        Assert.Equal(0, moved);
        Assert.Equal(0, failed);
        // Old entry stays put and the blocking file is untouched.
        Assert.True(File.Exists(Path.Combine(oldTrash, "20260101-120000_file.txt")));
        Assert.True(File.Exists(newTrash));
        Assert.False(Directory.Exists(newTrash));
    }

    [Fact]
    public void RelocateTrashContents_AllEntriesMoved_RemovesEmptiedOldTrash()
    {
        // After every entry moves out, the old trash directory is left empty and must be
        // removed, while the success path still reports the full moved count.
        var oldTrash = Path.Combine(_testRoot, "old-trash");
        var newTrash = Path.Combine(_testRoot, "new-trash");
        Directory.CreateDirectory(oldTrash);

        var dir = Path.Combine(oldTrash, "20260101-120000_MyMovie");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "movie.mkv"), "content");
        File.WriteAllText(Path.Combine(oldTrash, "20260102-100000_subtitle.srt"), "sub");

        var (moved, failed) = _sut.RelocateTrashContents(oldTrash, newTrash, _logger);

        Assert.Equal(2, moved);
        Assert.Equal(0, failed);
        Assert.False(Directory.Exists(oldTrash), "Emptied old trash folder must be removed");
        Assert.True(Directory.Exists(Path.Combine(newTrash, "20260101-120000_MyMovie")));
        Assert.True(File.Exists(Path.Combine(newTrash, "20260102-100000_subtitle.srt")));
    }
}