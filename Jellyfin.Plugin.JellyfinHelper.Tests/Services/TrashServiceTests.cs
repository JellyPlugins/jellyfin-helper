using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.JellyfinHelper.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

public class TrashServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<ILogger> _loggerMock;

    public TrashServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "JellyfinHelperTests_Trash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _loggerMock = new Mock<ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    // ===== TryParseTrashTimestamp Tests =====

    [Fact]
    public void TryParseTrashTimestamp_ValidFormat_ReturnsTrue()
    {
        var result = TrashService.TryParseTrashTimestamp("20260101-120000_MyMovie", out var timestamp);
        Assert.True(result);
        Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), timestamp);
    }

    [Fact]
    public void TryParseTrashTimestamp_InvalidFormat_ReturnsFalse()
    {
        var result = TrashService.TryParseTrashTimestamp("not-a-timestamp_MyMovie", out _);
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void TryParseTrashTimestamp_EmptyOrShort_ReturnsFalse(string? input)
    {
        var result = TrashService.TryParseTrashTimestamp(input!, out _);
        Assert.False(result);
    }

    // ===== MoveToTrash Tests =====

    [Fact]
    public void MoveToTrash_NonExistentSource_ReturnsZero()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        var result = TrashService.MoveToTrash(
            Path.Combine(_testRoot, "nonexistent"),
            trashPath,
            _loggerMock.Object);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MoveToTrash_ValidDirectory_MovesAndReturnsSize()
    {
        var sourceDir = Path.Combine(_testRoot, "source_movie");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "movie.mkv"), new byte[1024]);

        var trashPath = Path.Combine(_testRoot, "trash");

        var result = TrashService.MoveToTrash(sourceDir, trashPath, _loggerMock.Object);

        Assert.Equal(1024, result);
        Assert.False(Directory.Exists(sourceDir));
        Assert.True(Directory.Exists(trashPath));

        // Verify trash folder contains one timestamped directory
        var trashDirs = Directory.GetDirectories(trashPath);
        Assert.Single(trashDirs);
        Assert.Contains("source_movie", Path.GetFileName(trashDirs[0]));
    }

    // ===== MoveFileToTrash Tests =====

    [Fact]
    public void MoveFileToTrash_NonExistentFile_ReturnsZero()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        var result = TrashService.MoveFileToTrash(
            Path.Combine(_testRoot, "nonexistent.srt"),
            trashPath,
            _loggerMock.Object);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MoveFileToTrash_ValidFile_MovesAndReturnsSize()
    {
        var sourceFile = Path.Combine(_testRoot, "subtitle.srt");
        File.WriteAllBytes(sourceFile, new byte[512]);

        var trashPath = Path.Combine(_testRoot, "trash");

        var result = TrashService.MoveFileToTrash(sourceFile, trashPath, _loggerMock.Object);

        Assert.Equal(512, result);
        Assert.False(File.Exists(sourceFile));
        Assert.True(Directory.Exists(trashPath));

        var trashFiles = Directory.GetFiles(trashPath);
        Assert.Single(trashFiles);
        Assert.Contains("subtitle.srt", Path.GetFileName(trashFiles[0]));
    }

    // ===== PurgeExpiredTrash Tests =====

    [Fact]
    public void PurgeExpiredTrash_NonExistentTrashFolder_ReturnsZero()
    {
        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(
            Path.Combine(_testRoot, "nonexistent_trash"),
            7,
            _loggerMock.Object);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_NoExpiredItems_ReturnsZero()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create a "fresh" trash item with current timestamp
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{timestamp}_RecentMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[100]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredDirectory_PurgesIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an "expired" trash item with old timestamp
        var oldTimestamp = DateTime.UtcNow.AddDays(-10).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var oldDir = Path.Combine(trashPath, $"{oldTimestamp}_OldMovie");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "movie.mkv"), new byte[256]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(256, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredFile_PurgesIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        var oldTimestamp = DateTime.UtcNow.AddDays(-15).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var oldFile = Path.Combine(trashPath, $"{oldTimestamp}_old.srt");
        File.WriteAllBytes(oldFile, new byte[128]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(128, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(File.Exists(oldFile));
    }

    // ===== Extended PurgeExpiredTrash Tests (Retention Logic) =====

    [Fact]
    public void PurgeExpiredTrash_RetentionDaysZero_PurgesEverything()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create a "just now" item
        var nowTimestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var nowDir = Path.Combine(trashPath, $"{nowTimestamp}_JustNowMovie");
        Directory.CreateDirectory(nowDir);
        File.WriteAllBytes(Path.Combine(nowDir, "movie.mkv"), new byte[200]);

        // RetentionDays = 0 means cutoff = DateTime.UtcNow, so everything older than "now" is purged
        // Items created at the same second may or may not be purged depending on timing,
        // but items even 1 second old should be. Let's use an old item to be deterministic.
        var oldTimestamp = DateTime.UtcNow.AddSeconds(-2).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var oldDir = Path.Combine(trashPath, $"{oldTimestamp}_OldMovie");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "movie.mkv"), new byte[300]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 0, _loggerMock.Object);

        // At least the old item should be purged
        Assert.True(itemsPurged >= 1, $"Expected at least 1 purged item, got {itemsPurged}");
        Assert.True(bytesFreed >= 300, $"Expected at least 300 bytes freed, got {bytesFreed}");
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void PurgeExpiredTrash_MixedExpiredAndFresh_OnlyPurgesExpired()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an expired item (15 days old)
        var expiredTimestamp = DateTime.UtcNow.AddDays(-15).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_ExpiredMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[500]);

        // Create a fresh item (1 day old)
        var freshTimestamp = DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[400]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(500, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(Directory.Exists(expiredDir), "Expired directory should be deleted");
        Assert.True(Directory.Exists(freshDir), "Fresh directory should still exist");
    }

    [Fact]
    public void PurgeExpiredTrash_ItemWithoutValidTimestamp_SkipsIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an item without a valid timestamp prefix
        var invalidDir = Path.Combine(trashPath, "no-timestamp_SomeMovie");
        Directory.CreateDirectory(invalidDir);
        File.WriteAllBytes(Path.Combine(invalidDir, "movie.mkv"), new byte[100]);

        // Create a valid expired item
        var expiredTimestamp = DateTime.UtcNow.AddDays(-10).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_OldMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[200]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(200, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.True(Directory.Exists(invalidDir), "Directory without valid timestamp should be skipped");
        Assert.False(Directory.Exists(expiredDir), "Expired directory should be deleted");
    }

    [Fact]
    public void PurgeExpiredTrash_EmptyTrashFolder_ReturnsZero()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_MixedDirectoriesAndFiles_PurgesBothExpired()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Expired directory (20 days old)
        var expDirTimestamp = DateTime.UtcNow.AddDays(-20).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expDirTimestamp}_ExpiredMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[1000]);

        // Expired file (20 days old)
        var expFileTimestamp = DateTime.UtcNow.AddDays(-20).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var expiredFile = Path.Combine(trashPath, $"{expFileTimestamp}_old.srt");
        Directory.CreateDirectory(trashPath);
        File.WriteAllBytes(expiredFile, new byte[500]);

        // Fresh directory (2 days old)
        var freshTimestamp = DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[300]);

        // Fresh file (2 days old)
        var freshFileTimestamp = DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var freshFile = Path.Combine(trashPath, $"{freshFileTimestamp}_new.srt");
        File.WriteAllBytes(freshFile, new byte[200]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(1500, bytesFreed);
        Assert.Equal(2, itemsPurged);
        Assert.False(Directory.Exists(expiredDir));
        Assert.False(File.Exists(expiredFile));
        Assert.True(Directory.Exists(freshDir));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public void PurgeExpiredTrash_ItemExactlyAtBoundary_IsNotPurged()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an item exactly 7 days minus 1 minute old (should NOT be purged with retentionDays=7)
        var borderTimestamp = DateTime.UtcNow.AddDays(-7).AddMinutes(1).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var borderDir = Path.Combine(trashPath, $"{borderTimestamp}_BorderMovie");
        Directory.CreateDirectory(borderDir);
        File.WriteAllBytes(Path.Combine(borderDir, "movie.mkv"), new byte[100]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
        Assert.True(Directory.Exists(borderDir), "Item at boundary should not be purged");
    }

    [Fact]
    public void PurgeExpiredTrash_ItemJustPastBoundary_IsPurged()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an item 7 days + 1 minute old (should be purged with retentionDays=7)
        var pastTimestamp = DateTime.UtcNow.AddDays(-7).AddMinutes(-1).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var pastDir = Path.Combine(trashPath, $"{pastTimestamp}_PastMovie");
        Directory.CreateDirectory(pastDir);
        File.WriteAllBytes(Path.Combine(pastDir, "movie.mkv"), new byte[150]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(150, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(Directory.Exists(pastDir), "Item past boundary should be purged");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void PurgeExpiredTrash_VariousRetentionDays_RespectsConfiguration(int retentionDays)
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an item that is definitely expired (retentionDays + 1 day old)
        var expiredTimestamp = DateTime.UtcNow.AddDays(-(retentionDays + 1)).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_ExpiredItem");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "data.bin"), new byte[100]);

        // Create an item that is definitely fresh (retentionDays - 1 day old, min 0)
        var freshAge = Math.Max(retentionDays - 1, 0);
        var freshTimestamp = DateTime.UtcNow.AddDays(-freshAge).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshItem");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "data.bin"), new byte[100]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, retentionDays, _loggerMock.Object);

        Assert.Equal(1, itemsPurged);
        Assert.Equal(100, bytesFreed);
        Assert.False(Directory.Exists(expiredDir), $"Item older than {retentionDays} days should be purged");
        Assert.True(Directory.Exists(freshDir), $"Item younger than {retentionDays} days should remain");
    }

    [Fact]
    public void PurgeExpiredTrash_FileWithoutTimestamp_SkipsIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        // Create a file without a valid timestamp prefix
        var invalidFile = Path.Combine(trashPath, "random-file.txt");
        File.WriteAllBytes(invalidFile, new byte[50]);

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 0, _loggerMock.Object);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
        Assert.True(File.Exists(invalidFile), "File without valid timestamp should be skipped");
    }

    [Fact]
    public void PurgeExpiredTrash_MultipleExpiredItems_PurgesAll()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        for (int i = 0; i < 5; i++)
        {
            var ts = DateTime.UtcNow.AddDays(-30 - i).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.Combine(trashPath, $"{ts}_Movie{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "video.mkv"), new byte[100]);
        }

        var (bytesFreed, itemsPurged) = TrashService.PurgeExpiredTrash(trashPath, 7, _loggerMock.Object);

        Assert.Equal(5, itemsPurged);
        Assert.Equal(500, bytesFreed);
    }

    // ===== GetTrashSummary Tests =====

    [Fact]
    public void GetTrashSummary_NonExistentFolder_ReturnsZero()
    {
        var (totalSize, itemCount) = TrashService.GetTrashSummary(Path.Combine(_testRoot, "nonexistent"));
        Assert.Equal(0, totalSize);
        Assert.Equal(0, itemCount);
    }

    // ===== GetTrashContents Tests =====

    [Fact]
    public void GetTrashContents_NonExistentFolder_ReturnsEmptyList()
    {
        var result = TrashService.GetTrashContents(Path.Combine(_testRoot, "nonexistent"), 30);
        Assert.Empty(result);
    }

    [Fact]
    public void GetTrashContents_EmptyFolder_ReturnsEmptyList()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        var result = TrashService.GetTrashContents(trashPath, 30);
        Assert.Empty(result);
    }

    [Fact]
    public void GetTrashContents_WithDirectoryItems_ReturnsCorrectInfo()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        var timestamp = "20260315-140000";
        var dirName = $"{timestamp}_MyMovie";
        var dir = Path.Combine(trashPath, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "movie.mkv"), new byte[2048]);

        var result = TrashService.GetTrashContents(trashPath, 30);

        Assert.Single(result);
        var item = result[0];
        Assert.Equal("MyMovie", item.Name);
        Assert.Equal(dirName, item.FullName);
        Assert.Equal(2048, item.Size);
        Assert.True(item.IsDirectory);
        Assert.NotNull(item.TrashedAt);
        Assert.Equal(new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc), item.TrashedAt.Value);
        Assert.NotNull(item.PurgesAt);
        Assert.Equal(new DateTime(2026, 4, 14, 14, 0, 0, DateTimeKind.Utc), item.PurgesAt.Value);
    }

    [Fact]
    public void GetTrashContents_WithFileItems_ReturnsCorrectInfo()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);
        var timestamp = "20260601-100000";
        var fileName = $"{timestamp}_subtitle.srt";
        File.WriteAllBytes(Path.Combine(trashPath, fileName), new byte[512]);

        var result = TrashService.GetTrashContents(trashPath, 7);

        Assert.Single(result);
        var item = result[0];
        Assert.Equal("subtitle.srt", item.Name);
        Assert.Equal(fileName, item.FullName);
        Assert.Equal(512, item.Size);
        Assert.False(item.IsDirectory);
        Assert.NotNull(item.TrashedAt);
        Assert.Equal(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), item.TrashedAt.Value);
        Assert.NotNull(item.PurgesAt);
        Assert.Equal(new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc), item.PurgesAt.Value);
    }

    [Fact]
    public void GetTrashContents_MixedItems_SortedByDateDescending()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Older item
        var olderDir = Path.Combine(trashPath, "20260101-100000_OldMovie");
        Directory.CreateDirectory(olderDir);
        File.WriteAllBytes(Path.Combine(olderDir, "m.mkv"), new byte[100]);

        // Newer item
        var newerDir = Path.Combine(trashPath, "20260601-100000_NewMovie");
        Directory.CreateDirectory(newerDir);
        File.WriteAllBytes(Path.Combine(newerDir, "m.mkv"), new byte[200]);

        // File item in between
        Directory.CreateDirectory(trashPath); // ensure exists
        File.WriteAllBytes(Path.Combine(trashPath, "20260301-100000_mid.srt"), new byte[50]);

        var result = TrashService.GetTrashContents(trashPath, 30);

        Assert.Equal(3, result.Count);
        Assert.Equal("NewMovie", result[0].Name);
        Assert.Equal("mid.srt", result[1].Name);
        Assert.Equal("OldMovie", result[2].Name);
    }

    [Fact]
    public void GetTrashContents_ItemWithoutTimestamp_HasNullDates()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        var invalidDir = Path.Combine(trashPath, "no-timestamp-folder");
        Directory.CreateDirectory(invalidDir);
        File.WriteAllBytes(Path.Combine(invalidDir, "data.bin"), new byte[100]);

        var result = TrashService.GetTrashContents(trashPath, 30);

        Assert.Single(result);
        var item = result[0];
        Assert.Equal("no-timestamp-folder", item.Name);
        Assert.True(item.IsDirectory);
        Assert.Null(item.TrashedAt);
        Assert.Null(item.PurgesAt);
    }

    [Fact]
    public void GetTrashContents_RetentionDaysZero_PurgeDateEqualsTrashDate()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);
        File.WriteAllBytes(Path.Combine(trashPath, "20260101-120000_test.txt"), new byte[10]);

        var result = TrashService.GetTrashContents(trashPath, 0);

        Assert.Single(result);
        Assert.Equal(result[0].TrashedAt, result[0].PurgesAt);
    }

    // ===== ExtractOriginalName Tests =====

    [Theory]
    [InlineData("20260101-120000_MyMovie", "MyMovie")]
    [InlineData("20260315-140000_subtitle.srt", "subtitle.srt")]
    [InlineData("20260601-100000_Movie With Spaces", "Movie With Spaces")]
    public void ExtractOriginalName_ValidTimestamp_ReturnsOriginalName(string input, string expected)
    {
        var result = TrashService.ExtractOriginalName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("no-timestamp-here")]
    [InlineData("short")]
    [InlineData("")]
    public void ExtractOriginalName_InvalidTimestamp_ReturnsFullName(string input)
    {
        var result = TrashService.ExtractOriginalName(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ExtractOriginalName_NullInput_ReturnsNull()
    {
        var result = TrashService.ExtractOriginalName(null!);
        Assert.Null(result);
    }

    [Fact]
    public void GetTrashSummary_WithItems_ReturnsSizeAndCount()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Directory item
        var dir1 = Path.Combine(trashPath, "20260101-120000_Movie1");
        Directory.CreateDirectory(dir1);
        File.WriteAllBytes(Path.Combine(dir1, "movie.mkv"), new byte[1000]);

        // File item
        var file1 = Path.Combine(trashPath, "20260101-130000_sub.srt");
        File.WriteAllBytes(file1, new byte[500]);

        var (totalSize, itemCount) = TrashService.GetTrashSummary(trashPath);

        Assert.Equal(1500, totalSize);
        Assert.Equal(2, itemCount);
    }
}