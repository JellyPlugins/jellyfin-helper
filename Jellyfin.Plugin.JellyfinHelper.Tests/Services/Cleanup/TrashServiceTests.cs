using System.Globalization;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

public class TrashServiceTests : IDisposable
{
    private readonly string _testRoot = TestDataGenerator.CreateTempDirectory("Trash");
    private readonly ILogger _loggerMock = TestMockFactory.CreateLogger().Object;
    private readonly TrashService _trashService = new(TestMockFactory.CreatePluginLogService());

    /// <summary>
    /// Fixed reference time used across all tests for deterministic behavior.
    /// </summary>
    private static readonly DateTime Now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string TimestampFormat = "yyyyMMdd-HHmmss";

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
        var result = _trashService.MoveToTrash(
            Path.Combine(_testRoot, "nonexistent"),
            trashPath,
            _loggerMock);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MoveToTrash_ValidDirectory_MovesAndReturnsSize()
    {
        var sourceDir = Path.Combine(_testRoot, "source_movie");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "movie.mkv"), new byte[1024]);

        var trashPath = Path.Combine(_testRoot, "trash");

        var result = _trashService.MoveToTrash(sourceDir, trashPath, _loggerMock, utcNow: Now);

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
        var result = _trashService.MoveFileToTrash(
            Path.Combine(_testRoot, "nonexistent.srt"),
            trashPath,
            _loggerMock);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MoveFileToTrash_ValidFile_MovesAndReturnsSize()
    {
        var sourceFile = Path.Combine(_testRoot, "subtitle.srt");
        File.WriteAllBytes(sourceFile, new byte[512]);

        var trashPath = Path.Combine(_testRoot, "trash");

        var result = _trashService.MoveFileToTrash(sourceFile, trashPath, _loggerMock, utcNow: Now);

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
        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(
            Path.Combine(_testRoot, "nonexistent_trash"),
            7,
            _loggerMock,
            utcNow: Now);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_NoExpiredItems_ReturnsZero()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create a "fresh" trash item with current timestamp
        var timestamp = Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{timestamp}_RecentMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[100]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredDirectory_PurgesIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an "expired" trash item with old timestamp (10 days before Now)
        var oldTimestamp = Now.AddDays(-10).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var oldDir = Path.Combine(trashPath, $"{oldTimestamp}_OldMovie");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "movie.mkv"), new byte[256]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(256, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void PurgeExpiredTrash_ExpiredFile_PurgesIt()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        var oldTimestamp = Now.AddDays(-15).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var oldFile = Path.Combine(trashPath, $"{oldTimestamp}_old.srt");
        File.WriteAllBytes(oldFile, new byte[128]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(128, bytesFreed);
        Assert.Equal(1, itemsPurged);
        Assert.False(File.Exists(oldFile));
    }

    // ===== Extended PurgeExpiredTrash Tests (Retention Logic) =====

    [Fact]
    public void PurgeExpiredTrash_RetentionDaysZero_PurgesEverything()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // RetentionDays = 0 means cutoff = Now, so everything with timestamp < Now is purged.
        // Item 1 second before Now → gets purged.
        var oldTimestamp = Now.AddSeconds(-1).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var oldDir = Path.Combine(trashPath, $"{oldTimestamp}_OldMovie");
        Directory.CreateDirectory(oldDir);
        File.WriteAllBytes(Path.Combine(oldDir, "movie.mkv"), new byte[300]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 0, _loggerMock, utcNow: Now);

        Assert.Equal(1, itemsPurged);
        Assert.Equal(300, bytesFreed);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void PurgeExpiredTrash_RetentionDaysZero_ItemAtExactCutoff_NotPurged()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Item with timestamp exactly at Now → timestamp is NOT < cutoff (Now) → NOT purged.
        var exactTimestamp = Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var exactDir = Path.Combine(trashPath, $"{exactTimestamp}_ExactMovie");
        Directory.CreateDirectory(exactDir);
        File.WriteAllBytes(Path.Combine(exactDir, "movie.mkv"), new byte[200]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 0, _loggerMock, utcNow: Now);

        Assert.Equal(0, itemsPurged);
        Assert.Equal(0, bytesFreed);
        Assert.True(Directory.Exists(exactDir), "Item at exact cutoff should NOT be purged");
    }

    [Fact]
    public void PurgeExpiredTrash_MixedExpiredAndFresh_OnlyPurgesExpired()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an expired item (15 days before Now)
        var expiredTimestamp = Now.AddDays(-15).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_ExpiredMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[500]);

        // Create a fresh item (1 day before Now)
        var freshTimestamp = Now.AddDays(-1).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[400]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

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

        // Create a valid expired item (10 days before Now)
        var expiredTimestamp = Now.AddDays(-10).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_OldMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[200]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

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

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
    }

    [Fact]
    public void PurgeExpiredTrash_MixedDirectoriesAndFiles_PurgesBothExpired()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Expired directory (20 days before Now)
        var expDirTimestamp = Now.AddDays(-20).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expDirTimestamp}_ExpiredMovie");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "movie.mkv"), new byte[1000]);

        // Expired file (20 days before Now)
        var expFileTimestamp = Now.AddDays(-20).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var expiredFile = Path.Combine(trashPath, $"{expFileTimestamp}_old.srt");
        Directory.CreateDirectory(trashPath);
        File.WriteAllBytes(expiredFile, new byte[500]);

        // Fresh directory (2 days before Now)
        var freshTimestamp = Now.AddDays(-2).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshMovie");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "movie.mkv"), new byte[300]);

        // Fresh file (2 days before Now)
        var freshFileTimestamp = Now.AddDays(-2).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var freshFile = Path.Combine(trashPath, $"{freshFileTimestamp}_new.srt");
        File.WriteAllBytes(freshFile, new byte[200]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

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
        var borderTimestamp = Now.AddDays(-7).AddMinutes(1).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var borderDir = Path.Combine(trashPath, $"{borderTimestamp}_BorderMovie");
        Directory.CreateDirectory(borderDir);
        File.WriteAllBytes(Path.Combine(borderDir, "movie.mkv"), new byte[100]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(0, bytesFreed);
        Assert.Equal(0, itemsPurged);
        Assert.True(Directory.Exists(borderDir), "Item at boundary should not be purged");
    }

    [Fact]
    public void PurgeExpiredTrash_ItemJustPastBoundary_IsPurged()
    {
        var trashPath = Path.Combine(_testRoot, "trash");

        // Create an item 7 days + 1 minute old (should be purged with retentionDays=7)
        var pastTimestamp = Now.AddDays(-7).AddMinutes(-1).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var pastDir = Path.Combine(trashPath, $"{pastTimestamp}_PastMovie");
        Directory.CreateDirectory(pastDir);
        File.WriteAllBytes(Path.Combine(pastDir, "movie.mkv"), new byte[150]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

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

        // Create an item that is definitely expired (retentionDays + 1 day before Now)
        var expiredTimestamp = Now.AddDays(-(retentionDays + 1)).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var expiredDir = Path.Combine(trashPath, $"{expiredTimestamp}_ExpiredItem");
        Directory.CreateDirectory(expiredDir);
        File.WriteAllBytes(Path.Combine(expiredDir, "data.bin"), new byte[100]);

        // Create an item that is definitely fresh (retentionDays - 1 day before Now, min 0)
        var freshAge = Math.Max(retentionDays - 1, 0);
        var freshTimestamp = Now.AddDays(-freshAge).ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var freshDir = Path.Combine(trashPath, $"{freshTimestamp}_FreshItem");
        Directory.CreateDirectory(freshDir);
        File.WriteAllBytes(Path.Combine(freshDir, "data.bin"), new byte[100]);

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, retentionDays, _loggerMock, utcNow: Now);

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

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 0, _loggerMock, utcNow: Now);

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
            var ts = Now.AddDays(-30 - i).ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var dir = Path.Combine(trashPath, $"{ts}_Movie{i}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "video.mkv"), new byte[100]);
        }

        var (bytesFreed, itemsPurged) = _trashService.PurgeExpiredTrash(trashPath, 7, _loggerMock, utcNow: Now);

        Assert.Equal(5, itemsPurged);
        Assert.Equal(500, bytesFreed);
    }

    // ===== GetTrashSummary Tests =====

    [Fact]
    public void GetTrashSummary_NonExistentFolder_ReturnsZero()
    {
        var (totalSize, itemCount) = _trashService.GetTrashSummary(Path.Combine(_testRoot, "nonexistent"));
        Assert.Equal(0, totalSize);
        Assert.Equal(0, itemCount);
    }

    // ===== GetTrashContents Tests =====

    [Fact]
    public void GetTrashContents_NonExistentFolder_ReturnsEmptyList()
    {
        var result = _trashService.GetTrashContents(Path.Combine(_testRoot, "nonexistent"), 30);
        Assert.Empty(result);
    }

    [Fact]
    public void GetTrashContents_EmptyFolder_ReturnsEmptyList()
    {
        var trashPath = Path.Combine(_testRoot, "trash");
        Directory.CreateDirectory(trashPath);

        var result = _trashService.GetTrashContents(trashPath, 30);
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

        var result = _trashService.GetTrashContents(trashPath, 30);

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

        var result = _trashService.GetTrashContents(trashPath, 7);

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

        var result = _trashService.GetTrashContents(trashPath, 30);

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

        var result = _trashService.GetTrashContents(trashPath, 30);

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

        var result = _trashService.GetTrashContents(trashPath, 0);

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

        var (totalSize, itemCount) = _trashService.GetTrashSummary(trashPath);

        Assert.Equal(1500, totalSize);
        Assert.Equal(2, itemCount);
    }
}